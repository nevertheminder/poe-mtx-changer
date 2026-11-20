using System.Numerics;

namespace PoeMtxChanger.Core.Rendering;

/// <summary>
/// Where an emitter of a .pet file places new particles, from the emitter kind and the points of its bone group
/// (model space, PoE axes: -Z is up, XY is the ground plane).
/// </summary>
/// <remarks>
/// The bone groups give the shape. A cylinder group is the base point plus a socket whose horizontal offset is the
/// radius and whose height is the cylinder's height (e.g. Lightbringer's socket_cyl at 41, 0, -114 over aux_floor);
/// a circle group is its centre and points on the rim; spheres and boxes span their first two points. Line emitters
/// run along all the points. Point emitters use the first point.
/// </remarks>
public sealed class EmitterShape
{
    private static readonly Vector3 Up = new(0, 0, -1);
    private readonly Func<float, Vector3> _spawn;
    public string Kind { get; }

    private EmitterShape(string kind, Func<float, Vector3> spawn) { Kind = kind; _spawn = spawn; }

    /// <param name="u">Per-particle random value in [0, 1); further values are derived from it.</param>
    public Vector3 Spawn(float u) => _spawn(u);

    public static EmitterShape For(string kind, IReadOnlyList<Vector3> points)
    {
        if (points.Count == 0) return new(kind, _ => Vector3.Zero);
        var first = points[0];
        if (points.Count == 1 || kind.StartsWith("Point", StringComparison.OrdinalIgnoreCase)) return new(kind, _ => first);
        var second = points[1];

        if (kind.StartsWith("Cylinder", StringComparison.OrdinalIgnoreCase))
        {
            var offset = second - first;
            float height = Vector3.Dot(offset, Up);
            float radius = new Vector2(offset.X, offset.Y).Length();
            return new(kind, u =>
            {
                float angle = Fraction(u, 1) * MathF.Tau;
                return first + Up * (u * height) + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * radius;
            });
        }
        if (kind.StartsWith("Circle", StringComparison.OrdinalIgnoreCase))
        {
            // Centre: the point closest to the others (aux_floor between aux_circle1 and aux_circle2).
            var centre = points.OrderBy(p => points.Sum(q => Vector3.Distance(p, q))).First();
            var rim = points.Where(p => p != centre).ToArray();
            float radius = rim.Length == 0 ? 0 : rim.Average(p => new Vector2(p.X - centre.X, p.Y - centre.Y).Length());
            return new(kind, u =>
            {
                float angle = u * MathF.Tau;
                return centre + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * radius;
            });
        }
        if (kind.StartsWith("Sphere", StringComparison.OrdinalIgnoreCase))
        {
            float radius = Vector3.Distance(first, second);
            bool surface = kind.Contains("Surface", StringComparison.OrdinalIgnoreCase);
            return new(kind, u =>
            {
                float z = Fraction(u, 1) * 2 - 1, angle = Fraction(u, 2) * MathF.Tau, r = MathF.Sqrt(Math.Max(0, 1 - z * z));
                var direction = new Vector3(r * MathF.Cos(angle), r * MathF.Sin(angle), z);
                return first + direction * radius * (surface ? 1 : MathF.Cbrt(u));
            });
        }
        if (kind.StartsWith("AxisAlignedBox", StringComparison.OrdinalIgnoreCase))
        {
            var min = Vector3.Min(first, second);
            var size = Vector3.Max(first, second) - min;
            return new(kind, u => min + size * new Vector3(u, Fraction(u, 1), Fraction(u, 2)));
        }
        // Line emitters (and unknown kinds): along the polyline of all points.
        var line = points.ToArray();
        return new(kind, u =>
        {
            float segment = u * (line.Length - 1);
            int index = Math.Min((int)segment, line.Length - 2);
            return Vector3.Lerp(line[index], line[index + 1], segment - index);
        });
    }

    /// <summary>Further well-spread values from one random value (golden-ratio sequence).</summary>
    private static float Fraction(float u, int k)
    {
        float v = u * 1000.0f * (k * 0.6180339887f + 1.3247179572f);
        return v - MathF.Floor(v);
    }
}
