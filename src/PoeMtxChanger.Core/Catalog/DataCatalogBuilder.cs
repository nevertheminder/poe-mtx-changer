using PoeMtxChanger.Core.Formats.Dat;
using PoeMtxChanger.Core.Resources;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Catalog;

public sealed record TableDiagnostic(string Table, bool Ok, string Message);

public sealed class DataCatalogResult
{
    public required IReadOnlyList<CosmeticItem> Items { get; init; }
    public required IReadOnlyList<TargetSuggestion> Suggestions { get; init; }
    public required IReadOnlyList<TableDiagnostic> Diagnostics { get; init; }
    public IReadOnlyList<SkillVisualDefinition> Skills { get; init; } = [];
    public IReadOnlyList<BuffVisualDefinition> Buffs { get; init; } = [];
    public IReadOnlyList<BuffSkillDefinition> BuffSkills { get; init; } = [];
    public bool HasMtxNames => Diagnostics.Any(d => d.Table == "MtxTypes" && d.Ok);
}

/// <summary>
/// Builds the catalog from the game's own data tables (read-only). Links verified against a real PoE 1
/// install (build 26694821, 2026-11-17):
///   MtxTypes (Name, Type) ← MtxTypeGameSpecific (Type, DDSFile icon)
///   ItemisedVisualEffect (Mtx → MtxTypeGameSpecific) → ItemVisualIdentity (per-class worn files) / ItemVisualEffect (weapon .epk)
///   MicrotransactionPortalVariations, Pet, Footprints for portals, pets and footprints.
/// Base items (BaseItemTypes → ItemVisualIdentity) become per-class replacement targets for equipment slots.
/// </summary>
public sealed class DataCatalogBuilder(IResourceSource source, DatSchema schema, CatalogRules rules)
{
    private readonly List<TableDiagnostic> _diag = [];
    private readonly Dictionary<string, DatTable?> _tables = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string[]> SlotItemClasses = new()
    {
        ["body"] = ["Body Armour"],
        ["helmet"] = ["Helmet"],
        ["gloves"] = ["Gloves"],
        ["boots"] = ["Boots"],
        ["shield"] = ["Shield"],
        ["weapon_main"] = ["One Hand Sword", "Thrusting One Hand Sword", "One Hand Axe", "One Hand Mace", "Sceptre", "Dagger", "Rune Dagger",
                           "Claw", "Wand", "Bow", "Staff", "Warstaff", "Two Hand Sword", "Two Hand Axe", "Two Hand Mace"],
    };

    /// <summary>Default town portal objects (both use the "townportal" rig in the real data, build 26694821).</summary>
    public static readonly string[] DefaultPortalFiles = ["Metadata/Effects/Spells/portal/portal.ao", "Metadata/Effects/Spells/portal/portal2.ao"];

    /// <summary>MtxTypes.Type → category (values observed in the real table).</summary>
    private static readonly Dictionary<string, CosmeticCategory> TypeCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Helmet Skin"] = CosmeticCategory.Helmet,
        ["Helmet Attachment"] = CosmeticCategory.Helmet,
        ["Body Armour Skin"] = CosmeticCategory.BodyArmour,
        ["Body Armour Attachment"] = CosmeticCategory.WingsAndBack,
        ["Gloves Skin"] = CosmeticCategory.Gloves,
        ["Boots Skin"] = CosmeticCategory.Boots,
        ["Shield Skin"] = CosmeticCategory.Shield,
        ["Weapon Skin"] = CosmeticCategory.Weapon,
        ["Sword Skin"] = CosmeticCategory.Weapon,
        ["Weapon Effect"] = CosmeticCategory.Weapon,
        ["Portal Modification"] = CosmeticCategory.Portal,
        ["Character Effect"] = CosmeticCategory.CharacterEffect,
        ["Apparition Effect"] = CosmeticCategory.CharacterEffect,
        ["Finisher Effect"] = CosmeticCategory.CharacterEffect,
        ["Extra Effect"] = CosmeticCategory.CharacterEffect,
        ["Footprints Effect"] = CosmeticCategory.FootprintsAndTrails,
        ["Aura Skin"] = CosmeticCategory.AuraAndHerald,
        ["Herald Skin"] = CosmeticCategory.AuraAndHerald,
    };

    private DatTable? Table(string name)
    {
        if (_tables.TryGetValue(name, out var t)) return t;
        t = null;
        if (!schema.Tables.TryGetValue(name, out var ts)) _diag.Add(new(name, false, "not in schema"));
        else if (!source.Exists(ts.GamePath)) _diag.Add(new(name, false, $"{ts.GamePath} not in index"));
        else
        {
            try
            {
                t = DatTable.Parse(source.Read(ts.GamePath), ts);
                _diag.Add(new(name, true, $"{t.RowCount} rows"));
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or KeyNotFoundException or IOException or InvalidOperationException or ArgumentException)
            {
                _diag.Add(new(name, false, ex.Message));
            }
        }
        _tables[name] = t;
        return t;
    }

    private static string S(DatTable t, int row, string col) => t.HasColumn(col) ? t.GetString(row, col) : "";
    private static IReadOnlyList<string> SA(DatTable t, int row, string col) => t.HasColumn(col) ? t.GetStringArray(row, col) : [];
    private static int? R(DatTable t, int row, string col) => t.HasColumn(col) ? t.GetRowRef(row, col) : null;

    private bool Has(string p) => !string.IsNullOrWhiteSpace(p) && source.Exists(p);
    private List<string> Existing(IEnumerable<string> paths) => paths
        .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Replace('\\', '/').Trim())
        .Where(source.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private sealed class Draft
    {
        public required string Id;
        public required string Name;
        public string? Icon;
        public string? HeldWeaponPath;
        public string Type = "";
        public CosmeticCategory? Forced;
        public readonly List<string> Resources = [];
        public readonly Dictionary<string, string> Classes = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Roles = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ItemClasses = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> MappingIssues = [];
        public readonly List<string> Links = [];
    }

    public DataCatalogResult Build(CancellationToken ct = default)
    {
        var drafts = new Dictionary<int, Draft>(); // keyed by MtxTypes row
        var extra = new List<Draft>();
        var suggestions = new List<TargetSuggestion>();

        // 1. Names and types. "[DNT]" rows are internal test entries and are skipped.
        var mtx = Table("MtxTypes");
        if (mtx != null)
            for (int r = 0; r < mtx.RowCount; r++)
            {
                var name = S(mtx, r, "Name").Trim();
                if (name.Length == 0 || IsInternal(name)) continue;
                drafts[r] = new Draft { Id = "mtx:" + S(mtx, r, "Id"), Name = name, Type = S(mtx, r, "Type").Trim() };
            }

        // 2. Game-specific rows → MtxTypes row, icon
        var gs = Table("MtxTypeGameSpecific");
        var gsToMtx = new Dictionary<int, int>();
        if (gs != null)
            for (int r = 0; r < gs.RowCount; r++)
                if (R(gs, r, "Type") is int m && drafts.TryGetValue(m, out var d))
                {
                    gsToMtx[r] = m;
                    var icon = S(gs, r, "DDSFile");
                    if (d.Icon is null && !string.IsNullOrWhiteSpace(icon)) d.Icon = icon;
                }
        Draft? ByGameSpecific(int? g) => g is int gi && gsToMtx.TryGetValue(gi, out var m) ? drafts[m] : null;
        ct.ThrowIfCancellationRequested();

        var ivi = Table("ItemVisualIdentity");
        var ivx = Table("ItemVisualEffect");
        var itemClasses = Table("ItemClasses");

        // 3. Equipment skins / effects
        if (Table("ItemisedVisualEffect") is { } ive)
            for (int r = 0; r < ive.RowCount; r++)
            {
                var d = ByGameSpecific(R(ive, r, "Mtx"));
                if (d is null) continue;
                if (itemClasses != null && ive.HasColumn("ItemClasses"))
                    foreach (var itemClass in ive.GetRowRefArray(r, "ItemClasses").OfType<int>().Where(index => index < itemClasses.RowCount))
                        d.ItemClasses.Add(S(itemClasses, itemClass, "Id"));
                if (ivi != null && R(ive, r, "VisualIdentity") is int v && v < ivi.RowCount)
                {
                    foreach (var (classKey, col) in CharacterClasses.All)
                    {
                        var variants = SA(ivi, v, col).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        if (variants.Count == 1 && Has(variants[0])) d.Classes.TryAdd(classKey, variants[0]);
                        else if (variants.Count > 1)
                        {
                            d.MappingIssues.Add(classKey + ": several resources in one class variant; matching the parts needs research.");
                            d.Resources.AddRange(Existing(variants));
                        }
                    }
                    // AOFile of an MTX identity is the dropped-item model (often CurrencyOrb.ao), not the worn look.
                    d.Resources.AddRange(Existing([.. SA(ivi, v, "SMFiles"), S(ivi, v, "AOFile2"), S(ivi, v, "EPKFile")]));
                    string held = S(ivi,v,"AOFile2");
                    if (d.Type is "Weapon Skin" or "Sword Skin" or "Shield Skin" && Has(held)) d.HeldWeaponPath ??= held;
                    d.Resources.AddRange(d.Classes.Values);
                    d.Links.Add("ItemisedVisualEffect → ItemVisualIdentity");
                }
                if (ivx != null && R(ive, r, "VisualEffect") is int ve && ve < ivx.RowCount)
                {
                    d.Resources.AddRange(Existing(ivx.Schema.Columns.Where(c => c.Name != null && c.Type == DatColumnType.String && !c.IsArray && c.Name != "Id")
                        .Select(c => S(ivx, ve, c.Name!))));
                    if(d.Type == "Weapon Effect")
                        foreach(string role in WeaponVisuals.EffectRoles)
                        {
                            string path=S(ivx,ve,role);
                            if(Has(path)&&Path.GetExtension(path).Equals(".epk",StringComparison.OrdinalIgnoreCase))
                                d.Roles.TryAdd(role,path);
                        }
                    d.Links.Add("ItemisedVisualEffect → ItemVisualEffect");
                }
            }

        // 4. Portals. The default town portal is not a row of this table (no row lacks an MTX in the real data);
        //    it is the "townportal" object under Metadata/Effects/Spells/portal, offered below if present.
        if (Table("MicrotransactionPortalVariations") is { } pv)
            for (int r = 0; r < pv.RowCount; r++)
            {
                var files = Existing([S(pv, r, "AOFile"), S(pv, r, "MapAOFile"), S(pv, r, "PortalEffect"), S(pv, r, "PortalEffectLarge")]);
                var key = R(pv, r, "MtxTypeGameSpecific");
                if (key is null)
                {
                    foreach (var f in files) suggestions.Add(new("portal", "Default portal · " + Path.GetFileName(f), f, "MicrotransactionPortalVariations"));
                    continue;
                }
                if (ByGameSpecific(key) is not { } d) continue; // e.g. internal [DNT] entries
                d.Forced = CosmeticCategory.Portal;
                d.Resources.AddRange(files);
                // MapAOFile and apparition resources have separate roles. Both townportal base files use AOFile.
                if (Has(S(pv, r, "AOFile")))
                {
                    d.Roles["portal"] = S(pv, r, "AOFile");
                    d.Roles["portal2"] = S(pv, r, "AOFile");
                }
                d.Links.Add("MicrotransactionPortalVariations");
            }
        var defaultPortal = Existing(DefaultPortalFiles);
        if (defaultPortal.Count > 0)
            suggestions.Add(new("portal", "Default town portal", defaultPortal[0], "Metadata/Effects/Spells/portal")
            {
                ClassVariants = defaultPortal.ToDictionary(p => Path.GetFileNameWithoutExtension(p), p => p, StringComparer.OrdinalIgnoreCase),
            });

        // 5. Pets: the pet's object type id; visuals are its .ao/.aoc or referenced from the .otc
        if (Table("Pet") is { } pet)
            for (int r = 0; r < pet.RowCount; r++)
            {
                var d = ByGameSpecific(R(pet, r, "MtxTypeGameSpecificKey"));
                var id = S(pet, r, "Id");
                if (d is null || string.IsNullOrWhiteSpace(id)) continue;
                d.Forced = CosmeticCategory.Pet;
                d.Links.Add("Pet");
                foreach (var f in Existing([id + ".aoc", id + ".ao", id + ".otc"]))
                {
                    if (f.EndsWith(".otc", StringComparison.OrdinalIgnoreCase))
                        d.Resources.AddRange(Existing(ReferenceExtractor.Extract(source.Read(f)).Where(p => ResourceClassifier.Classify(p).Domain == ResourceDomain.Visual)));
                    else d.Resources.Add(f);
                }
            }

        // 6. Footprints. The schema has no MTX column, so rows are matched to "… Footprints" MTX entries by their words
        //    (e.g. id "Footprints_Angelic" ↔ "Angelic Footprints"); unmatched rows are listed under their own id.
        if (Table("Footprints") is { } fp)
        {
            static string Key(string s) => string.Join(' ', Pretty(s).ToLowerInvariant()
                .Split([' ', '_', '-', '/'], StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w is not ("footprints" or "footprint" or "effect")).Order());
            var byWords = drafts.Values.Where(d => d.Type.Contains("Footprint", StringComparison.OrdinalIgnoreCase))
                .GroupBy(d => Key(d.Name)).ToDictionary(g => g.Key, g => g.First());
            for (int r = 0; r < fp.RowCount; r++)
            {
                var id = S(fp, r, "Id");
                var files = Existing(SA(fp, r, "Active_AOFiles").Concat(SA(fp, r, "Idle_AOFiles")));
                if (string.IsNullOrWhiteSpace(id) || files.Count == 0) continue;
                if (!byWords.TryGetValue(Key(id), out var d))
                {
                    d = new Draft { Id = "footprints:" + id, Name = Pretty(id) };
                    extra.Add(d);
                }
                else d.Links.Add("Footprints (by name)");
                d.Forced = CosmeticCategory.FootprintsAndTrails;
                d.Resources.AddRange(files);
                if (!d.Links.Contains("Footprints (by name)")) d.Links.Add("Footprints");
                foreach (var column in new[] { "Active_AOFiles", "Idle_AOFiles" })
                {
                    var roleFiles = SA(fp, r, column);
                    for (int i = 0; i < roleFiles.Count; i++)
                        if (!string.IsNullOrWhiteSpace(roleFiles[i])) d.Roles[column + ":" + i] = roleFiles[i];
                }
            }
        }

        // 7. Base items → per-class slot targets
        var bit = Table("BaseItemTypes");
        var cls = Table("ItemClasses");
        if (bit != null && ivi != null && cls != null)
        {
            var classToSlot = SlotItemClasses.SelectMany(kv => kv.Value.Select(c => (c, kv.Key))).ToDictionary(x => x.c, x => x.Key, StringComparer.OrdinalIgnoreCase);
            for (int r = 0; r < bit.RowCount; r++)
            {
                ct.ThrowIfCancellationRequested();
                var id = S(bit, r, "Id");
                var name = S(bit, r, "Name").Trim();
                if (name.Length == 0 || IsInternal(name) || id.Contains("Microtransaction", StringComparison.OrdinalIgnoreCase)) continue;
                if (R(bit, r, "ItemClassesKey") is not int c || c >= cls.RowCount || !classToSlot.TryGetValue(S(cls, c, "Id"), out var slot)) continue;
                if (R(bit, r, "ItemVisualIdentity") is not int v || v >= ivi.RowCount) continue;

                var perClass = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var targetIssues = new List<string>();
                foreach (var (k, col) in CharacterClasses.All)
                {
                    var variants = SA(ivi, v, col).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (variants.Count == 1 && Has(variants[0])) perClass[k] = variants[0];
                    else if (variants.Count > 1) targetIssues.Add(k + ": several base resources for the class.");
                }
                string? primary = perClass.Values.FirstOrDefault()
                                  ?? new[] { S(ivi, v, "AOFile2"), S(ivi, v, "EPKFile"), S(ivi, v, "AOFile") }.FirstOrDefault(Has);
                if (primary is null) continue;
                suggestions.Add(new(slot, name, primary, "BaseItemTypes → ItemVisualIdentity")
                { ClassVariants = perClass, ItemClass = S(cls, c, "Id"), IconPath=S(ivi,v,"DDSFile"), VisualIdentityRow = v, VisualIdentityId = S(ivi, v, "Id"), AutomaticMappingIssue = targetIssues.Count > 0 ? string.Join(" ", targetIssues) : null });
            }
        }

        var categorizer = new CatalogBuilder(rules);
        ResolveNamedSwordModels(drafts.Values.Where(d => d.Type == "Sword Skin" && d.HeldWeaponPath == null));
        var items = drafts.Values.Concat(extra).Select(d => ToItem(d, categorizer))
            .OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        // Only an already-equipped cosmetic supplies an attachment/pet/footprint target; this does not create entities.
        foreach (var item in items.Where(i => i.Category is CosmeticCategory.WingsAndBack or CosmeticCategory.Pet
                     or CosmeticCategory.CharacterEffect or CosmeticCategory.FootprintsAndTrails))
        {
            string slot = item.Category switch
            {
                CosmeticCategory.WingsAndBack => "back", CosmeticCategory.Pet => "pet",
                CosmeticCategory.CharacterEffect => "character_effect", _ => "footprints",
            };
            if (item.ClassVariants.Count == 0 && item.ResourceVariants.Count == 0 && item.PrimaryResources.Count != 1) continue;
            var path = item.ClassVariants.Values.FirstOrDefault() ?? item.ResourceVariants.Values.FirstOrDefault() ?? item.PrimaryResources.SingleOrDefault();
            if (path != null) suggestions.Add(new(slot, "Equipped cosmetic: " + item.DisplayName, path, "existing cosmetic")
            { ClassVariants = item.ClassVariants, ResourceVariants = item.ResourceVariants });
        }
        var dedup = suggestions.DistinctBy(s => (s.SlotKey, s.Label)).OrderBy(s => s.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        var skills = new SkillVisualCatalogBuilder(source, Table).Build(ct);
        var buffBuilder = new BuffVisualCatalogBuilder(Table);
        var buffs = buffBuilder.Build();
        var buffSkills = buffBuilder.BuildSkills(buffs);
        return new DataCatalogResult { Items = items, Suggestions = dedup, Skills = skills, Buffs = buffs, BuffSkills=buffSkills, Diagnostics = _diag.ToList() };
    }

    private void ResolveNamedSwordModels(IEnumerable<Draft> unresolved)
    {
        var swords = unresolved.ToArray();
        if (swords.Length == 0) return;
        static string Key(string value) => new(value.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var models = source.EnumeratePaths().Where(path => path.StartsWith("Metadata/Items/Weapons/", StringComparison.OrdinalIgnoreCase)
            && path.Contains("/Microtransactions/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("held.ao", StringComparison.OrdinalIgnoreCase))
            .ToLookup(path => Key(Path.GetFileNameWithoutExtension(path)));
        foreach (var sword in swords)
            foreach (var (suffix, classes) in new[]
            {
                ("sword1hheld", new[] { "One Hand Sword", "Thrusting One Hand Sword" }),
                ("sword2hheld", new[] { "Two Hand Sword" })
            })
            {
                var paths = models[Key(sword.Name) + suffix].Take(2).ToArray();
                if (paths.Length != 1) continue;
                sword.Resources.Add(paths[0]);
                foreach (string itemClass in classes)
                {
                    sword.ItemClasses.Add(itemClass);
                    sword.Roles.TryAdd(itemClass, paths[0]);
                }
                sword.Links.Add("an unambiguous skin AO by name and weapon type");
            }
    }

    private CosmeticCategory Categorize(Draft d, CatalogBuilder categorizer)
    {
        if (d.Forced is { } f) return f;
        if (TypeCategory.TryGetValue(d.Type, out var c)) return c;
        // "<Skill> Skin" (Cyclone Skin, Flame Dash Skin, Totem Skin …) are skill effects; ring/amulet/frame skins are not wearable visuals.
        if (d.Type.EndsWith(" Skin", StringComparison.OrdinalIgnoreCase) && !d.Type.StartsWith("Ring") && !d.Type.StartsWith("Amulet"))
            return CosmeticCategory.SkillEffect;
        if (d.Type.Contains("Flask", StringComparison.OrdinalIgnoreCase)) return CosmeticCategory.CharacterEffect;
        return categorizer.Categorize(d.Type + " " + d.Name);
    }

    private CosmeticItem ToItem(Draft d, CatalogBuilder categorizer)
    {
        var category = Categorize(d, categorizer);
        var resources = d.Resources.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        string[] order = [".aoc", ".ao", ".epk", ".pet", ".trl", ".sm", ".mat"];
        IEnumerable<string> entries = category == CosmeticCategory.Portal && d.Roles.TryGetValue("portal", out var portal) ? [portal] : resources;
        var primary = entries.Where(p => order.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => Array.IndexOf(order, Path.GetExtension(p).ToLowerInvariant())).ToList();
        var kinds = resources.Select(ResourceClassifier.Classify).ToList();
        bool hasRes = primary.Count > 0;
        return new CosmeticItem
        {
            Id = d.Id.ToLowerInvariant(),
            DisplayName = d.Name,
            Category = category,
            Status = hasRes && d.MappingIssues.Count == 0 ? (category == CosmeticCategory.SkillAudio ? CompatibilityStatus.Unsupported : CompatibilityStatus.Experimental) : CompatibilityStatus.Unsupported,
            StatusReason = hasRes
                ? "Resources from the game tables: " + string.Join(", ", d.Links.Distinct()) + ". " + rules.DefaultFor(category).Reason + " " + string.Join(" ", d.MappingIssues)
                : $"For type “{(d.Type.Length > 0 ? d.Type : "no type")}” no linked resources were found in the known tables.",
            RootPath = resources.Count > 0 ? Path.GetDirectoryName(resources[0])?.Replace('\\', '/') ?? "" : "",
            PrimaryResources = primary,
            AllResources = resources,
            HasAudio = kinds.Any(k => k.Domain == ResourceDomain.Audio),
            HasVisualEffects = kinds.Any(k => k.Kind is ResourceKind.EffectPack or ResourceKind.ParticleEffect or ResourceKind.Trail),
            IconPath = d.Icon,
            Origin = "data",
            ClassVariants = d.Classes,
            ResourceVariants = d.Roles,
            MtxType = d.Type.Length > 0 ? d.Type : null,
            HeldWeaponPath=d.HeldWeaponPath,
            AllowedItemClasses = d.ItemClasses.ToList(),
            AutomaticMappingIssue = d.MappingIssues.Count > 0 ? string.Join(" ", d.MappingIssues) : null,
        };
    }

    /// <summary>Internal/test rows in the real tables are tagged "[DNT]", "[DO NOT USE]", "[UNUSED]" and similar.</summary>
    public static bool IsInternal(string name)
        => name.StartsWith("[DNT", StringComparison.OrdinalIgnoreCase) || name.StartsWith("[DO NOT", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("[UNUSED", StringComparison.OrdinalIgnoreCase) || name.StartsWith("[TEST", StringComparison.OrdinalIgnoreCase);

    private static string Pretty(string id)
    {
        var last = id.Replace('\\', '/').TrimEnd('/').Split('/')[^1].Replace('_', ' ');
        return string.Concat(last.Select((c, i) => i > 0 && char.IsUpper(c) && char.IsLower(last[i - 1]) ? " " + c : c.ToString()));
    }
}
