using System.Numerics;
using PoeMtxChanger.Core.Formats.Geometry;
using PoeMtxChanger.Core.Rendering;

namespace PoeMtxChanger.Core.Tests;

public class BonePhysicsTests
{
    // A root and a phys_ chain at x = 0, 10, 20 sticking out sideways (+X), asset space Z down.
    private static AnimationSkeleton Ribbon() => AnimationSkeleton.FromBones(
    [
        new("root_jntBnd", -1, Matrix4x4.Identity),
        new("phys_ribbon_01", 0, Matrix4x4.Identity),
        new("phys_ribbon_02", 1, Matrix4x4.CreateTranslation(10, 0, 0)),
        new("phys_ribbon_03", 2, Matrix4x4.CreateTranslation(10, 0, 0)),
    ]);

    private static Vector3 Joint(AnimationSkeleton rig, Matrix4x4[] skin, int bone) => (rig.BindWorld[bone] * skin[bone]).Translation;

    private static Matrix4x4[] Run(BoneChainPhysics physics, float seconds, Vector3 velocity = default)
    {
        Matrix4x4[] skin = [];
        for (float t = 0; t <= seconds + 1e-4f; t += 1f / 60) skin = physics.Step(null, Matrix4x4.Identity, t, velocity);
        return skin;
    }

    [Fact]
    public void Settings_use_engine_defaults_and_object_overrides()
    {
        var s = BonePhysicsSettings.Parse("""
            client { ClientAnimationController
            {
                skeleton = "Art/rig.ast"
                bend_stiffness = 0.000
                bend_allowed_angle = 2.000
                normal_viscosity_multiplier = 0.120
                enclosure_radius = 1.000
            } }
            """)!;
        Assert.Equal(0f, s.BendStiffness);
        Assert.Equal(2f, s.BendAllowedAngle);
        Assert.Equal(.12f, s.NormalViscosity, 4);
        Assert.Equal(.02f, s.TangentViscosity, 4); // engine default
        Assert.Equal(980f, s.Gravity);
        Assert.Null(BonePhysicsSettings.Parse("client { SkinMesh { skin = \"a.sm\" } }"));
    }

    [Fact]
    public void Free_chain_falls_under_gravity_and_keeps_its_segment_lengths()
    {
        var rig = Ribbon();
        var physics = new BoneChainPhysics(rig, new BonePhysicsSettings { BendStiffness = 0, EnclosureAngle = 0 });
        Assert.Equal(3, physics.SimulatedBones);
        var skin = Run(physics, 3);
        var tip = Joint(rig, skin, 3);
        Assert.True(tip.Z > 15, $"tip should hang down (+Z), got {tip}");
        Assert.Equal(10, Vector3.Distance(Joint(rig, skin, 2), Joint(rig, skin, 1)), 1);
        Assert.Equal(10, Vector3.Distance(tip, Joint(rig, skin, 2)), 1);
        Assert.Equal(Vector3.Zero, Joint(rig, skin, 0)); // the kinematic root does not move
    }

    [Fact]
    public void Enclosure_cone_keeps_the_chain_near_its_animated_pose()
    {
        var rig = Ribbon();
        var physics = new BoneChainPhysics(rig, new BonePhysicsSettings { BendStiffness = 0, EnclosureAngle = .3f });
        var tip = Joint(rig, Run(physics, 3), 3);
        float angle = MathF.Acos(Vector3.Dot(Vector3.Normalize(tip), Vector3.UnitX));
        Assert.InRange(angle, .2f, .31f);
    }

    [Fact]
    public void Running_streams_the_chain_against_the_motion_and_a_time_jump_restarts()
    {
        var rig = Ribbon();
        var settings = new BonePhysicsSettings { BendStiffness = 0, EnclosureAngle = 0, Gravity = 0 };
        // Pointing sideways; the wearer runs along +Y, so the air pushes the ribbon towards −Y.
        var tip = Joint(rig, Run(new BoneChainPhysics(rig, settings), 2, new Vector3(0, 400, 0)), 3);
        Assert.True(tip.Y < -5, $"tip should trail behind, got {tip}");

        var physics = new BoneChainPhysics(rig, settings with { Gravity = 980 });
        Run(physics, 2);
        var restarted = physics.Step(null, Matrix4x4.Identity, 0);
        Assert.Equal(new Vector3(20, 0, 0), Joint(rig, restarted, 3));
    }
}
