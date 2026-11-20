using System.Numerics;
using System.Text.Json;
using PoeMtxChanger.Core.Formats.Geometry;
using PoeMtxChanger.Core.Formats.Shaders;

namespace PoeMtxChanger.Core.Rendering;

/// <summary>Applies UV-dependent scale and local rotation from GPU particle mesh render passes.</summary>
public static class GpuParticleGeometry
{
    public static FixedParticleMesh Apply(FixedParticleMesh mesh, FxMaterial material)
    {
        var vertices = mesh.Vertices;
        foreach (var graph in material.Instances)
        {
            if (graph.Parent.EndsWith("/Scale_XYZ_MeshUVs.fxgraph", StringComparison.OrdinalIgnoreCase))
            {
                var scale = new Vector3(Number(graph, "X Scale", 1), Number(graph, "Y Scale", 1), Number(graph, "Z Scale", 1));
                var x = Spline(graph, "X Multiplier");
                var y = Spline(graph, "Y Multiplier");
                var z = Spline(graph, "Z Multiplier");
                var uvMask = Spline(graph, "Displacement Over UVs");
                float channel = Number(graph, "UV Coordinates", 0);
                vertices = Array.ConvertAll(vertices, vertex =>
                {
                    float uv = channel switch
                    {
                        1 => vertex.Uv.Y,
                        2 => (vertex.Uv - new Vector2(.5f)).Length() * 2,
                        _ => vertex.Uv.X
                    };
                    float t = Sample(uvMask, uv, uv);
                    var factor = scale * new Vector3(Sample(x, t, 1), Sample(y, t, 1), Sample(z, t, 1));
                    var inverse = new Vector3(Reciprocal(factor.X), Reciprocal(factor.Y), Reciprocal(factor.Z));
                    return vertex with { Position = vertex.Position * factor, Normal = UnitOr(vertex.Normal * inverse, vertex.Normal) };
                });
            }
            else if (graph.Parent.EndsWith("/Rotate_Local.fxgraph", StringComparison.OrdinalIgnoreCase))
            {
                var rotation = Matrix4x4.CreateRotationX(Number(graph, "Rotate X", 0)) *
                    Matrix4x4.CreateRotationY(Number(graph, "Rotate Y", 0)) *
                    Matrix4x4.CreateRotationZ(Number(graph, "Rotate Z", 0));
                vertices = Array.ConvertAll(vertices, vertex => vertex with
                {
                    Position = Vector3.Transform(vertex.Position, rotation),
                    Normal = Vector3.TransformNormal(vertex.Normal, rotation)
                });
            }
        }
        return vertices == mesh.Vertices ? mesh : mesh with { Vertices = vertices };
    }

    private static JsonElement Parameter(FxGraphInstance graph, string name) =>
        graph.CustomParameters.TryGetValue(name, out var values) && values.ValueKind == JsonValueKind.Array &&
        values.GetArrayLength() > 0 && values[0].ValueKind == JsonValueKind.Object ? values[0] : default;

    private static bool TryNumber(JsonElement element, out float value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out value) && float.IsFinite(value);
    }

    private static float Number(FxGraphInstance graph, string name, float fallback)
    {
        var parameter = Parameter(graph, name);
        if (parameter.ValueKind != JsonValueKind.Object || !parameter.TryGetProperty("value", out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.True) return 1;
        if (value.ValueKind == JsonValueKind.False) return 0;
        if (TryNumber(value, out float number)) return number;
        // The GPU hashes the authored range per particle. Use its midpoint for the shared preview mesh.
        return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2 &&
               TryNumber(value[0], out float min) && TryNumber(value[1], out float max) ? float.Lerp(min, max, .5f) : fallback;
    }

    private static (float Time, float Value, bool Smooth)[] Spline(FxGraphInstance graph, string name)
    {
        var param = Parameter(graph, name);
        if (param.ValueKind != JsonValueKind.Object || !param.TryGetProperty("A", out var track) || track.ValueKind != JsonValueKind.Object ||
            !track.TryGetProperty("points", out var keys) || keys.ValueKind != JsonValueKind.Array) return [];
        var points = new List<(float Time, float Value, bool Smooth)>();
        foreach (var key in keys.EnumerateArray())
            if (key.ValueKind == JsonValueKind.Object && key.TryGetProperty("time", out var time) && TryNumber(time, out float t) &&
                key.TryGetProperty("value", out var value) && TryNumber(value, out float v))
                points.Add((t, v, key.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.ValueEquals("Smooth")));
        return points.OrderBy(p => p.Time).ToArray();
    }

    private static float Sample((float Time, float Value, bool Smooth)[] keys, float time, float fallback)
    {
        if (keys.Length == 0) return fallback;
        if (time <= keys[0].Time) return keys[0].Value;
        for (int i = 1; i < keys.Length; i++)
        {
            var left = keys[i - 1];
            var right = keys[i];
            if (time > right.Time) continue;
            float f = (time - left.Time) / (right.Time - left.Time);
            if (left.Smooth) f *= f * (3 - 2 * f);
            return float.Lerp(left.Value, right.Value, f);
        }
        return keys[^1].Value;
    }

    private static float Reciprocal(float value) => MathF.Abs(value) > 1e-6f ? 1 / value : 0;
    private static Vector3 UnitOr(Vector3 v, Vector3 fallback) => v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : fallback;
}
