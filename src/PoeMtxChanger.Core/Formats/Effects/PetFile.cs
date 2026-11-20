using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace PoeMtxChanger.Core.Formats.Effects;

/// <summary>
/// A keyed curve of a particle or trail definition: times in 0…1 with one or more channels per key.
/// "Smooth" keys ease into the next key (smoothstep); others interpolate linearly.
/// </summary>
public sealed class EffectCurve
{
    public required float[] Times { get; init; }
    /// <summary>Values[key][channel].</summary>
    public required float[][] Values { get; init; }
    public required bool[] Smooth { get; init; }
    public int Channels => Values.Length == 0 ? 0 : Values[0].Length;

    public static EffectCurve Constant(params float[] values) => new() { Times = [0], Values = [values], Smooth = [false] };

    /// <summary>Evenly spaced samples (the legacy and .trl form).</summary>
    public static EffectCurve Sampled(float[] samples) => samples.Length == 0 ? Constant(0) : new()
    {
        Times = samples.Length == 1 ? [0] : samples.Select((_, i) => (float)i / (samples.Length - 1)).ToArray(),
        Values = samples.Select(v => new[] { v }).ToArray(),
        Smooth = new bool[samples.Length],
    };

    public float Evaluate(float t, int channel = 0)
    {
        if (Values.Length == 0) return 0;
        channel = Math.Min(channel, Channels - 1);
        if (t <= Times[0] || Times.Length == 1) return Values[0][channel];
        for (int i = 1; i < Times.Length; i++)
        {
            if (t > Times[i]) continue;
            float span = Times[i] - Times[i - 1];
            float f = span <= 1e-6f ? 1 : (t - Times[i - 1]) / span;
            if (Smooth[i - 1]) f = f * f * (3 - 2 * f);
            return float.Lerp(Values[i - 1][channel], Values[i][channel], f);
        }
        return Values[^1][channel];
    }

    public float Max(int channel = 0) => Values.Length == 0 ? 0 : Values.Max(v => v[Math.Min(channel, v.Length - 1)]);

    /// <summary>Combines single-channel curves (v3+ files store colour as one curve per channel).</summary>
    public static EffectCurve Combine(IReadOnlyList<EffectCurve> channels)
    {
        var times = channels.SelectMany(c => c.Times).Distinct().Order().ToArray();
        return new()
        {
            Times = times,
            Values = times.Select(t => channels.Select(c => c.Evaluate(t)).ToArray()).ToArray(),
            Smooth = times.Select(t => channels.Any(c => Array.IndexOf(c.Times, t) is >= 0 and var k && c.Smooth[k])).ToArray(),
        };
    }
}

/// <summary>One emitter block of a .pet particle definition.</summary>
public sealed class PetEmitter
{
    public required string Kind { get; init; }
    public required string Material { get; init; }
    public string? Mesh { get; init; }
    /// <summary>Emitter blend mode (blend_mode / blend_type), e.g. AlphaBlend, AdditiveBlend, MultiplicitiveBlend.</summary>
    public string? Blend { get; init; }
    public string RotationLock { get; init; } = "Random";
    public string FaceType { get; init; } = "FaceCamera";
    public Vector3 RotationOffset { get; init; }
    public Vector3? RotationOffsetMax { get; init; }
    public bool LockedToBone { get; init; }
    public bool Infinite { get; init; }
    public float EmitterDuration { get; init; } = 1;
    public float ParticleDuration { get; init; } = 1;
    public float ParticleDurationVariance { get; init; }
    public int InitialBurst { get; init; }
    public bool PersistentParticles { get; init; }
    public int FramesX { get; init; } = 1;
    public int FramesY { get; init; } = 1;
    public float AnimSpeed { get; init; }
    public bool AnimPlayOnce { get; init; }
    public bool RandomFlipX { get; init; }
    public bool RandomFlipY { get; init; }
    public float StretchVariance { get; init; }
    public float UpAccelerationVariance { get; init; }
    public float VelocityAlongVariance { get; init; }
    public float VelocityUpVariance { get; init; }
    public float ScaleVariance { get; init; }
    public float RotationSpeedVariance { get; init; }
    public float[] ColourVariance { get; init; } = [0, 0, 0, 0];

    // Curves over the particle's life (0…1).
    public EffectCurve Colour { get; init; } = EffectCurve.Constant(1, 1, 1, 1);
    public EffectCurve Opacity { get; init; } = EffectCurve.Constant(1);
    public EffectCurve Scale { get; init; } = EffectCurve.Constant(10);
    public EffectCurve Stretch { get; init; } = EffectCurve.Constant(1);
    public EffectCurve VelocityAlong { get; init; } = EffectCurve.Constant(0);
    public EffectCurve VelocityUp { get; init; } = EffectCurve.Constant(0);
    public EffectCurve UpAcceleration { get; init; } = EffectCurve.Constant(0);
    public EffectCurve RotationSpeed { get; init; } = EffectCurve.Constant(0);
    // Curves over the emitter's life (0…1).
    public EffectCurve ParticlesPerSecond { get; init; } = EffectCurve.Constant(0);
    public EffectCurve EmitColour { get; init; } = EffectCurve.Constant(1, 1, 1, 1);
    public EffectCurve EmitScale { get; init; } = EffectCurve.Constant(1);
    public EffectCurve ScatterCone { get; init; } = EffectCurve.Constant(0);
}

/// <summary>
/// Parser for .pet files (text, versions 2–4). Version 2 curves are "channels keys (t values… "interp")…",
/// version 3+ curves are "keys (t value interp)…" with one line per channel for colours.
/// </summary>
public static partial class PetFile
{
    [GeneratedRegex(@"\{\s*([A-Za-z]+Emitter)\s+""([^""]*)""([^{}]*)\}", RegexOptions.IgnoreCase)]
    private static partial Regex EmitterBlock();

    public static IReadOnlyList<PetEmitter> Parse(string text)
    {
        var result = new List<PetEmitter>();
        foreach (Match block in EmitterBlock().Matches(text))
        {
            var fields = Fields(block.Groups[3].Value);
            string? Str(string name) => fields.TryGetValue(name, out var l) ? l[0].Trim().Trim('"') : null;
            float Num(string name, float fallback = 0) => fields.TryGetValue(name, out var l) && Number(l[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()) is { } x ? x : fallback;
            Vector3? Angles(string name)
            {
                var parts = Str(name)?.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                return parts is { Length: 3 } && Number(parts[0]) is float x && Number(parts[1]) is float y && Number(parts[2]) is float z
                    ? new Vector3(x, y, z) * (MathF.PI / 180) : null;
            }
            EffectCurve? Curve(string name, int channels)
            {
                if (!fields.TryGetValue(name + "_V2", out var lines) && !fields.TryGetValue(name, out lines)) return null;
                return ParseCurve(lines, channels);
            }
            var mesh = Str("mesh_filename");
            result.Add(new PetEmitter
            {
                Kind = block.Groups[1].Value,
                Material = block.Groups[2].Value,
                Mesh = string.IsNullOrWhiteSpace(mesh) ? null : mesh,
                Blend = Str("blend_mode") ?? Str("blend_type"),
                RotationLock = Str("rotation_lock_type") ?? "Random",
                FaceType = Str("face_type") ?? "FaceCamera",
                RotationOffset = Angles("rotation_offset") ?? Vector3.Zero,
                RotationOffsetMax = Angles("rotation_offset_max"),
                LockedToBone = Num("is_locked_to_bone") != 0,
                Infinite = Num("is_infinite") != 0,
                EmitterDuration = Math.Max(0.01f, Num("emitter_duration", 1)),
                ParticleDuration = Math.Max(0.01f, Num("particle_duration", 1)),
                ParticleDurationVariance = Num("particle_duration_variance"),
                FramesX = Math.Clamp((int)Num("number_of_frames", 1), 1, 64),
                FramesY = Math.Clamp((int)Num("number_of_frames_y", 1), 1, 64),
                AnimSpeed = Num("anim_speed"),
                AnimPlayOnce = Num("anim_play_once") != 0,
                RandomFlipX = Num("random_flip_x") != 0,
                RandomFlipY = Num("random_flip_y") != 0,
                StretchVariance = Num("stretch_variance"),
                UpAccelerationVariance = Num("up_acceleration_variance"),
                VelocityAlongVariance = Num("velocity_along_variance"),
                VelocityUpVariance = Num("velocity_up_variance"),
                ScaleVariance = Num("scale_variance"),
                RotationSpeedVariance = Num("rotation_speed_variance"),
                ColourVariance = fields.TryGetValue("colour_variance", out var cv)
                    ? cv[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => Number(t) ?? 0).Concat([0f, 0, 0, 0]).Take(4).ToArray() : [0, 0, 0, 0],
                Colour = Curve("colour", 4) ?? PackedColour(Str("colour")) ?? EffectCurve.Constant(1, 1, 1, 1),
                Opacity = Curve("opacity", 1) ?? EffectCurve.Constant(1),
                Scale = Curve("scale", 1) ?? EffectCurve.Constant(10),
                Stretch = Curve("stretch", 1) ?? EffectCurve.Constant(1),
                VelocityAlong = Curve("velocity_along", 1) ?? EffectCurve.Constant(0),
                VelocityUp = Curve("velocity_up", 1) ?? EffectCurve.Constant(0),
                UpAcceleration = Curve("up_acceleration_graph", 1) ?? EffectCurve.Constant(0),
                RotationSpeed = Curve("rotation_speed", 1) ?? EffectCurve.Constant(0),
                ParticlesPerSecond = Curve("particles_per_second", 1) ?? EffectCurve.Constant(0),
                EmitColour = Curve("emit_colour", 4) ?? EffectCurve.Constant(1, 1, 1, 1),
                EmitScale = Curve("emit_scale", 1) ?? EffectCurve.Constant(1),
                ScatterCone = Curve("scatter_cone_size", 1) ?? EffectCurve.Constant(0),
            });
        }
        return result;
    }

    /// <summary>Field name → value lines (a field followed by indented numeric lines is a multi-line curve).</summary>
    private static Dictionary<string, List<string>> Fields(string body)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        foreach (var raw in body.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if ((char.IsDigit(line[0]) || line[0] is '-' or '.') && current != null) { current.Add(line); continue; }
            int space = line.IndexOfAny([' ', '\t']);
            var name = space < 0 ? line : line[..space];
            var rest = space < 0 ? "" : line[(space + 1)..].Trim();
            current = [rest];
            fields[name] = current;
        }
        return fields;
    }

    internal static EffectCurve? ParseCurve(IReadOnlyList<string> lines, int channels)
    {
        // Multi-line form: the first line is empty and each following line is one channel.
        var channelLines = lines.Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count > 1 && lines[0].Trim().Length == 0)
        {
            var curves = channelLines.Select(l => ParseSingle(Tokens(l))).OfType<EffectCurve>().ToList();
            return curves.Count == 0 ? null : EffectCurve.Combine(curves);
        }
        if (channelLines.Count == 0) return null;
        var tokens = Tokens(channelLines[0]);
        // Files with a v3 header can still use the channel-count/key-count form.
        if (tokens.Count >= 2 && int.TryParse(tokens[0], out int c) && int.TryParse(tokens[1], out int keys)
            && c == channels && keys >= 1 && (long)tokens.Count == 2L + (long)keys * (c + 2)
            && Number(tokens[^1]) == null)
        {
            var times = new float[keys]; var values = new float[keys][]; var smooth = new bool[keys];
            for (int k = 0; k < keys; k++)
            {
                int o = 2 + k * (c + 2);
                times[k] = Number(tokens[o]) ?? 0;
                values[k] = Enumerable.Range(0, c).Select(i => Number(tokens[o + 1 + i]) ?? 0).ToArray();
                smooth[k] = tokens[o + 1 + c].Equals("Smooth", StringComparison.OrdinalIgnoreCase);
            }
            return new EffectCurve { Times = times, Values = values, Smooth = smooth };
        }
        if (ParseSingle(tokens) is { } single) return single;
        // Legacy: evenly spaced samples.
        var samples = tokens.Select(Number).ToList();
        return channels == 1 && samples.All(x => x.HasValue) && samples.Count > 0
            ? EffectCurve.Sampled(samples.Select(x => x!.Value).ToArray()) : null;
    }

    private static EffectCurve? PackedColour(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var samples = new float[tokens.Length][];
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            // Legacy PETs print the packed ARGB integer without leading zeroes.
            if (token.Length is < 1 or > 8 || !uint.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
                return null;
            samples[i] = [((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f,
                (argb >> 24) / 255f];
        }
        return new EffectCurve
        {
            Times = Enumerable.Range(0, samples.Length).Select(i => i / (float)Math.Max(1, samples.Length - 1)).ToArray(),
            Values = samples,
            Smooth = new bool[samples.Length],
        };
    }

    private static EffectCurve? ParseSingle(List<string> tokens)
    {
        if (tokens.Count < 4 || !int.TryParse(tokens[0], out int keys) || keys < 1 || tokens.Count != 1L + keys * 3L) return null;
        if (!char.IsLetter(tokens[3][0])) return null; // "keys t v interp" has a word in every third position
        var times = new float[keys]; var values = new float[keys][]; var smooth = new bool[keys];
        for (int k = 0; k < keys; k++)
        {
            times[k] = Number(tokens[1 + k * 3]) ?? 0;
            values[k] = [Number(tokens[2 + k * 3]) ?? 0];
            smooth[k] = tokens[3 + k * 3].Equals("Smooth", StringComparison.OrdinalIgnoreCase);
        }
        return new EffectCurve { Times = times, Values = values, Smooth = smooth };
    }

    private static List<string> Tokens(string line) => line.Replace("\"", " ").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();

    internal static float? Number(string? token) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && float.IsFinite(x) ? x : null;
}

/// <summary>One block of a .trl trail definition, with segment curves normalized to the segment lifetime.</summary>
public sealed partial class TrlTrail
{
    public required string Material { get; init; }
    public float StartDistanceThreshold { get; init; }
    public float SegmentDistanceThreshold { get; init; } = 10;
    public float MaxSegmentDistance { get; init; } = 1000;
    public float SegmentDuration { get; init; } = 0.5f;
    public float TextureStretch { get; init; } = 1;
    public bool LockedToParent { get; init; }
    public bool CameraFacing { get; init; }
    public bool HasAuthoredWidth { get; init; }
    public int UvMode { get; init; }
    public EffectCurve Colour { get; init; } = EffectCurve.Constant(1, 1, 1, 1);
    public EffectCurve EmitColour { get; init; } = EffectCurve.Constant(1, 1, 1, 1);
    public EffectCurve Opacity { get; init; } = EffectCurve.Constant(1);
    public EffectCurve Scale { get; init; } = EffectCurve.Constant(1);
    public EffectCurve Red { get; init; } = EffectCurve.Constant(1);
    public EffectCurve Green { get; init; } = EffectCurve.Constant(1);
    public EffectCurve Blue { get; init; } = EffectCurve.Constant(1);
    public EffectCurve VelocityWorldZ { get; init; } = EffectCurve.Constant(0);
    public EffectCurve EmitOpacity { get; init; } = EffectCurve.Constant(1);
    public EffectCurve EmitScale { get; init; } = EffectCurve.Constant(1);

    [GeneratedRegex(@"\{([^{}]*?\bmaterial[ \t]+""([^""\r\n]+)""[^{}]*)\}", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Block();

    public static IReadOnlyList<TrlTrail> Parse(string text)
    {
        var result = new List<TrlTrail>();
        foreach (Match m in Block().Matches(text))
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in m.Groups[1].Value.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                int space = line.IndexOfAny([' ', '\t']);
                if (space > 0) fields[line[..space]] = line[(space + 1)..].Trim();
            }
            float Num(string name, float fallback) => fields.TryGetValue(name, out var v) && PetFile.Number(v.Split(' ')[0]) is { } x ? x : fallback;
            float duration = Math.Max(0.02f, Num("segment_duration", 0.5f));
            EffectCurve? Curve(string name, int channels = 1, bool segment = true)
            {
                if (!fields.TryGetValue(name, out var raw)) return null;
                int interpolation = raw.LastIndexOf('"');
                if (interpolation < 0) return PetFile.ParseCurve([raw], channels);
                // Some keyed TRL fields append variance after the final interpolation token.
                var curve = PetFile.ParseCurve([raw[..(interpolation + 1)]], channels);
                if (curve != null && segment)
                    for (int i = 0; i < curve.Times.Length; i++) curve.Times[i] /= duration;
                return curve;
            }
            bool authoredWidth = fields.TryGetValue("scale", out var scale) && scale.Contains('"');
            result.Add(new TrlTrail
            {
                Material = m.Groups[2].Value,
                StartDistanceThreshold = Num("start_distance_threshold", 0),
                SegmentDistanceThreshold = Math.Max(0.1f, Num("segment_distance_threshold", 10)),
                MaxSegmentDistance = Num("max_segment_distance", 1000),
                SegmentDuration = duration,
                TextureStretch = Num("texture_stretch", 1),
                LockedToParent = Num("locked_to_parent", 0) != 0,
                CameraFacing = Num("camera_facing", 0) != 0,
                HasAuthoredWidth = authoredWidth,
                UvMode = (int)Num("uv_mode", 0),
                Colour = Curve("color", 4) ?? EffectCurve.Constant(1, 1, 1, 1),
                EmitColour = Curve("emit_color", 4, segment: false) ?? EffectCurve.Constant(1, 1, 1, 1),
                Opacity = Curve("opacity") ?? EffectCurve.Constant(1),
                Scale = Curve("scale") ?? EffectCurve.Constant(1),
                Red = Curve("r") ?? EffectCurve.Constant(1),
                Green = Curve("g") ?? EffectCurve.Constant(1),
                Blue = Curve("b") ?? EffectCurve.Constant(1),
                VelocityWorldZ = Curve("velocity_world_z") ?? EffectCurve.Constant(0),
                EmitOpacity = Curve("emit_opacity", segment: false) ?? EffectCurve.Constant(1),
                EmitScale = Curve("emit_scale", segment: false) ?? EffectCurve.Constant(1),
            });
        }
        return result;
    }
}
