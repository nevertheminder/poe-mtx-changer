using System.Text;
using System.Text.RegularExpressions;
using PoeMtxChanger.Core.Formats.Bundles;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Resources;

public enum ResourceKind
{
    Unknown,
    AnimatedObject,        // .ao
    AnimatedObjectClient,  // .aoc
    EffectPack,            // .epk
    ParticleEffect,        // .pet
    Trail,                 // .trl
    Material,              // .mat
    SkinnedMesh,           // .sm
    MeshData,              // .smd
    FixedMesh,             // .fmt
    Animation,             // .ast
    Texture,               // .dds, .png
    ObjectType,            // .ot
    ObjectTypeClient,      // .otc
    DataTable,             // .dat, .dat64, .datc64, .datl, .datl64
    SoundBank,             // .bank
    Audio,                 // .ogg, .wav
    Shader,                // .fx, .hlsl, .ffx
    Ui,                    // .ui
    Text,                  // .txt, .json, .xml, .csd
}

/// <summary>What a resource is used for; drives <see cref="Mapping.ResourcePolicy"/>.</summary>
public enum ResourceDomain { Visual, Audio, Gameplay, Interface, Other }

public sealed record ResourceInfo(ResourceKind Kind, ResourceDomain Domain, bool IsTextual);

public static class ResourceClassifier
{
    private static readonly Dictionary<string, ResourceInfo> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ao"] = new(ResourceKind.AnimatedObject, ResourceDomain.Visual, true),
        [".aoc"] = new(ResourceKind.AnimatedObjectClient, ResourceDomain.Visual, true),
        [".epk"] = new(ResourceKind.EffectPack, ResourceDomain.Visual, true),
        [".pet"] = new(ResourceKind.ParticleEffect, ResourceDomain.Visual, true),
        [".trl"] = new(ResourceKind.Trail, ResourceDomain.Visual, true),
        [".mat"] = new(ResourceKind.Material, ResourceDomain.Visual, true),
        [".sm"] = new(ResourceKind.SkinnedMesh, ResourceDomain.Visual, true),
        [".smd"] = new(ResourceKind.MeshData, ResourceDomain.Visual, false),
        [".fmt"] = new(ResourceKind.FixedMesh, ResourceDomain.Visual, false),
        [".ast"] = new(ResourceKind.Animation, ResourceDomain.Visual, false),
        [".dds"] = new(ResourceKind.Texture, ResourceDomain.Visual, false),
        [".png"] = new(ResourceKind.Texture, ResourceDomain.Visual, false),
        [".ot"] = new(ResourceKind.ObjectType, ResourceDomain.Gameplay, true),
        [".otc"] = new(ResourceKind.ObjectTypeClient, ResourceDomain.Gameplay, true),
        [".dat"] = new(ResourceKind.DataTable, ResourceDomain.Gameplay, false),
        [".dat64"] = new(ResourceKind.DataTable, ResourceDomain.Gameplay, false),
        [".datc64"] = new(ResourceKind.DataTable, ResourceDomain.Gameplay, false),
        [".datl"] = new(ResourceKind.DataTable, ResourceDomain.Gameplay, false),
        [".datl64"] = new(ResourceKind.DataTable, ResourceDomain.Gameplay, false),
        [".bank"] = new(ResourceKind.SoundBank, ResourceDomain.Audio, false),
        [".ogg"] = new(ResourceKind.Audio, ResourceDomain.Audio, false),
        [".wav"] = new(ResourceKind.Audio, ResourceDomain.Audio, false),
        [".fx"] = new(ResourceKind.Shader, ResourceDomain.Visual, true),
        [".hlsl"] = new(ResourceKind.Shader, ResourceDomain.Visual, true),
        [".ffx"] = new(ResourceKind.Shader, ResourceDomain.Visual, true),
        [".ui"] = new(ResourceKind.Ui, ResourceDomain.Interface, true),
        [".txt"] = new(ResourceKind.Text, ResourceDomain.Other, true),
        [".json"] = new(ResourceKind.Text, ResourceDomain.Other, true),
        [".xml"] = new(ResourceKind.Text, ResourceDomain.Other, true),
        [".csd"] = new(ResourceKind.Text, ResourceDomain.Interface, true),
    };

    public static ResourceInfo Classify(string path)
        => ByExtension.TryGetValue(Path.GetExtension(path), out var info) ? info : new(ResourceKind.Unknown, ResourceDomain.Other, false);
}

/// <summary>Finds references to other game resources inside text-based resource files.</summary>
public static partial class ReferenceExtractor
{
    // Quoted path or bare path that starts at a known top-level folder.
    // Extensions up to 8 chars (".fxgraph" exists in real files).
    [GeneratedRegex("""(?:"(?<q>(?:Art|Metadata|Audio|Shaders|Data)/[^"\r\n]{1,300}?\.[A-Za-z0-9]{1,8})")|(?<![A-Za-z0-9_/])(?<b>(?:Art|Metadata|Audio|Shaders|Data)/[A-Za-z0-9_\-./]{1,300}\.[A-Za-z0-9]{1,8})""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathRegex();

    /// <summary>Decodes a resource as text: UTF-16LE (with or without BOM) or UTF-8. Returns null for binary data.</summary>
    public static string? DecodeText(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data[2..]);
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8.GetString(data[3..]);
        if (data.Length == 0) return string.Empty;

        int sample = Math.Min(data.Length, 4096);
        int oddZeros = 0, control = 0;
        for (int i = 0; i < sample; i++)
        {
            byte b = data[i];
            if (b == 0 && (i & 1) == 1) oddZeros++;
            if (b < 9 || (b > 13 && b < 32)) control++;
        }
        if (oddZeros > sample / 4) return Encoding.Unicode.GetString(data[..(data.Length & ~1)]);
        if (control > sample / 50) return null;
        return Encoding.UTF8.GetString(data);
    }

    public static IReadOnlyList<string> Extract(ReadOnlySpan<byte> data)
    {
        var text = DecodeText(data);
        return text is null ? [] : Extract(text);
    }

    public static IReadOnlyList<string> Extract(string text)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in PathRegex().Matches(text))
        {
            var v = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["b"].Value;
            set.Add(BundleIndex.NormalizePath(v.Trim()));
        }
        return [.. set];
    }
}

public sealed record DependencyNode(string Path, ResourceKind Kind, bool Exists, int Depth, IReadOnlyList<string> References);

public sealed class DependencyReport
{
    public required string Root { get; init; }
    public required IReadOnlyList<DependencyNode> Nodes { get; init; }
    public IEnumerable<string> Missing => Nodes.Where(n => !n.Exists).Select(n => n.Path);
    public bool Truncated { get; init; }
    public IReadOnlyList<string> ReadErrors { get; init; } = [];
    public bool IsComplete => !Truncated && !Missing.Any() && ReadErrors.Count == 0;
}

/// <summary>Walks resource references breadth-first and reports what resolves in the index.</summary>
public sealed class DependencyAnalyzer(IResourceSource source, int maxDepth = 8, int maxNodes = 4000)
{
    public DependencyReport Analyze(string rootPath)
    {
        rootPath = BundleIndex.NormalizePath(rootPath);
        var nodes = new List<DependencyNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootPath };
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((rootPath, 0));
        bool truncated = false;
        var readErrors = new List<string>();

        while (queue.Count > 0)
        {
            var (path, depth) = queue.Dequeue();
            var info = ResourceClassifier.Classify(path);
            bool exists = source.Exists(path);
            IReadOnlyList<string> refs = [];
            if (exists && info.IsTextual)
            {
                try { refs = ReferenceExtractor.Extract(source.Read(path)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
                { readErrors.Add(path + ": " + ex.Message); }
            }
            nodes.Add(new DependencyNode(path, info.Kind, exists, depth, refs));

            if (depth >= maxDepth)
            {
                if (refs.Any(r => !seen.Contains(r))) truncated = true;
                continue;
            }

            foreach (var r in refs)
            {
                if (!seen.Add(r)) continue;
                if (seen.Count > maxNodes) { truncated = true; break; }
                queue.Enqueue((r, depth + 1));
            }
            if (truncated) break;
        }
        return new DependencyReport { Root = rootPath, Nodes = nodes, Truncated = truncated, ReadErrors = readErrors };
    }
}
