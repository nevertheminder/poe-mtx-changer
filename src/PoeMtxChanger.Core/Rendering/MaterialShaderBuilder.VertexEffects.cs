using System.Text.Json;
using PoeMtxChanger.Core.Formats.Shaders;

namespace PoeMtxChanger.Core.Rendering;

/// <summary>Vertex changes a material's graphs make that the preview's shared vertex shader can apply per draw.</summary>
/// <param name="NormalOffset">World-space distance each vertex moves along its normal (an inflated outline shell). Its
/// depth bias is the graph's own pixel WorldPos write with OutCustomDepth, which the composed pixel shader keeps.</param>
public readonly record struct VertexEffects(float NormalOffset);

public sealed partial class MaterialShaderBuilder
{
    /// <summary>
    /// Reads the linear vertex pattern VertexPosition@WorldTransform = position + normal × k (wp_outline_2pass inflates
    /// the weapon by its .Expand). Anything else stays unsupported and keeps its note.
    /// </summary>
    private static VertexEffects ReadVertexEffects(List<Instance> instances, Ctx ctx)
    {
        foreach (var inst in instances)
            foreach (var node in inst.Nodes.Values)
                if (node.Id is { Type: "VertexPosition", Stage: "WorldTransform" }
                    && Evaluate(inst, node.Id, "input", "xyz") is { } position && position.IsVector
                    && position.Only("P", 1, out float k) && k != 0)
                {
                    ctx.Notes.Add($"vertex offset along normal applied: {k:0.###}");
                    return new(k);
                }
        return default;
    }

    /// <summary>A linear combination of symbols (P vertex position, N vertex normal) or a scalar ("1").</summary>
    private sealed class Linear
    {
        public readonly Dictionary<string, float> Terms = new(StringComparer.Ordinal);
        public bool IsVector => Terms.Keys.Any(k => k != "1");
        public static Linear Scalar(float v) { var l = new Linear(); l.Terms["1"] = v; return l; }
        public static Linear Symbol(string s) { var l = new Linear(); l.Terms[s] = 1; return l; }

        /// <summary>True when the value is <paramref name="base"/> × <paramref name="weight"/> plus one other symbol (returned as <paramref name="k"/>).</summary>
        public bool Only(string @base, float weight, out float k)
        {
            k = 0;
            if (!Terms.TryGetValue(@base, out float b) || Math.Abs(b - weight) > 1e-5f) return false;
            var others = Terms.Where(t => t.Key != @base && Math.Abs(t.Value) > 1e-7f).ToList();
            if (others.Count != 1) return false;
            var (symbol, value) = (others[0].Key, others[0].Value);
            if (!(@base == "P" && symbol == "N")) return false;
            k = value;
            return true;
        }

        public static Linear? Combine(Linear a, Linear b, float sign)
        {
            if (a.IsVector != b.IsVector) return null;
            var r = new Linear();
            foreach (var (k, v) in a.Terms) r.Terms[k] = v;
            foreach (var (k, v) in b.Terms) r.Terms[k] = r.Terms.GetValueOrDefault(k) + sign * v;
            return r;
        }

        public static Linear? Multiply(Linear a, Linear b)
        {
            if (a.IsVector && b.IsVector) return null;
            var (scalar, other) = a.IsVector ? (b, a) : (a, b);
            float s = scalar.Terms.GetValueOrDefault("1");
            var r = new Linear();
            foreach (var (k, v) in other.Terms) r.Terms[k] = v * s;
            return r;
        }
    }

    /// <summary>Value arriving at a node input, restricted to the linear node types of vertex inflation.</summary>
    private static Linear? Evaluate(Instance inst, FxNodeId node, string variable, string? swizzle, int depth = 0)
    {
        if (depth > 32 || !inst.Incoming.TryGetValue((node, variable), out var links)) return null;
        // A destination swizzle (e.g. xyz of a float4) picks the link that writes those components.
        var link = links.FirstOrDefault(l => swizzle == null ? l.Target.Swizzle is null or "xyz" : l.Target.Swizzle == swizzle)
                   ?? (links.Count == 1 ? links[0] : null);
        if (link is null || !inst.Nodes.TryGetValue(link.Source.Node, out var source)) return null;
        if (link.Source.Swizzle is not (null or "xyz") && source.Id.Type is not ("ConstantVertex" or "ConstantPixel" or "ConstantFloat")) return null;
        Linear? In(string name) => Evaluate(inst, source.Id, name, null, depth + 1);
        switch (source.Id.Type)
        {
            case "InputVertexPosition": return Linear.Symbol("P");
            case "InputVertexNormal": return Linear.Symbol("N");
            case "ConstantVertex" or "ConstantPixel" or "ConstantFloat":
                return ConstantValue(inst, source) is float value ? Linear.Scalar(value) : null;
            case "Multiply" or "Multiply3":
                return In("a") is { } ma && In("b") is { } mb ? Linear.Multiply(ma, mb) : null;
            case "Add" or "Add3":
                return In("a") is { } aa && In("b") is { } ab ? Linear.Combine(aa, ab, 1) : null;
            case "Subtract" or "Subtract3":
                return In("a") is { } sa && In("b") is { } sb ? Linear.Combine(sa, sb, -1) : null;
            default: return null;
        }
    }

    /// <summary>A scalar constant node's value, with the material's custom parameter override when it names one.</summary>
    private static float? ConstantValue(Instance inst, FxNode node)
    {
        static float? First(JsonElement array) =>
            array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0 && array[0].ValueKind == JsonValueKind.Object
            && array[0].TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : null;
        if (node.CustomParameter != null && inst.Mat.CustomParameters.TryGetValue(node.CustomParameter, out var o) && First(o) is { } custom)
            return custom;
        // An empty parameter object is the node's default of zero.
        return First(node.Parameters) ?? (node.Parameters.ValueKind == JsonValueKind.Array ? 0f : null);
    }
}
