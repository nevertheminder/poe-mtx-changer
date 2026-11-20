using System.Numerics;
using PoeMtxChanger.Core.Rendering;

namespace PoeMtxChanger.Core.Tests;

public sealed class EmitterShapeTests
{
    private static readonly float[] Samples = Enumerable.Range(0, 200).Select(i => (i + 0.5f) / 200).ToArray();

    [Fact]
    public void Point_emitters_use_the_first_bone_of_their_group()
    {
        var shape = EmitterShape.For("PointEmitter", [new(0, 0, -10), new(41, 0, -124)]);
        Assert.All(Samples, u => Assert.Equal(new Vector3(0, 0, -10), shape.Spawn(u)));
    }

    [Fact]
    public void Cylinder_socket_offset_gives_radius_and_height()
    {
        // Lightbringer: aux_floor and socket_cyl 41 units out and 114 units up.
        var floor = new Vector3(0, 0, -10);
        var shape = EmitterShape.For("CylinderEmitter", [floor, floor + new Vector3(41, 0, -114)]);
        foreach (var u in Samples)
        {
            var p = shape.Spawn(u) - floor;
            Assert.Equal(41, new Vector2(p.X, p.Y).Length(), 2);
            Assert.InRange(-p.Z, -0.01f, 114.01f);
        }
        // Spread around the axis, not along one line.
        Assert.True(Samples.Select(u => MathF.Atan2(shape.Spawn(u).Y, shape.Spawn(u).X)).Max() -
                    Samples.Select(u => MathF.Atan2(shape.Spawn(u).Y, shape.Spawn(u).X)).Min() > 5);
    }

    [Fact]
    public void Circle_emitters_find_their_centre_among_the_rim_points()
    {
        var shape = EmitterShape.For("CircleEmitter", [new(150, 0, 0), new(0, 0, 0), new(0, -150, 0)]);
        Assert.All(Samples, u => Assert.Equal(150, shape.Spawn(u).Length(), 2));
    }

    [Fact]
    public void Line_emitters_run_along_all_points()
    {
        var shape = EmitterShape.For("LineEmitter", [new(0, 0, 0), new(10, 0, 0), new(10, 10, 0)]);
        Assert.Equal(new Vector3(0, 0, 0), shape.Spawn(0));
        Assert.Equal(new Vector3(10, 0, 0), shape.Spawn(0.5f));
        Assert.Equal(10, shape.Spawn(0.75f).X, 3);
        Assert.Equal(5, shape.Spawn(0.75f).Y, 3);
    }

    [Fact]
    public void Sphere_surface_points_lie_on_the_radius()
    {
        var shape = EmitterShape.For("SphereSurfaceEmitter", [new(5, 5, 5), new(5, 5, -15)]);
        Assert.All(Samples, u => Assert.Equal(20, Vector3.Distance(shape.Spawn(u), new(5, 5, 5)), 2));
    }
}
