using Microsoft.Extensions.Logging;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Patching;
using PoeMtxChanger.Core.Profiles;

namespace PoeMtxChanger.App.Services;

/// <summary>Apply / restore / assign operations shared by several screens. Every file change asks for confirmation first.</summary>
public sealed class ProfileActions(AppServices app)
{
    private readonly ILogger _log = app.Log("Actions");

    /// <summary>Picks the resource of <paramref name="item"/> that can replace <paramref name="targetPath"/> (same extension).</summary>
    public static string? MatchSource(CosmeticItem item, string targetPath)
    {
        var ext = Path.GetExtension(targetPath);
        var candidates = item.PrimaryResources
            .Where(r => string.Equals(Path.GetExtension(r), ext, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// Puts a cosmetic into a profile entry and re-targets its existing targets (per class / variant) to the new cosmetic.
    /// For the portal slot with no targets yet, the default town portal is used automatically.
    /// </summary>
    public void Assign(ProfileEntry entry, CosmeticItem item)
    {
        entry.CosmeticId = item.Id;
        entry.CosmeticName = item.DisplayName;
        TargetSuggestion? target = null;
        if (entry.Mappings.Count > 0)
        {
            target = app.Game.Suggestions.FirstOrDefault(s => (s.SlotKey == entry.Key || entry.Key == "weapon_off" && s.SlotKey == "weapon_main")
                && (entry.Mappings[0].VisualIdentityEdit is { } visual && s.VisualIdentityRow == visual.Row
                    || s.Path.Equals(entry.Mappings[0].TargetPath, StringComparison.OrdinalIgnoreCase)
                    || s.ClassVariants.Values.Contains(entry.Mappings[0].TargetPath, StringComparer.OrdinalIgnoreCase)
                    || s.ResourceVariants.Values.Contains(entry.Mappings[0].TargetPath, StringComparer.OrdinalIgnoreCase)));
            var variants = entry.Mappings.Where(m => m.Note != null).DistinctBy(m => m.Note).ToDictionary(m => m.Note!, m => m.TargetPath);
            target ??= new TargetSuggestion(entry.Key, "", entry.Mappings[0].TargetPath, "profile") { ClassVariants = variants };
        }
        else if (entry.Key == "portal")
            target = app.Game.Suggestions.FirstOrDefault(s => s.SlotKey == "portal" && s.ClassVariants.Count > 0);
        entry.Mappings.Clear();
        entry.Notes.Clear();
        if (target is null) return;
        var plan = SkinMappingResolver.Resolve(target, null, item, app.Game.Resources);
        entry.Mappings.AddRange(plan.Mappings);
        entry.Notes.AddRange(plan.Notes);
    }

    /// <summary>Returns a user-facing message.</summary>
    public string AddToActiveProfile(CosmeticItem item)
    {
        var profile = app.Profiles.Active;
        if (profile is null) return "No active profile.";
        if(item.IsWeaponEffect)return "Open “Wardrobe” and click “Weapon effect” under the hand you want.";
        var slot = ViewModels.Ui.SlotFor(item.Category);
        if (slot is null)
            return item.Category == CosmeticCategory.SkillEffect
                ? "Skill effects are assigned to a specific skill: “My Sets” → “Skill effects”."
                : "This category cannot be assigned to a slot.";
        var entry = profile.GetOrAddEntry(ProfileEntryKind.EquipmentSlot, slot);
        Assign(entry, item);
        app.Profiles.SaveActive();
        _log.LogInformation("Assigned {Item} to slot {Slot} in profile {Profile}", item.Id, slot, profile.Name);
        return entry.Mappings.Count == 0
            ? $"“{item.DisplayName}” was added to profile “{profile.Name}”. In “My Sets”, choose which base item it is worn over."
            : $"“{item.DisplayName}” was added to profile “{profile.Name}” ({entry.Mappings.Count} replacements).";
    }

    public ValidationReport? Validate(CosmeticProfile profile)
    {
        if (app.Game.Resources is not { } src) return null;
        var report = new MappingValidator(ResourcePolicy.Default).Validate(profile.ActiveMappings(), src, app.Settings.ValidateResources);
        foreach (var entry in profile.Entries.Where(e => e.Enabled && e.CosmeticId != null && e.Mappings.Count == 0))
            report.Add(IssueSeverity.Error, "INCOMPLETE_SLOT", "No replacement set up: " + (entry.CosmeticName ?? entry.Key));
        foreach(var entry in profile.Entries.Where(e=>e.Enabled&&(e.BuffSelection!=null||e.Key.StartsWith("buff-visual:",StringComparison.Ordinal))))
        {
            var selected=entry.BuffSelection;
            var skill=app.Game.BuffSkills.FirstOrDefault(s=>s.Id==selected?.SkillId);
            var original=app.Game.BuffVisuals.FirstOrDefault(v=>v.Id==selected?.OriginalId);
            var donor=app.Game.BuffVisuals.FirstOrDefault(v=>v.Id==selected?.DonorId);
            if(skill==null||original==null||donor==null)
            { report.Add(IssueSeverity.Error,"BUFF_COMPAT","For the aura, choose the gem and effect again in the “Auras” window: "+entry.Key);continue; }
            var current=BuffVisualCatalogBuilder.Plan(original,donor,src,skill);
            string Key(Core.Mapping.ReplacementMapping m)=>m.Normalized().TargetPath+"|"+m.Normalized().SourcePath;
            if(!current.CanApply||!entry.Mappings.Select(Key).Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(current.Mappings.Select(Key).Order(StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase))
                report.Add(IssueSeverity.Error,"BUFF_COMPAT","The aura plan does not match the selected gem or the game's current links. Choose the effect again: "+entry.Key);
        }
        return report;
    }

    public async Task ApplyActiveAsync()
    {
        var profile = app.Profiles.Active;
        var engine = app.Game.CreateEngine();
        if (profile is null) return;
        if (app.Game.CatalogIsMock)
        {
            app.Dialogs.Info("Demo mode", "Demo data (MOCK) is shown. It cannot be applied to the game.");
            return;
        }
        if (engine is null || !app.Game.Codec.IsAvailable)
        {
            app.Dialogs.Error("Applying is unavailable",
                "A detected game installation and the Oodle library are required (Settings → Oodle).");
            return;
        }
        var mappings = profile.ActiveMappings();
        var unfinished = profile.Entries.Where(e => e.Enabled && e.CosmeticId != null && e.Mappings.Count == 0).ToList();
        if (unfinished.Count > 0)
        {
            app.Dialogs.Error("The set is not ready", "No compatible replacement is set up for part of the selected cosmetics:\n" +
                string.Join("\n", unfinished.Select(e => "• " + (e.CosmeticName ?? e.Key))) +
                "\n\nOpen “My Sets” and choose targets or remove these items. Part of the set will not be applied.");
            return;
        }
        if (mappings.Count == 0)
        {
            app.Dialogs.Info("Nothing to apply", "The active profile has no replacements with a target resource.");
            return;
        }

        ValidationReport? checkedPlan = null;
        await app.Game.RunBusy("Checking the whole set…", async () => checkedPlan = await Task.Run(() => Validate(profile)));
        if (checkedPlan is null || checkedPlan.HasErrors)
        {
            app.Dialogs.Error("The set failed the check", checkedPlan is null ? "A loaded resource index is required."
                : string.Join("\n", checkedPlan.Issues.Where(i => i.Severity == IssueSeverity.Error).Take(10).Select(i => "• " + i.Message)));
            return;
        }

        var preview = string.Join("\n", mappings.Take(8).Select(m => $"• {m.TargetPath}" +
            (m.VisualIdentityEdit is { } visual ? $" [row {visual.Row}, {visual.Column}]" : "") + $"\n   ← {m.SourcePath}"));
        if (mappings.Count > 8) preview += $"\n… and {mappings.Count - 8} more";
        var notes = profile.Entries.Where(e => e.Enabled).SelectMany(e => e.Notes).Distinct().ToList();
        if (notes.Count > 0) preview += "\n\nLimitations:\n" + string.Join("\n", notes.Take(8).Select(note => "• " + note));
        preview += "\n\nReplacing shared files affects every object that uses them in this client.";
        var ok = app.Dialogs.Confirm("Change the game files?",
            $"Profile “{profile.Name}”: {mappings.Count} replacements.\n\n{preview}\n\n" +
            (app.Game.Installation?.UsesGgpk == true ? "A full backup of Content.ggpk will be made and a new archive built next to the original. Free space for two full copies of the archive is required.\n" : "Bundles2\\_.index.bin will be changed (a verified backup is made first) and a file will be added to Bundles2\\PoeMtxChanger\\.\n") +
            "The game must be closed. Changing client files may violate GGG's terms of use; " +
            "account safety is not guaranteed.\n\nContinue?");
        if (!ok) return;

        await app.Game.RunBusy("Applying the profile…", async () =>
        {
            try
            {
                var result = await engine.ApplyAsync(new PatchRequest
                {
                    Mappings = mappings,
                    ProfileName = profile.Name,
                    UserConfirmed = true,
                    AnalyzeDependencies = app.Settings.ValidateResources,
                    Compressor = app.Settings.Compressor,
                    Level = app.Settings.CompressionLevel,
                });
                var warnings = result.Report.Issues.Count(i => i.Severity == IssueSeverity.Warning);
                app.Dialogs.Info("Done", $"Files replaced: {result.ReplacedFiles}. Warnings: {warnings}.\n" +
                    "Checked by hash before writing. The program does not check how it looks in game: launch the game and see for yourself.");
            }
            catch (ValidationFailedException vex)
            {
                app.Dialogs.Error("Check failed", string.Join("\n", vex.Report.Issues.Where(i => i.Severity == IssueSeverity.Error)
                    .Take(12).Select(i => $"• {i.Message}{(i.Path is null ? "" : " — " + i.Path)}")));
            }
            catch (PatchException pex)
            {
                app.Dialogs.Error("Not applied", pex.Message);
            }
            await app.Game.InspectAsync();
        });
    }

    public async Task RestoreAsync()
    {
        var engine = app.Game.CreateEngine();
        if (engine is null) { app.Dialogs.Info("Restore", "Game installation not found."); return; }
        if (!app.Dialogs.Confirm("Restore the originals?",
                (app.Game.Installation?.UsesGgpk == true ? "Content.ggpk will be fully restored from the verified backup.\n" : "The original _.index.bin will be restored from the backup (SHA-256 checked), and this program's files in Bundles2\\PoeMtxChanger\\ will be deleted.\n") + "The game must be closed. Continue?"))
            return;
        await app.Game.RunBusy("Restoring…", async () =>
        {
            try
            {
                var r = await engine.RestoreAsync(userConfirmed: true);
                app.Dialogs.Info("Restore", r.Message + (r.BundlesDeleted > 0 ? $"\nFiles deleted: {r.BundlesDeleted}." : ""));
            }
            catch (PatchException ex) { app.Dialogs.Error("Restore failed", ex.Message); }
            await app.Game.InspectAsync();
        });
    }

    public async Task AdoptBaselineAsync()
    {
        var engine = app.Game.CreateEngine();
        if (engine is null) return;
        if (!app.Dialogs.Confirm("New backup",
                "Use the current game files as the new original? Only do this after a game update or a Steam file check, when the files are definitely clean."))
            return;
        await app.Game.RunBusy("Creating the backup…", async () =>
        {
            try { await Task.Run(() => engine.AdoptCurrentAsBaseline(userConfirmed: true)); }
            catch (PatchException ex) { app.Dialogs.Error("Backup not created", ex.Message); }
            await app.Game.InspectAsync();
        });
    }
}
