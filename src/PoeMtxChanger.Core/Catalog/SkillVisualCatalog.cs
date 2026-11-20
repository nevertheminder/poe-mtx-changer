using PoeMtxChanger.Core.Formats.Dat;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Catalog;

public sealed record SkillVisualVariant(int Number, string Name, IReadOnlyList<ReplacementMapping> Mappings,
    IReadOnlyList<string> Limitations, IReadOnlyList<string> Errors)
{
    public bool CanApply => Mappings.Count > 0 && Errors.Count == 0;
    /// <summary>Shop name of the MTX that selects this variant, when exactly one does.</summary>
    public string? CosmeticName { get; init; }
    /// <summary>Inventory icon of that MTX.</summary>
    public string? IconPath { get; init; }
    public string PickerName => CosmeticName ?? Name;
    public string Summary => string.Join("\n", new[] { CanApply ? $"Experimental: {Mappings.Count} visual resource replacements. Not checked in game." : "Automatic replacement is unavailable." }
        .Concat(Limitations).Concat(Errors));
}

public sealed record SkillVisualDefinition(string Id, string Name, IReadOnlyList<SkillVisualVariant> Variants)
{
    public string? IconPath { get; init; }
}

/// <summary>
/// Read-only joins through explicitly named Variant columns. Never infers a variant from row order,
/// never changes skill/projectile tables, and never invents a target for an additional-only effect.
/// </summary>
public sealed class SkillVisualCatalogBuilder(IResourceSource source, Func<string, DatTable?> table)
{
    private sealed record Role(string Key, IReadOnlyDictionary<int, IReadOnlyList<string>> Files, HashSet<int> AmbiguousVariants);

    public IReadOnlyList<SkillVisualDefinition> Build(CancellationToken ct = default)
    {
        var skills = table("SkillArtVariations");
        if (skills is null) return [];
        var animationRows = table("MiscAnimatedArtVariations");
        var animations = table("MiscAnimated");
        var projectileRows = table("ProjectilesArtVariations");
        var projectiles = table("Projectiles");
        var activeSkills = table("ActiveSkills");
        var beamRows = table("MiscBeamsArtVariations"); var beams = table("MiscBeams");
        var packRows = table("MiscEffectPacksArtVariations"); var packs = table("MiscEffectPacks");
        var animationGroups = Groups(animationRows);
        var projectileGroups = Groups(projectileRows);
        var beamGroups = Groups(beamRows); var packGroups = Groups(packRows);
        var result = new List<SkillVisualDefinition>();
        var mtxArt = MtxArtIndex.Build(table);
        for (int r = 0; r < skills.RowCount; r++)
        {
            ct.ThrowIfCancellationRequested();
            var id = skills.GetString(r, "Id");
            if (string.IsNullOrWhiteSpace(id) || DataCatalogBuilder.IsInternal(id)) continue;
            var roles = new List<Role>();
            AddRoles("AnimatedArt", "animated", animationRows, animationGroups, variantRow =>
                Refs(animationRows!, variantRow, "Animation").Select(index => animations is not null && index < animations.RowCount
                    ? animations.GetString(index, "AOFile") : "").ToList());
            AddRoles("ProjectileArt", "projectile", projectileRows, projectileGroups, variantRow =>
                Refs(projectileRows!, variantRow, "Projectiles").SelectMany(index => projectiles is not null && index < projectiles.RowCount
                    ? projectiles.GetStringArray(index, "AOFiles") : [""]).ToList());
            AddRoles("BeamArt", "beam", beamRows, beamGroups, variantRow =>
                Refs(beamRows!, variantRow, "Beam").Select(index =>
                {
                    int? animated = beams != null && index < beams.RowCount ? beams.GetRowRef(index, "MiscAnimated") : null;
                    return animated is {} a && animations != null && a < animations.RowCount ? animations.GetString(a, "AOFile") : "";
                }).ToList());
            foreach (string column in new[] { "EPKFile", "PlayerOnly_EPKFile" })
                AddRoles("EffectPacksArt", "pack:" + column, packRows, packGroups, variantRow =>
                    Refs(packRows!, variantRow, "EffectPack").Select(index => packs != null && index < packs.RowCount && packs.HasColumn(column)
                        ? packs.GetString(index, column) : "").ToList());
            if (roles.Count == 0) continue;
            var labels = skills.HasColumn("Variants") ? skills.GetStringArray(r, "Variants") : [];
            var variantNumbers = roles.SelectMany(role => role.Files.Keys).Concat(Enumerable.Range(0, labels.Count))
                .Where(n => n > 0 && n < 4096).Distinct().Order().ToList();
            var skillName = Refs(skills, r, "ActiveSkill").Select(index => activeSkills is not null && index < activeSkills.RowCount
                    ? activeSkills.GetString(index, "DisplayedName") : "").FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? id;
            // Skill skins share the art-variation stat; the gem base items they list (named like the active skills) pick the skill.
            var gemNames = Refs(skills, r, "ActiveSkill").Where(index => activeSkills is not null && index < activeSkills.RowCount)
                .Select(index => activeSkills!.GetString(index, "DisplayedName")).Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var variants = new List<SkillVisualVariant>();
            foreach (int number in variantNumbers)
            {
                var limitations = new List<string>();
                var errors = new List<string>();
                var mappings = new Dictionary<string, ReplacementMapping>(StringComparer.OrdinalIgnoreCase);
                var other = new[] { "ObjectArt", "BuffVisuals", "MonsterVarieties" }
                    .Where(column => Refs(skills, r, column).Any()).ToList();
                if (Refs(skills,r,"BeamArt").Any() && !beamGroups.Any()) other.Add("BeamArt");
                if (Refs(skills,r,"EffectPacksArt").Any() && !packGroups.Any()) other.Add("EffectPacksArt");
                if (other.Count > 0) limitations.Add("Partial replacement: roles not parsed " + string.Join(", ", other) + ".");
                foreach (var role in roles.DistinctBy(role => role.Key))
                {
                    if (role.AmbiguousVariants.Contains(0) || role.AmbiguousVariants.Contains(number))
                    { errors.Add(role.Key + ": duplicate variant number; the replacement is ambiguous."); continue; }
                    if (!role.Files.TryGetValue(0, out var original) || original.Count == 0)
                    {
                        limitations.Add(role.Key + ": no default resource; no additional object is created.");
                        continue;
                    }
                    if (!role.Files.TryGetValue(number, out var replacement))
                    {
                        limitations.Add(role.Key + ": the variant is missing; this role stays original.");
                        continue;
                    }
                    if (original.Count != replacement.Count)
                    {
                        errors.Add(role.Key + ": the resource count differs; matching elements is not guessed.");
                        continue;
                    }
                    for (int i = 0; i < original.Count; i++)
                    {
                        var target = original[i]; var alternative = replacement[i];
                        if (string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(alternative)) continue;
                        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(alternative))
                        { limitations.Add(role.Key + ": one side has no resource; an object cannot be added or removed this way."); continue; }
                        if (!source.Exists(target) || !source.Exists(alternative))
                        { errors.Add(role.Key + ": the resource is missing from the index."); continue; }
                        if (target.Equals(alternative, StringComparison.OrdinalIgnoreCase)) continue;
                        if (ResourcePolicy.Default.WhyTargetDenied(target) is { } reason)
                        { errors.Add(role.Key + ": " + reason); continue; }
                        var mapping = new ReplacementMapping { TargetPath = target, SourcePath = alternative,
                            SourceCosmeticId = $"skill-variant:{id}:{number}", Note = role.Key + ":" + i }.Normalized();
                        if (mappings.TryGetValue(mapping.TargetPath, out var previous) && previous.SourcePath != mapping.SourcePath)
                            errors.Add("A shared resource needs different replacements: " + mapping.TargetPath);
                        else mappings[mapping.TargetPath] = mapping;
                    }
                }
                var name = number < labels.Count && !string.IsNullOrWhiteSpace(labels[number]) ? labels[number] : "untitled";
                if (DataCatalogBuilder.IsInternal(name) || name.StartsWith("Reserved", StringComparison.OrdinalIgnoreCase)) continue;
                // A skill MTX sets the skill's art-variation stat to the variant number.
                var art = skills.HasColumn("Stat") && skills.GetRowRef(r, "Stat") is int stat ? mtxArt.ForGem(stat, number, gemNames) : null;
                variants.Add(new(number, $"{name} · variant {number}", errors.Count == 0 ? mappings.Values.ToList() : [],
                    limitations.Distinct().ToList(), errors.Distinct().ToList()) { CosmeticName = art?.Name, IconPath = art?.Icon });
            }
            if (variants.Count > 0)
            {
                var icon = activeSkills?.HasColumn("Icon_DDSFile") == true
                    ? Refs(skills,r,"ActiveSkill").Where(row=>row<activeSkills.RowCount)
                        .Select(row=>activeSkills.GetString(row,"Icon_DDSFile")).FirstOrDefault(path=>!string.IsNullOrWhiteSpace(path))
                    : null;
                // "Cyclone" for id "cyclone": the displayed name alone, never the lowercase id.
                string label = skillName.Equals(id, StringComparison.Ordinal) ? id
                    : skillName.Equals(id, StringComparison.OrdinalIgnoreCase) ? skillName : skillName + " · " + id;
                result.Add(new(id, label, variants) { IconPath=icon });
            }

            void AddRoles(string linkColumn, string prefix, DatTable? variations, Dictionary<string, List<int>> groups, Func<int, IReadOnlyList<string>> read)
            {
                if (variations is null) return;
                foreach (var seed in Refs(skills, r, linkColumn).Where(index => index < variations.RowCount))
                {
                    var roleId = variations.GetString(seed, "Id");
                    if (!groups.TryGetValue(roleId, out var rows)) continue;
                    // Duplicated Variant IDs are ambiguous, not alternative candidates to pick by order.
                    var byVariant = rows.GroupBy(row => variations.GetInt32(row, "Variant"))
                        .ToDictionary(group => group.Key, group => group.Count() == 1 ? read(group.Single()) : (IReadOnlyList<string>)[""]);
                    roles.Add(new(prefix + ":" + roleId, byVariant, rows.GroupBy(row => variations.GetInt32(row,"Variant")).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet()));
                }
            }
        }
        return result.OrderBy(skill => skill.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IEnumerable<int> Refs(DatTable t, int row, string column)
        => t.HasColumn(column) ? t.GetRowRefArray(row, column).OfType<int>() : [];

    private static Dictionary<string, List<int>> Groups(DatTable? t)
        => t is null || !t.HasColumn("Id") || !t.HasColumn("Variant") ? new(StringComparer.OrdinalIgnoreCase)
            : Enumerable.Range(0, t.RowCount).GroupBy(row => t.GetString(row, "Id"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
}
