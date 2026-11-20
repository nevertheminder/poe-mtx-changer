using System.Numerics;
using PoeMtxChanger.Core.Formats.Effects;

namespace PoeMtxChanger.Core.Rendering;

/// <summary>A live particle for one frame, in the emitter's model space (PoE axes: -Z is up).</summary>
/// <param name="Rotation">Screen-plane rotation in radians (Random lock), ignored when aligned to velocity.</param>
/// <param name="Atlas">Flipbook cell: x, y, width, height in UV space.</param>
public readonly record struct ParticleSprite(Vector3 Position, float Width, float Height, float Rotation, Vector3 Velocity,
    Vector4 Colour, Vector4 Atlas, bool FlipX, bool FlipY, float Depth = 0, Vector3 LocalRotation = default);

/// <summary>
/// Stateless evaluation of a .pet emitter: for any time it derives which particles are alive from the emission
/// rate curve and a per-particle seed, then integrates their motion analytically. The same time always gives the
/// same frame, so the preview can scrub and loop without keeping particle state.
/// </summary>
/// <remarks>
/// The .pet files carry the parameters but not the engine's exact rules. Where they are not evident from the data
/// the following choices are made: variances are absolute (± value) except the duration variance, which is a
/// fraction of the duration; the scatter cone value is the cone's half angle in radians around the up axis;
/// velocity_along moves along that cone direction, velocity_up and up_acceleration along the up axis; particles
/// follow the emitter's current pose (no history of where it was).
/// </remarks>
public sealed class ParticleSimulator
{
    private static readonly Vector3 Up = new(0, 0, -1);
    public const int MaxParticles = 384;

    private readonly PetEmitter _e;
    private readonly int _seed;
    private readonly double[] _emission; // cumulative births over the sampled window
    private readonly float _window;
    private readonly float _period;
    private readonly float _maxLife;
    private readonly double _firstBirth;
    private readonly int _birthStride;
    private readonly int _burst;

    public PetEmitter Emitter => _e;
    public int Capacity { get; }
    public bool Thinned => _birthStride > 1;
    public float Warmup => _e.Infinite ? Math.Min(_maxLife, 8f) : 0;

    public ParticleSimulator(PetEmitter emitter, int seed)
    {
        _e = emitter;
        _seed = seed;
        _burst = Math.Clamp(emitter.InitialBurst, 0, MaxParticles);
        _maxLife = emitter.ParticleDuration * (1 + Math.Abs(emitter.ParticleDurationVariance));
        float emitTime = emitter.EmitterDuration;
        _window = emitter.Infinite ? emitTime * MathF.Max(1, MathF.Ceiling(2f / emitTime)) : emitTime;
        _period = emitter.Infinite ? _window : emitTime + _maxLife + 0.4f;
        int steps = Math.Clamp((int)(_window * 240), 64, 4096);
        _emission = new double[steps + 1];
        _firstBirth = 1 - Hash01(seed, -1, 7);
        double stepDuration = (double)_window / steps;
        for (int i = 0; i < steps; i++)
        {
            float phase = (float)(((i + .5) * stepDuration % emitTime) / emitTime);
            _emission[i + 1] = _emission[i] + Math.Max(0, emitter.ParticlesPerSecond.Evaluate(phase)) * stepDuration;
        }
        float emittingLifetime = emitter.Infinite ? _maxLife : Math.Min(_maxLife, _window);
        float peak = Math.Max(0, emitter.ParticlesPerSecond.Max()) * emittingLifetime + 2;
        _birthStride = Math.Max(1, (int)Math.Ceiling(peak / Math.Max(1, MaxParticles - _burst)));
        Capacity = Math.Clamp((int)Math.Ceiling(Math.Min(peak, MaxParticles) * 1.25f) + _burst + 2, 1, MaxParticles);
    }

    /// <summary>
    /// Appends the particles alive at <paramref name="time"/>. <paramref name="spawn"/> maps a 0…1 position along the
    /// emitter shape to a point in model space (line between anchors, one anchor, …).
    /// </summary>
    public void Evaluate(float time, Func<float, Vector3> spawn, List<ParticleSprite> output)
    {
        if (time < 0) return;
        int added = 0;
        for (int i = 0; i < _burst; i++)
        {
            int id = -i - 2;
            float life = Lifetime(id);
            float age = _e.PersistentParticles ? time % life : time;
            if (age < life)
            {
                output.Add(Particle(id, 0, age, life, spawn));
                added++;
            }
        }
        double emissionsPerWindow = _emission[^1];
        if (emissionsPerWindow <= 0 || added == Capacity) return;
        int cycle = (int)MathF.Floor(time / _period);
        for (int c = cycle; c >= 0 && added < Capacity && (time - c * _period) <= _maxLife + _window; c--)
        {
            double localTime = time - c * (double)_period;
            // Carry the fractional emission count across windows. A low-rate continuous emitter
            // may emit nothing in one window and still emit later.
            double preceding = _e.Infinite ? c * emissionsPerWindow : 0;
            double first = preceding + EmissionAt(localTime - _maxLife) - _firstBirth;
            double last = preceding + EmissionAt(localTime) - _firstBirth;
            long birth = (long)Math.Ceiling(first / _birthStride) * _birthStride;
            for (; birth <= last && added < Capacity; birth += _birthStride)
            {
                double target = birth + _firstBirth - preceding;
                if (target <= 0 || target > emissionsPerWindow) continue;
                float born = BirthTime(target);
                float age = (float)(localTime - born);
                int id = unchecked((int)(_e.Infinite ? birth : c * 100_003L + birth));
                float life = Lifetime(id);
                if (age < 0 || age >= life) continue;
                output.Add(Particle(id, (born % _e.EmitterDuration) / _e.EmitterDuration, age, life, spawn));
                added++;
            }
        }
    }

    private double EmissionAt(double time)
    {
        int steps = _emission.Length - 1;
        double sample = Math.Clamp(time / _window * steps, 0, steps);
        int left = Math.Min((int)sample, steps - 1);
        return double.Lerp(_emission[left], _emission[left + 1], sample - left);
    }

    private float BirthTime(double emission)
    {
        int left = 0, right = _emission.Length - 1;
        while (left + 1 < right)
        {
            int middle = (left + right) / 2;
            if (_emission[middle] < emission) left = middle;
            else right = middle;
        }
        double fraction = (emission - _emission[left]) / (_emission[right] - _emission[left]);
        return (float)((left + fraction) * _window / (_emission.Length - 1));
    }

    private float Lifetime(int id) =>
        _e.ParticleDuration * Math.Max(0.05f, 1 + _e.ParticleDurationVariance * (Hash01(_seed, id, 1) * 2 - 1));

    private ParticleSprite Particle(int id, float emitterPhase, float age, float life, Func<float, Vector3> spawn)
    {
        float R(int k) => Hash01(_seed, id, k);
        float Signed(int k) => R(k) * 2 - 1;
        var e = _e;
        float phase = age / life;

        // Direction inside the scatter cone around the up axis.
        float cone = Math.Clamp(e.ScatterCone.Evaluate(emitterPhase), 0, MathF.PI);
        float cosTheta = 1 - R(2) * (1 - MathF.Cos(cone));
        float sinTheta = MathF.Sqrt(Math.Max(0, 1 - cosTheta * cosTheta));
        float phi = R(3) * MathF.Tau;
        var direction = Vector3.Normalize(new Vector3(sinTheta * MathF.Cos(phi), sinTheta * MathF.Sin(phi), 0) + Up * cosTheta);

        float alongBias = e.VelocityAlongVariance * Signed(4), upBias = e.VelocityUpVariance * Signed(5), accelBias = e.UpAccelerationVariance * Signed(6);
        // Integrate speed curves over the particle's age (midpoint rule).
        const int Steps = 10;
        float dt = age / Steps, along = 0, up = 0, rotation = 0;
        float upVelocity = 0, accelerated = 0;
        for (int i = 0; i < Steps; i++)
        {
            float p = (i + 0.5f) / Steps * phase;
            along += (e.VelocityAlong.Evaluate(p) + alongBias) * dt;
            up += (e.VelocityUp.Evaluate(p) + upBias) * dt;
            float accel = e.UpAcceleration.Evaluate(p) + accelBias;
            accelerated += (upVelocity + accel * dt * 0.5f) * dt;
            upVelocity += accel * dt;
            rotation += (e.RotationSpeed.Evaluate(p) + e.RotationSpeedVariance * Signed(7)) * dt;
        }
        var origin = spawn(R(8));
        var position = origin + direction * along + Up * (up + accelerated);
        var velocity = direction * (e.VelocityAlong.Evaluate(phase) + alongBias) + Up * (e.VelocityUp.Evaluate(phase) + upBias + upVelocity);

        float size = Math.Max(0, e.Scale.Evaluate(phase) + e.ScaleVariance * Signed(9)) * e.EmitScale.Evaluate(emitterPhase);
        float stretch = Math.Max(0.01f, e.Stretch.Evaluate(phase) + e.StretchVariance * Signed(10));
        var colour = new Vector4(e.Colour.Evaluate(phase, 0), e.Colour.Evaluate(phase, 1), e.Colour.Evaluate(phase, 2), e.Colour.Evaluate(phase, 3))
                   * new Vector4(e.EmitColour.Evaluate(emitterPhase, 0), e.EmitColour.Evaluate(emitterPhase, 1), e.EmitColour.Evaluate(emitterPhase, 2), e.EmitColour.Evaluate(emitterPhase, 3));
        var variance = e.ColourVariance;
        colour += new Vector4(variance[0] * Signed(11), variance[1] * Signed(12), variance[2] * Signed(13), variance[3] * Signed(14));
        colour = Vector4.Max(colour, Vector4.Zero);
        colour.W = Math.Clamp(colour.W * e.Opacity.Evaluate(phase), 0, 1);

        int frames = e.FramesX * e.FramesY;
        int frame = 0;
        if (frames > 1)
        {
            if (e.AnimSpeed == 0) frame = (int)(R(15) * frames) % frames;
            else
            {
                float f = phase * frames * e.AnimSpeed;
                frame = e.AnimPlayOnce ? Math.Min((int)f, frames - 1) : (int)f % frames;
            }
        }
        var atlas = new Vector4((float)(frame % e.FramesX) / e.FramesX, (float)(frame / e.FramesX) / e.FramesY, 1f / e.FramesX, 1f / e.FramesY);
        float initial = e.RotationLock.Equals("Random", StringComparison.OrdinalIgnoreCase) ? R(16) * MathF.Tau : 0;
        var localRotation = e.RotationOffset;
        if (e.RotationOffsetMax is { } maxRotation)
            localRotation += (maxRotation - localRotation) * new Vector3(R(19), R(20), R(21));
        return new(position, size, size * stretch, initial + rotation, velocity, colour, atlas,
            e.RandomFlipX && R(17) < 0.5f, e.RandomFlipY && R(18) < 0.5f, LocalRotation: localRotation);
    }

    /// <summary>Deterministic hash → [0, 1).</summary>
    internal static float Hash01(int seed, int id, int channel)
    {
        uint h = (uint)seed * 0x9E3779B1u ^ (uint)id * 0x85EBCA77u ^ (uint)channel * 0xC2B2AE3Du;
        h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
        return (h >> 8) / 16777216f;
    }
}

/// <summary>A ribbon point of a trail for one frame.</summary>
public readonly record struct TrailPoint(Vector3 Left, Vector3 Right, float Along, float Distance, Vector4 Colour);

/// <summary>
/// Trail of a .trl block: records emitter positions as segments over time and builds the ribbon with the
/// segment-life curves (opacity, scale, colour). Stateful: feed it frames in time order.
/// </summary>
public sealed class TrailSimulator(TrlTrail trail, float baseWidth)
{
    private readonly record struct Segment(float Time, Vector3 A, Vector3 B, float Distance)
    {
        public Vector3 Centre => (A + B) * 0.5f;
    }
    private readonly List<Segment> _segments = [];
    private readonly List<Segment> _points = [];
    public const int MaxSegments = 48;
    public TrlTrail Trail => trail;
    public void Reset() => _segments.Clear();

    /// <summary>
    /// Advances to <paramref name="time"/> with the emitter's current edge (a → b; equal points for a single anchor,
    /// in which case <paramref name="side"/> gives the ribbon's width direction) and fills the ribbon.
    /// </summary>
    public void Update(float time, Vector3 a, Vector3 b, Vector3 side, List<TrailPoint> output, Vector3 viewDirection = default)
    {
        if (_segments.Count > 0 && time < _segments[^1].Time) _segments.Clear(); // time went back (loop / scrub)
        while (_segments.Count > 0 && time - _segments[0].Time > trail.SegmentDuration) _segments.RemoveAt(0);
        var centre = (a + b) * 0.5f;
        float step = _segments.Count == 0 ? 0 : Vector3.Distance(_segments[^1].Centre, centre);
        if (trail.MaxSegmentDistance > 0 && step > trail.MaxSegmentDistance) { _segments.Clear(); step = 0; }
        float distance = _segments.Count == 0 ? 0 : _segments[^1].Distance + step;
        if (_segments.Count == 0 || step >= trail.SegmentDistanceThreshold)
        {
            _segments.Add(new(time, a, b, distance));
            if (_segments.Count > MaxSegments) _segments.RemoveAt(0);
        }
        output.Clear();
        if (_segments.Count < 2 || _segments[^1].Distance - _segments[0].Distance < trail.StartDistanceThreshold) return;
        // Head (current emitter position) first, then older segments.
        _points.Clear();
        if (distance - _segments[^1].Distance > 1e-4f) _points.Add(new(time, a, b, distance));
        for (int i = _segments.Count - 1; i >= 0; i--) _points.Add(_segments[i]);
        Vector3 previousSide = side;
        for (int i = 0; i < _points.Count; i++)
        {
            var s = _points[i];
            float phase = Phase(s);
            float scale = trail.Scale.Evaluate(phase) * trail.EmitScale.Evaluate(0);
            Vector3 mid = Position(s);
            Vector3 half;
            if (Vector3.DistanceSquared(s.A, s.B) > 0.01f)
                half = (s.B - s.A) * (0.5f * scale);
            else
            {
                if (trail.CameraFacing)
                {
                    var tangent = Position(_points[Math.Max(0, i - 1)]) - Position(_points[Math.Min(_points.Count - 1, i + 1)]);
                    var widthAxis = Vector3.Cross(viewDirection, tangent);
                    if (widthAxis.LengthSquared() > 1e-8f)
                    {
                        widthAxis = Vector3.Normalize(widthAxis);
                        previousSide = Vector3.Dot(widthAxis, previousSide) < 0 ? -widthAxis : widthAxis;
                    }
                }
                float width = trail.HasAuthoredWidth ? scale : baseWidth * scale;
                half = previousSide * (0.5f * width);
            }
            var colour = new Vector4(trail.Red.Evaluate(phase), trail.Green.Evaluate(phase), trail.Blue.Evaluate(phase),
                trail.Opacity.Evaluate(phase) * trail.EmitOpacity.Evaluate(0));
            for (int channel = 0; channel < 4; channel++)
                colour[channel] *= trail.Colour.Evaluate(phase, channel) * trail.EmitColour.Evaluate(0, channel);
            colour.W = Math.Clamp(colour.W, 0, 1);
            output.Add(new(mid - half, mid + half, phase, distance - s.Distance, colour));
        }

        float Phase(Segment s) => Math.Clamp((time - s.Time) / trail.SegmentDuration, 0, 1);
        Vector3 Position(Segment s) => s.Centre + new Vector3(0, 0, trail.VelocityWorldZ.Evaluate(Phase(s)) * (time - s.Time));
    }
}
