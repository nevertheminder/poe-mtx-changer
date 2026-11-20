using System.Text.Json;

namespace PoeMtxChanger.Core.Formats.Shaders;

/// <summary>Identity of a graph node: type + index (+ stage for extension-point nodes).</summary>
public readonly record struct FxNodeId(string Type, int Index, string? Stage)
{
    public override string ToString() => Stage is null ? $"{Type}#{Index}" : $"{Type}#{Index}@{Stage}";
}

public sealed record FxNode(FxNodeId Id, JsonElement Parameters, string? CustomParameter, int? GroupId);

public readonly record struct FxPortRef(FxNodeId Node, string Variable, string? Swizzle);

public sealed record FxLink(FxPortRef Source, FxPortRef Target);

/// <summary>A material/effect node graph (.fxgraph), or the inline "defaultgraph" of a .mat.</summary>
public sealed class FxGraph
{
    public required string Path { get; init; }
    public List<FxNode> Nodes { get; } = [];
    public List<FxLink> Links { get; } = [];
    public List<string> ShaderGroups { get; } = [];
    public string? BlendMode { get; init; }
    public string? LightingModel { get; init; }
    public bool LightingDisabled { get; init; }
    public int? AlphaTestThreshold { get; init; }
    public int? EffectOrder { get; init; }

    public static FxGraph Parse(string path, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return FromElement(path, doc.RootElement);
    }

    public static FxGraph FromElement(string path, JsonElement root)
    {
        var g = new FxGraph
        {
            Path = path,
            BlendMode = Str(root, "overriden_blend_mode"),
            LightingModel = Str(root, "overriden_lighting_model"),
            LightingDisabled = root.TryGetProperty("lighting_disabled", out var ld) && ld.ValueKind == JsonValueKind.True,
            AlphaTestThreshold = ReadAlphaTestThreshold(root),
            EffectOrder = root.TryGetProperty("effect_order", out var eo) ? Int(eo) : null,
        };
        if (root.TryGetProperty("shader_group", out var sg) && sg.ValueKind == JsonValueKind.Array)
            foreach (var s in sg.EnumerateArray()) if (s.GetString() is { } v) g.ShaderGroups.Add(v);
        if (root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            foreach (var n in nodes.EnumerateArray())
            {
                var id = Id(n);
                if (id.Type == "<Group>") continue; // editor-only frame
                g.Nodes.Add(new FxNode(id,
                    n.TryGetProperty("parameters", out var p) ? p.Clone() : default,
                    Str(n, "custom_parameter"),
                    n.TryGetProperty("group_id", out var gid) ? Int(gid) : null));
            }
        if (root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
            foreach (var l in links.EnumerateArray())
                g.Links.Add(new FxLink(Port(l.GetProperty("src")), Port(l.GetProperty("dst"))));
        return g;
    }

    private static FxNodeId Id(JsonElement e) => new(
        e.GetProperty("type").GetString()!,
        e.TryGetProperty("index", out var i) ? Int(i) ?? 0 : 0,
        Str(e, "stage"));

    /// <summary>Integers appear both as numbers and as strings in real graphs.</summary>
    internal static int? Int(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt32(out var v) => v,
        JsonValueKind.String when int.TryParse(e.GetString(), out var v) => v,
        _ => null,
    };

    internal static int? ReadAlphaTestThreshold(JsonElement root) =>
        root.TryGetProperty("alphatest_threshold", out var threshold) && Int(threshold) is >= 0 and <= 255 and var value
            ? value : null;

    private static FxPortRef Port(JsonElement e) => new(Id(e), Str(e, "variable") ?? "output", Str(e, "swizzle"));

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>One graph instance inside a .mat: the parent graph plus overrides of its custom parameters.</summary>
public sealed record FxGraphInstance(string Parent, IReadOnlyDictionary<string, JsonElement> CustomParameters)
{
    public int? AlphaTestThreshold { get; init; }
}

/// <summary>A material file (.mat): an ordered list of graph instances composed into one shader.</summary>
public sealed class FxMaterial
{
    public required string Path { get; init; }
    public List<FxGraphInstance> Instances { get; } = [];
    public FxGraph? DefaultGraph { get; init; }

    public static FxMaterial Parse(string path, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var mat = new FxMaterial
        {
            Path = path,
            DefaultGraph = root.TryGetProperty("defaultgraph", out var dg) && dg.ValueKind == JsonValueKind.Object ? FxGraph.FromElement(path + "#default", dg) : null,
        };
        if (root.TryGetProperty("graphinstances", out var gi) && gi.ValueKind == JsonValueKind.Array)
            foreach (var inst in gi.EnumerateArray())
            {
                if (!inst.TryGetProperty("parent", out var parent) || parent.GetString() is not { Length: > 0 } p) continue;
                var overrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (inst.TryGetProperty("custom_parameters", out var cps) && cps.ValueKind == JsonValueKind.Array)
                    foreach (var cp in cps.EnumerateArray())
                        if (cp.TryGetProperty("name", out var n) && n.GetString() is { } name && cp.TryGetProperty("parameters", out var ps))
                            overrides[name] = ps.Clone();
                mat.Instances.Add(new FxGraphInstance(p, overrides) { AlphaTestThreshold = FxGraph.ReadAlphaTestThreshold(inst) });
            }
        return mat;
    }
}
