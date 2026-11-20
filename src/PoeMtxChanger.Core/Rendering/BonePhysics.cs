using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using PoeMtxChanger.Core.Formats.Geometry;

namespace PoeMtxChanger.Core.Rendering;

/// <summary>
/// Bone physics settings of an object's ClientAnimationController block (capes, skirts, ribbons: bones named phys_*).
/// Defaults are the engine's own values for keys an object does not set. Angles are radians, lengths game units
/// (gravity 980 per second squared, so roughly centimetres).
/// </summary>
public sealed record BonePhysicsSettings
{
    public float LinearStiffness { get; init; } = -1f;
    public float LinearAllowedContraction { get; init; } = .5f;
    public float BendStiffness { get; init; } = 10f;
    public float BendAllowedAngle { get; init; } = 1f;
    public float NormalViscosity { get; init; } = .2f;
    public float TangentViscosity { get; init; } = .02f;
    public float EnclosureRadius { get; init; }
    public float EnclosureAngle { get; init; } = .5f;
    public float EnclosureRadiusAdditive { get; init; }
    public float AnimationVelocityFactor { get; init; }
    public float AnimationForceFactor { get; init; }
    public float AnimationVelocityMultiplier { get; init; } = 1f;
    public float AnimationMaxVelocity { get; init; } = 1000f;
    public float Gravity { get; init; } = 980f;

    /// <summary>Settings from the ClientAnimationController blocks of an object's text (later blocks override), or null without one.</summary>
    public static BonePhysicsSettings? Parse(string objectText)
    {
        var blocks = Regex.Matches(objectText, @"ClientAnimationController\s*\{([^{}]*)\}");
        if (blocks.Count == 0) return null;
        var settings = new BonePhysicsSettings();
        foreach (Match block in blocks)
            foreach (Match m in Regex.Matches(block.Groups[1].Value, @"(?m)^\s*(\w+)\s*=\s*(-?[\d.]+)"))
            {
                if (!float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || !float.IsFinite(v)) continue;
                settings = m.Groups[1].Value switch
                {
                    "linear_stiffness" => settings with { LinearStiffness = v },
                    "linear_allowed_contraction" => settings with { LinearAllowedContraction = v },
                    "bend_stiffness" => settings with { BendStiffness = v },
                    "bend_allowed_angle" => settings with { BendAllowedAngle = v },
                    "viscosity_multiplier" or "normal_viscosity_multiplier" => settings with { NormalViscosity = v },
                    "tangent_viscosity_multiplier" => settings with { TangentViscosity = v },
                    "enclosure_radius" => settings with { EnclosureRadius = v },
                    "enclosure_angle" => settings with { EnclosureAngle = v },
                    "enclosure_radius_additive" => settings with { EnclosureRadiusAdditive = v },
                    "animation_velocity_factor" => settings with { AnimationVelocityFactor = v },
                    "animation_force_factor" => settings with { AnimationForceFactor = v },
                    "animation_velocity_multiplier" => settings with { AnimationVelocityMultiplier = v },
                    "animation_max_velocity" => settings with { AnimationMaxVelocity = v },
                    "gravity" => settings with { Gravity = v },
                    _ => settings,
                };
            }
        return settings;
    }

    public static bool HasPhysicsBones(AnimationSkeleton rig) => rig.Bones.Any(b => IsPhysicsBone(b.Name));
    public static bool IsPhysicsBone(string name) => name.StartsWith("phys_", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Simulates the phys_* bone chains of a rig as particles at the joints (Verlet integration in world space, asset Z
/// down): gravity, air drag split into normal and tangent parts, optional pull towards the animated pose, rigid
/// segment lengths, a free bend range with a spring beyond it, and a cone around the animated pose (enclosure) that
/// keeps cloth out of the body. It is the preview's own solver driven by the game's settings, not the engine's code.
/// Stateful: call <see cref="Step"/> once per frame with increasing time; a jump back or a long pause restarts it.
/// </summary>
public sealed class BoneChainPhysics
{
    private const float SubstepSeconds = 1f / 120;
    private readonly AnimationSkeleton _rig;
    private readonly BonePhysicsSettings _settings;
    private readonly int[] _order;           // physics bones, parents first
    private readonly int[] _chainRoot;       // per bone: the kinematic ancestor its chain hangs from (-1 for others)
    private readonly int[] _firstChild;      // per bone: first physics child, or -1
    private readonly Matrix4x4[] _inverseBind;
    private readonly Vector3[] _position, _previous, _lastTarget;
    private float _lastTime;
    private bool _started;

    public BoneChainPhysics(AnimationSkeleton rig, BonePhysicsSettings settings)
    {
        _rig = rig;
        _settings = settings;
        int n = rig.Bones.Length;
        _inverseBind = rig.BindWorld.Select(m => Matrix4x4.Invert(m, out var inv) ? inv : Matrix4x4.Identity).ToArray();
        _position = new Vector3[n]; _previous = new Vector3[n]; _lastTarget = new Vector3[n];
        int Depth(int i) { int d = 0; for (int p = rig.Bones[i].Parent; p >= 0 && d < n; p = rig.Bones[p].Parent) d++; return d; }
        _order = Enumerable.Range(0, n).Where(i => BonePhysicsSettings.IsPhysicsBone(rig.Bones[i].Name) && rig.Bones[i].Parent >= 0)
            .OrderBy(Depth).ToArray();
        var physics = _order.ToHashSet();
        _chainRoot = Enumerable.Repeat(-1, n).ToArray();
        _firstChild = Enumerable.Repeat(-1, n).ToArray();
        foreach (int i in _order)
        {
            int parent = rig.Bones[i].Parent;
            _chainRoot[i] = physics.Contains(parent) ? _chainRoot[parent] : parent;
            if (physics.Contains(parent) && _firstChild[parent] < 0) _firstChild[parent] = i;
        }
    }

    public int SimulatedBones => _order.Length;

    /// <summary>Forgets the simulated state; the next <see cref="Step"/> starts from the animated pose.</summary>
    public void Reset() => _started = false;

    /// <summary>
    /// Skinning matrices with the physics bones replaced by their simulated pose.
    /// </summary>
    /// <param name="skinning">Animated skinning matrices of the rig (null: bind pose).</param>
    /// <param name="mount">Rig space to world (asset coordinates, Z down).</param>
    /// <param name="seconds">Animation time; the simulation advances by its difference to the last call.</param>
    /// <param name="actorVelocity">World velocity of the wearer (running in place still moves through air).</param>
    public Matrix4x4[] Step(Matrix4x4[]? skinning, Matrix4x4 mount, float seconds, Vector3 actorVelocity = default)
    {
        int n = _rig.Bones.Length;
        var skin = skinning is { Length: var len } && len == n ? skinning : Enumerable.Repeat(Matrix4x4.Identity, n).ToArray();
        if (_order.Length == 0) return skin;
        var world = new Matrix4x4[n];
        var target = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            world[i] = _rig.BindWorld[i] * skin[i] * mount;
            target[i] = world[i].Translation;
        }

        float dt = seconds - _lastTime;
        if (!_started || dt < 0 || dt > .5f || !Valid())
        {
            foreach (int i in _order) _position[i] = _previous[i] = target[i];
            _started = true;
        }
        else if (dt > 0)
        {
            int steps = Math.Clamp((int)MathF.Ceiling(dt / SubstepSeconds), 1, 60);
            float h = dt / steps;
            for (int s = 0; s < steps; s++)
            {
                float t0 = s / (float)steps, t1 = (s + 1) / (float)steps;
                Integrate(h, target, t0, t1, actorVelocity);
                for (int iteration = 0; iteration < 3; iteration++) Constrain(h, target, t1);
            }
        }
        Array.Copy(target, _lastTarget, n);
        _lastTime = seconds;

        // Physics bones turn so each points at its simulated child, then sit at their simulated joint.
        if (!Matrix4x4.Invert(mount, out var unmount)) return skin;
        var result = (Matrix4x4[])skin.Clone();
        var rotation = new Quaternion[n];
        foreach (int i in _order)
        {
            int parent = _rig.Bones[i].Parent, child = _firstChild[i];
            Quaternion r = child >= 0 ? Between(target[child] - target[i], _position[child] - _position[i])
                : _chainRoot[i] != parent ? rotation[parent] : Quaternion.Identity;
            rotation[i] = r;
            var moved = world[i] * Matrix4x4.CreateTranslation(-target[i]) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(_position[i]);
            result[i] = _inverseBind[i] * moved * unmount;
        }
        return result;
    }

    private bool Valid()
    {
        foreach (int i in _order) if (!float.IsFinite(_position[i].X + _position[i].Y + _position[i].Z)) return false;
        return true;
    }

    private Vector3 Target(Vector3[] target, int bone, float t) => Vector3.Lerp(_lastTarget[bone], target[bone], t);

    private void Integrate(float h, Vector3[] target, float t0, float t1, Vector3 actorVelocity)
    {
        var s = _settings;
        var gravity = new Vector3(0, 0, s.Gravity); // asset space is Z down
        var air = -actorVelocity;
        float frames = h * 60;
        float keepNormal = MathF.Pow(1 - Math.Clamp(s.NormalViscosity, 0, .99f), frames);
        float keepTangent = MathF.Pow(1 - Math.Clamp(s.TangentViscosity, 0, .99f), frames);
        foreach (int i in _order)
        {
            int parent = _rig.Bones[i].Parent;
            var goal = Target(target, i, t1);
            var velocity = (_position[i] - _previous[i]) / h;
            if (s.AnimationVelocityFactor > 0)
            {
                var animated = (goal - Target(target, i, t0)) / h * s.AnimationVelocityMultiplier;
                if (animated.Length() > s.AnimationMaxVelocity) animated = Vector3.Normalize(animated) * s.AnimationMaxVelocity;
                velocity += (animated - velocity) * (1 - MathF.Exp(-s.AnimationVelocityFactor * h));
            }
            // Drag towards the air: across the segment (normal) and along it (tangent) separately.
            var anchor = _chainRoot[i] == parent ? Target(target, parent, t1) : _position[parent];
            var along = _position[i] - anchor;
            var axis = along.LengthSquared() > 1e-8f ? Vector3.Normalize(along) : Vector3.UnitZ;
            var relative = velocity - air;
            var tangent = axis * Vector3.Dot(relative, axis);
            velocity = air + (relative - tangent) * keepNormal + tangent * keepTangent;
            var acceleration = gravity + (goal - _position[i]) * s.AnimationForceFactor;
            _previous[i] = _position[i];
            _position[i] += velocity * h + acceleration * h * h;
        }
    }

    private void Constrain(float h, Vector3[] target, float t)
    {
        var s = _settings;
        foreach (int i in _order)
        {
            int parent = _rig.Bones[i].Parent;
            bool kinematicParent = _chainRoot[i] == parent;
            var parentAt = kinematicParent ? Target(target, parent, t) : _position[parent];
            var rest = Target(target, i, t) - Target(target, parent, t);
            float length = rest.Length();
            if (length < 1e-5f) { _position[i] = parentAt + rest; continue; }
            var restDir = rest / length;
            var offset = _position[i] - parentAt;
            var dir = offset.LengthSquared() > 1e-10f ? Vector3.Normalize(offset) : restDir;

            // Bend: free within the allowed angle of the animated direction, a spring pulls back beyond it.
            float angle = AngleBetween(dir, restDir);
            if (angle > s.BendAllowedAngle && s.BendStiffness > 0)
            {
                float keep = MathF.Exp(-s.BendStiffness * h);
                dir = Rotate(dir, restDir, (angle - s.BendAllowedAngle) * (1 - keep));
            }
            // Length: rigid unless the stiffness is finite (then it may shrink to the allowed contraction).
            float current = s.LinearStiffness < 0 ? length : Math.Clamp(offset.Length(), length * (1 - Math.Clamp(s.LinearAllowedContraction, 0, 1)), length);
            _position[i] = parentAt + dir * current;

            // Enclosure: stay within a cone around the animated pose, seen from the chain's kinematic root.
            int root = _chainRoot[i];
            if (root >= 0 && s.EnclosureAngle > 0)
            {
                var rootAt = Target(target, root, t);
                var animated = Target(target, i, t) - rootAt;
                var simulated = _position[i] - rootAt;
                if (animated.LengthSquared() > 1e-8f && simulated.LengthSquared() > 1e-8f)
                {
                    float cone = AngleBetween(Vector3.Normalize(simulated), Vector3.Normalize(animated));
                    if (cone > s.EnclosureAngle)
                        _position[i] = rootAt + Rotate(simulated, Vector3.Normalize(animated), cone - s.EnclosureAngle);
                }
                float radius = s.EnclosureRadius > 0 || s.EnclosureRadiusAdditive > 0
                    ? s.EnclosureRadius * animated.Length() + s.EnclosureRadiusAdditive : float.PositiveInfinity;
                var away = _position[i] - Target(target, i, t);
                if (away.Length() > radius) _position[i] = Target(target, i, t) + Vector3.Normalize(away) * radius;
            }
        }
    }

    private static float AngleBetween(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1, 1));

    /// <summary>Turns <paramref name="v"/> towards <paramref name="towards"/> by <paramref name="radians"/>.</summary>
    private static Vector3 Rotate(Vector3 v, Vector3 towards, float radians)
    {
        var axis = Vector3.Cross(v, towards);
        if (axis.LengthSquared() < 1e-12f || radians <= 0) return v;
        return Vector3.Transform(v, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), radians));
    }

    /// <summary>The rotation that turns direction <paramref name="from"/> into <paramref name="to"/>.</summary>
    private static Quaternion Between(Vector3 from, Vector3 to)
    {
        if (from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f) return Quaternion.Identity;
        from = Vector3.Normalize(from); to = Vector3.Normalize(to);
        var axis = Vector3.Cross(from, to);
        float angle = AngleBetween(from, to);
        return axis.LengthSquared() < 1e-12f || angle < 1e-6f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
    }
}
