using System.Text.RegularExpressions;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Formats.Bundles;
using PoeMtxChanger.Core.Resources;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Mapping;

public sealed record ResolvedMappings(IReadOnlyList<ReplacementMapping> Mappings, IReadOnlyList<string> Notes);

/// <summary>Builds an all-or-nothing plan. Classes and named roles are matched exactly; file order is never a compatibility rule.</summary>
public static partial class SkinMappingResolver
{
    public static ResolvedMappings Resolve(TargetSuggestion? target, string? manualTarget, CosmeticItem cosmetic, IResourceSource? source)
    {
        var notes = new List<string>();
        var mappings = new Dictionary<string, ReplacementMapping>(StringComparer.OrdinalIgnoreCase);
        bool failed = false;
        if (string.IsNullOrWhiteSpace(manualTarget) && (target?.AutomaticMappingIssue ?? cosmetic.AutomaticMappingIssue) is { } issue)
            return new([], [issue]);
        if (target?.ItemClass is { } itemClass && cosmetic.AllowedItemClasses.Count > 0
            && !cosmetic.AllowedItemClasses.Contains(itemClass, StringComparer.OrdinalIgnoreCase))
            return new([], [$"According to the game tables, the cosmetic is not meant for item type “{itemClass}”."]);
        if (target is { ResourceVariants.Count: > 0 } && cosmetic.ResourceVariants.Count > 0
            && !target.ResourceVariants.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(cosmetic.ResourceVariants.Keys))
            return new([], ["The role sets differ (for example, active/idle footprints). No full compatible replacement was found."]);
        var targets = !string.IsNullOrWhiteSpace(manualTarget)
            ? new List<(string? Key, string Path)> { (null, manualTarget.Trim()) }
            : target is { ResourceVariants.Count: > 0 } ? target.ResourceVariants.Select(kv => ((string?)kv.Key, kv.Value)).ToList()
            : target is { ClassVariants.Count: > 0 } ? target.ClassVariants.Select(kv => ((string?)kv.Key, kv.Value)).ToList()
            : target is not null ? new List<(string? Key, string Path)> { (null, target.Path) } : [];
        foreach (var (key, basePath) in targets)
        {
            var isClass = key != null && CharacterClasses.All.Any(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            var named = key is null ? null : Find(isClass ? cosmetic.ClassVariants : cosmetic.ResourceVariants, key);
            IEnumerable<string> candidates;
            if (isClass && named is null)
            {
                notes.Add($"{key}: the cosmetic has no variant for this class; substituting another model is not allowed.");
                failed = true;
                continue;
            }
            if (key != null && !isClass && cosmetic.ResourceVariants.Count > 0 && named is null)
            {
                notes.Add($"{key}: the matching effect variant is missing.");
                failed = true;
                continue;
            }
            candidates = named is null
                ? target?.SlotKey is "weapon_main" or "shield" && cosmetic.HeldWeaponPath is { } held ? [held] : cosmetic.PrimaryResources
                : [named];
            if (isClass && named != null && target?.VisualIdentityRow is int visualRow
                && Path.GetExtension(basePath).Equals(".sm", StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(named).Equals(".ao", StringComparison.OrdinalIgnoreCase)
                && source?.Exists(VisualIdentityEditor.TablePath) == true && source.Exists(named))
            {
                var column = CharacterClasses.All.Single(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Column;
                var visualMapping = new ReplacementMapping
                {
                    TargetPath = VisualIdentityEditor.TablePath, SourcePath = named, SourceCosmeticId = cosmetic.Id, Note = key,
                    VisualIdentityEdit = new(visualRow, column, [basePath], target.VisualIdentityId ?? ""),
                };
                mappings.Add(VisualIdentityEditor.TablePath + "|" + visualRow + "|" + column, visualMapping);
                if (!notes.Contains(WrapperNote)) notes.Add(WrapperNote);
                continue;
            }
            var fitted = candidates.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => Fit(basePath, path, source, notes)).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (fitted.Count != 1)
            {
                notes.Add($"{key ?? Path.GetFileName(basePath)}: matching files found: {fitted.Count}. An unambiguous variant is needed; choose a specific resource yourself.");
                failed = true;
                continue;
            }
            var mapping = new ReplacementMapping { TargetPath = basePath, SourcePath = fitted[0], SourceCosmeticId = cosmetic.Id, Note = key }.Normalized();
            if (mappings.TryGetValue(mapping.TargetPath, out var previous))
            {
                if (!previous.SourcePath.Equals(mapping.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add($"One shared resource {mapping.TargetPath} needs different replacements; the set is incompatible.");
                    failed = true;
                }
            }
            else mappings.Add(mapping.TargetPath, mapping);
        }
        if (targets.Count == 0) notes.Add("No target resource selected.");
        if (failed) notes.Add("An incomplete set is not applied: fix all incompatible variants.");
        return new ResolvedMappings(failed ? [] : mappings.Values.ToList(), notes.Distinct().ToList());
    }

    private static string? Find(IReadOnlyDictionary<string, string> variants, string key)
        => variants.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Fit(string basePath, string cosmeticPath, IResourceSource? source, List<string> notes)
    {
        if (source != null && !source.Exists(cosmeticPath)) return null;
        var be = Path.GetExtension(basePath);
        var ce = Path.GetExtension(cosmeticPath);
        if (be.Equals(ce, StringComparison.OrdinalIgnoreCase)) return cosmeticPath;
        if (be.Equals(".sm", StringComparison.OrdinalIgnoreCase)
            && (ce.Equals(".ao", StringComparison.OrdinalIgnoreCase) || ce.Equals(".aoc", StringComparison.OrdinalIgnoreCase)) && source != null)
        {
            var text = ReferenceExtractor.DecodeText(source.Read(cosmeticPath));
            if (text is null) return null;
            var meshes = SkinMeshBlock().Matches(text).SelectMany(block => SkinAssignment().Matches(block.Groups["body"].Value))
                .Select(m => BundleIndex.NormalizePath(m.Groups["path"].Value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (meshes.Count == 1 && source.Exists(meshes[0]))
            {
                if (!notes.Contains(MeshNote)) notes.Add(MeshNote);
                return meshes[0];
            }
        }
        return null;
    }

    [GeneratedRegex(@"\bSkinMesh\s*\{(?<body>[^{}]*)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SkinMeshBlock();
    [GeneratedRegex("\\bskin\\s*=\\s*\"(?<path>[^\"\\r\\n]+\\.sm)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SkinAssignment();

    public const string MeshNote = "The mesh comes from the cosmetic's SkinMesh; its extra settings (hiding hair and so on) are not carried over.";
    public const string WrapperNote = "The skin's full .ao is attached through the class reference in ItemVisualIdentity. The skin settings are kept in the .ao; loading in game has not been checked yet. Other table fields do not change.";
}
