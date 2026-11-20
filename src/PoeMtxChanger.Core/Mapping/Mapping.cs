using System.Text.Json.Serialization;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Formats.Bundles;
using PoeMtxChanger.Core.Resources;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Mapping;

/// <summary>Replace the content of <see cref="TargetPath"/> with the content of <see cref="SourcePath"/>.</summary>
public sealed record ReplacementMapping
{
    public required string TargetPath { get; init; }
    public required string SourcePath { get; init; }
    /// <summary>Catalog id of the cosmetic the source belongs to (informational; "mock/..." ids are rejected).</summary>
    public string? SourceCosmeticId { get; init; }
    public string? Note { get; init; }
    /// <summary>Strictly scoped visual-reference edit; never a generic data-table replacement.</summary>
    public VisualIdentityReferenceEdit? VisualIdentityEdit { get; init; }

    public ReplacementMapping Normalized() => this with
    {
        TargetPath = BundleIndex.NormalizePath(TargetPath.Trim()),
        SourcePath = BundleIndex.NormalizePath(SourcePath.Trim()),
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<IssueSeverity>))]
public enum IssueSeverity { Info, Warning, Error }

public sealed record ValidationIssue(IssueSeverity Severity, string Code, string Message, string? Path = null);

public sealed class ValidationReport
{
    public List<ValidationIssue> Issues { get; } = [];
    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);

    public CompatibilityStatus Status => HasErrors ? CompatibilityStatus.Unsupported
        : Issues.Any(i => i.Severity == IssueSeverity.Warning) ? CompatibilityStatus.Experimental
        : CompatibilityStatus.Supported;

    public void Add(IssueSeverity s, string code, string message, string? path = null) => Issues.Add(new(s, code, message, path));
}

/// <summary>
/// Guard rails that keep the tool cosmetic-only. Raw data-table swaps, object types, terrain, monsters and UI
/// are blocked. VisualIdentityEditor separately permits only named per-class appearance pointers.
/// Empty content replacements (effect removal) are refused.
/// </summary>
public sealed class ResourcePolicy
{
    public IReadOnlySet<ResourceKind> ReplaceableKinds { get; init; } = new HashSet<ResourceKind>
    {
        ResourceKind.AnimatedObject, ResourceKind.AnimatedObjectClient, ResourceKind.EffectPack,
        ResourceKind.ParticleEffect, ResourceKind.Trail, ResourceKind.Material, ResourceKind.SkinnedMesh,
        ResourceKind.MeshData, ResourceKind.FixedMesh, ResourceKind.Animation, ResourceKind.Texture,
    };

    /// <summary>Kinds allowed but flagged as risky: .ao files are shared with server-side definitions.</summary>
    public IReadOnlySet<ResourceKind> WarnKinds { get; init; } = new HashSet<ResourceKind> { ResourceKind.AnimatedObject };

    /// <summary>Checked before <see cref="DeniedTargetPrefixes"/>: worn equipment visuals live under Metadata/Items.</summary>
    public IReadOnlyList<string> AllowedTargetPrefixes { get; init; } =
        ["Metadata/Items/Armours/", "Metadata/Items/Weapons/", "Metadata/Items/MicrotransactionItemEffects/"];

    public IReadOnlyList<string> DeniedTargetPrefixes { get; init; } =
    [
        "Data/", "Metadata/StatDescriptions/", "Metadata/Items/", "Metadata/Monsters/", "Art/Models/Monsters/",
        "Metadata/Effects/Monsters/", "Metadata/Terrain/", "Art/Models/Terrain/", "Metadata/Effects/Environment/",
        "Metadata/UI/", "Art/2DArt/UIImages/", "Art/2DArt/Minimap/", "Metadata/MiscellaneousObjects/",
        "Metadata/Chests/", "Metadata/NPC/", "Shaders/",
    ];

    public static ResourcePolicy Default { get; } = new();

    public string? WhyTargetDenied(string path)
    {
        var kind = ResourceClassifier.Classify(path).Kind;
        if (!ReplaceableKinds.Contains(kind))
            return kind switch
            {
                ResourceKind.DataTable or ResourceKind.ObjectType or ResourceKind.ObjectTypeClient
                    => "Gameplay data/object definitions are never modified.",
                ResourceKind.SoundBank or ResourceKind.Audio => "Use the separate FMOD sample editor for audio; bundle resource mappings cannot change audio banks.",
                _ => $"Resource kind {kind} is not on the cosmetic allow-list.",
            };
        if (AllowedTargetPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return null;
        foreach (var p in DeniedTargetPrefixes)
            if (path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return $"Paths under {p} can affect gameplay readability and are blocked.";
        return null;
    }
}

public sealed class MappingValidator(ResourcePolicy policy)
{
    public ValidationReport Validate(IReadOnlyList<ReplacementMapping> mappings, IResourceSource source, bool analyzeDependencies = true)
    {
        var report = new ValidationReport();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in mappings)
            ValidateOne(raw.Normalized(), source, report, targets, analyzeDependencies);
        if (mappings.Count == 0) report.Add(IssueSeverity.Error, "EMPTY", "Nothing to apply.");
        var visualEdits = mappings.Where(m => m.VisualIdentityEdit != null).ToList();
        if (visualEdits.Count > 0)
        {
            try { VisualIdentityEditor.Apply(source.Read(VisualIdentityEditor.TablePath), visualEdits); }
            catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            { report.Add(IssueSeverity.Error, "VISUAL_REFERENCE", ex.Message, VisualIdentityEditor.TablePath); }
        }
        return report;
    }

    private void ValidateOne(ReplacementMapping m, IResourceSource source, ValidationReport report, HashSet<string> targets, bool deps)
    {
        if (m.SourceCosmeticId?.StartsWith("mock/", StringComparison.OrdinalIgnoreCase) == true || m.SourcePath.StartsWith("mock/", StringComparison.OrdinalIgnoreCase))
        {
            report.Add(IssueSeverity.Error, "MOCK", "Mock cosmetics are demo data and cannot be applied.", m.SourcePath);
            return;
        }
        if (string.Equals(m.TargetPath, m.SourcePath, StringComparison.OrdinalIgnoreCase))
            report.Add(IssueSeverity.Error, "SELF", "Source and target are the same file.", m.TargetPath);
        var targetKey = m.VisualIdentityEdit is { } edit ? m.TargetPath + "|" + edit.Row + "|" + edit.Column : m.TargetPath;
        if (!targets.Add(targetKey))
            report.Add(IssueSeverity.Error, "DUP_TARGET", "Target is mapped more than once.", m.TargetPath);

        if (m.VisualIdentityEdit == null && policy.WhyTargetDenied(m.TargetPath) is { } why)
            report.Add(IssueSeverity.Error, "POLICY", why, m.TargetPath);

        var tk = ResourceClassifier.Classify(m.TargetPath).Kind;
        var sk = ResourceClassifier.Classify(m.SourcePath).Kind;
        if (m.VisualIdentityEdit != null && (!m.TargetPath.Equals(VisualIdentityEditor.TablePath, StringComparison.OrdinalIgnoreCase) || sk != ResourceKind.AnimatedObject))
            report.Add(IssueSeverity.Error, "VISUAL_REFERENCE", "Only class-appearance references in ItemVisualIdentity may point to an existing .ao.", m.TargetPath);
        if (tk != sk && m.VisualIdentityEdit == null)
            report.Add(IssueSeverity.Error, "KIND", $"Kind mismatch: target is {tk}, source is {sk}.", m.TargetPath);
        if (policy.WarnKinds.Contains(tk))
            report.Add(IssueSeverity.Info, "SHARED_KIND", ".ao files are shared with server-side definitions; only visual differences are expected.", m.TargetPath);

        bool targetExists = source.Exists(m.TargetPath);
        bool sourceExists = source.Exists(m.SourcePath);
        if (!targetExists) report.Add(IssueSeverity.Error, "TARGET_MISSING", "Target is not in the game index.", m.TargetPath);
        if (!sourceExists) report.Add(IssueSeverity.Error, "SOURCE_MISSING", "Source is not in the game index.", m.SourcePath);
        if (!sourceExists) return;

        byte[] content;
        try { content = source.Read(m.SourcePath); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            report.Add(IssueSeverity.Error, "SOURCE_READ", $"Source could not be read: {ex.Message}", m.SourcePath);
            return;
        }
        if (content.Length == 0)
            report.Add(IssueSeverity.Error, "EMPTY_SOURCE", "Empty replacements (effect removal) are not allowed.", m.SourcePath);

        if (!deps) return;
        var dep = new DependencyAnalyzer(source).Analyze(m.SourcePath);
        foreach (var missing in dep.Missing.Where(p => !string.Equals(p, m.SourcePath, StringComparison.OrdinalIgnoreCase)))
        {
            // Sounds are referenced by name but live inside FMOD banks, so they are never loose files in the index.
            bool audio = ResourceClassifier.Classify(missing).Domain == ResourceDomain.Audio || missing.Contains("$(", StringComparison.Ordinal);
            // The source is an unmodified game resource: a reference missing from its closure is missing for the game
            // too when the cosmetic is worn normally (real data: 55 of 4,083 linked cosmetics ship like this).
            // The swap does not introduce the gap, so it is a warning (Experimental), not a block.
            report.Add(audio ? IssueSeverity.Info : IssueSeverity.Warning, audio ? "DEP_AUDIO" : "DEP_MISSING",
                $"Referenced resource not found (also missing in the original game data): {missing}", m.SourcePath);
        }
        if (dep.Truncated)
            report.Add(IssueSeverity.Error, "DEP_TRUNCATED", "Dependency analysis reached its depth/node limit; completeness cannot be confirmed.", m.SourcePath);
        foreach (var error in dep.ReadErrors)
            report.Add(IssueSeverity.Error, "DEP_READ", "Dependency could not be inspected: " + error, m.SourcePath);
        report.Add(IssueSeverity.Info, "DEPS", $"{dep.Nodes.Count} resources in dependency closure.", m.SourcePath);
    }
}
