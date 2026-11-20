using System.Numerics;
using PoeMtxChanger.Core.Formats.Effects;
using PoeMtxChanger.Core.Formats.Geometry;
using PoeMtxChanger.Core.Formats.Shaders;
using PoeMtxChanger.Core.Rendering;

namespace PoeMtxChanger.Core.Tests;

public sealed class ParticleTests
{
    [Fact]
    public void Legacy_packed_argb_preserves_alpha_when_leading_zeroes_are_omitted()
    {
        var emitter = Assert.Single(PetFile.Parse("""
            version 2
            {
                PointEmitter "test.mat"
                colour ffffff 2ffffff ffffffff
            }
            """));
        Assert.Equal(0f, emitter.Colour.Evaluate(0, 3));
        Assert.Equal(2f / 255, emitter.Colour.Evaluate(.5f, 3));
        Assert.Equal(1f, emitter.Colour.Evaluate(1, 3));
    }

    [Fact]
    public void Slow_continuous_emitters_keep_fractional_births_across_repeated_cycles()
    {
        var emitter = new PetEmitter
        {
            Kind = "PointEmitter", Material = "test.mat",
            Infinite = true, EmitterDuration = 1, ParticleDuration = 5,
            ParticlesPerSecond = EffectCurve.Constant(.263406f)
        };
        for (int seed = 0; seed < 16; seed++)
        {
            var simulator = new ParticleSimulator(emitter, seed);
            var particles = new List<ParticleSprite>();
            int total = 0;
            for (int tick = 20; tick < 820; tick++)
            {
                particles.Clear();
                simulator.Evaluate(tick / 4f, _ => Vector3.Zero, particles);
                Assert.InRange(particles.Count, 1, 2);
                total += particles.Count;
            }
            Assert.InRange(total / 800f, 1.28f, 1.36f);
        }
    }

    [Fact]
    public void Version_three_emitter_keeps_keyed_rate_and_legacy_opacity()
    {
        var emitter = Assert.Single(PetFile.Parse("""
            version 3
            {
                PointEmitter "test.mat"
                is_infinite 1
                particles_per_second 1 2 0 12 "Linear" 1 12 "Linear"
                opacity 0.5 0.5 0.5 0.5 0.5 0.5 0.5
                colour 80FF8040 80FF8040 80FF8040 80FF8040 80FF8040 80FF8040 80FF8040
            }
            """));
        Assert.Equal(12, emitter.ParticlesPerSecond.Max());
        var particles = new List<ParticleSprite>();
        var simulator = new ParticleSimulator(emitter, 18);
        simulator.Evaluate(simulator.Warmup, _ => Vector3.Zero, particles);
        Assert.NotEmpty(particles);
        Assert.All(particles, particle =>
        {
            Assert.Equal(1f, particle.Colour.X);
            Assert.Equal(64f / 255, particle.Colour.Z, 5);
            Assert.Equal(64f / 255, particle.Colour.W, 5);
        });
    }

    [Fact]
    public void Gpu_definition_preserves_passes_mesh_and_lifetime()
    {
        var emitter = Assert.Single(GpuParticleFile.Parse("""
            version 3
            0
            {
                "gpu_emitters": [{
                    "particle_duration_min": 1.5,
                    "particle_duration_max": 2.5,
                    "particles_count": 5,
                    "is_continuous": true,
                    "particle_mesh": "original.fmt",
                    "render_passes": [
                        {"render_material": "red.mat", "face_lock": "Z_Fixed"},
                        {"render_material": "orange.mat", "overwrite_mesh": true, "render_mesh": "layer.fmt"}
                    ]
                }]
            }
            """));
        Assert.Equal(2f, emitter.ParticleDuration);
        Assert.Equal(.25f, emitter.DurationVariance);
        Assert.Equal(2.5f, emitter.Rate);
        Assert.Equal(new GpuParticlePass("red.mat", "original.fmt", "Z_Fixed"), emitter.Passes[0]);
        Assert.Equal(new GpuParticlePass("orange.mat", "layer.fmt", "Camera"), emitter.Passes[1]);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    public void Gpu_burst_uses_fixed_population_and_respects_lifetime(bool persistent, int afterLifetime)
    {
        var emitter = new PetEmitter
        {
            Kind = "PointEmitter", Material = "test.mat",
            InitialBurst = 3, ParticleDuration = 1, PersistentParticles = persistent
        };
        var simulator = new ParticleSimulator(emitter, 29);
        var particles = new List<ParticleSprite>();
        simulator.Evaluate(0, _ => Vector3.Zero, particles);
        Assert.Equal(3, particles.Count);
        particles.Clear();
        simulator.Evaluate(2, _ => Vector3.Zero, particles);
        Assert.Equal(afterLifetime, particles.Count);
    }

    [Fact]
    public void Keyed_trail_uses_width_in_model_units_and_colour_over_segment_lifetime()
    {
        var trail = Assert.Single(TrlTrail.Parse("""
            version 2
            {
                material "test.mat"
                camera_facing 1
                segment_duration 3
                segment_distance_threshold 1
                max_segment_distance 30
                scale 1 2 0 0 "Linear" 3 6 "Linear" 0.47
                emit_scale 1 1 0 0.5 "Linear"
                color 4 2 0 0.2 0.1 0 1 "Linear" 3 0.2 0.1 0 0 "Linear"
            }
            """));
        var sim = new TrailSimulator(trail, 20);
        var points = new List<TrailPoint>();
        sim.Update(0, Vector3.Zero, Vector3.Zero, Vector3.UnitX, points, Vector3.UnitZ);
        sim.Update(1.5f, Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, points, Vector3.UnitZ);
        Assert.Equal(2, points.Count);
        Assert.Equal(Vector3.UnitX, points[0].Left);
        Assert.Equal(Vector3.UnitX, points[0].Right);
        Assert.Equal(1.5f * Vector3.UnitY, points[1].Right - points[1].Left);
        Assert.Equal(new Vector4(.2f, .1f, 0, .5f), points[1].Colour);

        sim.Update(2, 100 * Vector3.UnitX, 100 * Vector3.UnitX, Vector3.UnitX, points, Vector3.UnitZ);
        Assert.Empty(points);
    }

    [Fact]
    public void Gpu_mesh_uses_material_scale_uv_spline_and_local_rotation()
    {
        var material = FxMaterial.Parse("test.mat", """
            {
                "graphinstances": [
                    {"parent": "vtx/Scale_XYZ_MeshUVs.fxgraph", "custom_parameters": [
                        {"name": "UV Coordinates", "parameters": [{"value": 1}]},
                        {"name": "X Scale", "parameters": [{"value": [0.5, 0.5]}]},
                        {"name": "X Multiplier", "parameters": [{"A": {"points": [
                            {"time": 0, "value": 0.5}, {"time": 1, "value": 1.5}
                        ]}}]}
                    ]},
                    {"parent": "vtx/Rotate_Local.fxgraph", "custom_parameters": [
                        {"name": "Rotate X", "parameters": [{"value": [1.57079632679, 1.57079632679]}]}
                    ]}
                ]
            }
            """);
        var vertex = new FixedParticleVertex(new(10, 2, 0), Vector3.UnitY, new(0, .75f));
        var mesh = new FixedParticleMesh([vertex], []);
        var rotated = Assert.Single(GpuParticleGeometry.Apply(mesh, material).Vertices);
        Assert.Equal(vertex, mesh.Vertices[0]);
        Assert.Equal(6.25f, rotated.Position.X);
        Assert.InRange(MathF.Abs(rotated.Position.Y), 0, 1e-5f);
        Assert.Equal(2, rotated.Position.Z);
        Assert.InRange(Vector3.Distance(rotated.Normal, Vector3.UnitZ), 0, 1e-5f);
    }
}
