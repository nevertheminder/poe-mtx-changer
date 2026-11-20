using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoeMtxChanger.Core.Formats.Effects;

public sealed record GpuParticlePass(string Material, string? Mesh, string Facing);

public sealed record GpuParticleEmitter(int Index, string? UpdateMaterial, float EmitterDuration,
    float ParticleDuration, float DurationVariance, int Population, float Rate, int Burst,
    bool Continuous, bool Locked, IReadOnlyList<GpuParticlePass> Passes);

public static partial class GpuParticleFile
{
    [GeneratedRegex(@"(?s)\{\s*""gpu_emitters""\s*:\s*\[.*\}\s*\z")]
    private static partial Regex Definition();

    public static IReadOnlyList<GpuParticleEmitter> Parse(string text)
    {
        var definition = Definition().Match(text);
        if (!definition.Success) return [];
        using var json = JsonDocument.Parse(definition.Value);
        var result = new List<GpuParticleEmitter>();
        int index = 0;
        foreach (var source in json.RootElement.GetProperty("gpu_emitters").EnumerateArray().Take(256))
        {
            int id = index++;
            if (source.ValueKind != JsonValueKind.Object ||
                !source.TryGetProperty("render_passes", out var passes) || passes.ValueKind != JsonValueKind.Array)
                continue;
            float min = Math.Clamp(ReadNumber(source, "particle_duration_min", 1), .01f, 60);
            float max = Math.Clamp(ReadNumber(source, "particle_duration_max", min), min, 60);
            float life = (min + max) * .5f;
            float duration = Math.Clamp(ReadNumber(source, "emitter_duration_max", 1), .01f, 60);
            int count = (int)Math.Clamp(ReadNumber(source, "particles_count", 0), 0, 100_000);
            bool continuous = ReadBool(source, "is_continuous") && !ReadBool(source, "emit_once");
            bool useRate = ReadBool(source, "use_pps");
            float burstFraction = Math.Clamp(ReadNumber(source, "emit_burst", 0) / 100, 0, 1);
            int burst = (int)MathF.Round(count * (continuous || burstFraction > 0 ? burstFraction : 1));
            float rate = continuous
                ? Math.Clamp(useRate ? ReadNumber(source, "particles_per_sec", count) : count / life, 0, 10_000)
                : 0;
            var mesh = ReadString(source, "particle_mesh");
            var renderPasses = new List<GpuParticlePass>();
            foreach (var pass in passes.EnumerateArray().Take(8))
            {
                if (pass.ValueKind != JsonValueKind.Object || ReadString(pass, "render_material") is not {} material)
                    continue;
                var passMesh = ReadBool(pass, "overwrite_mesh") ? ReadString(pass, "render_mesh") : mesh;
                renderPasses.Add(new(material, passMesh, ReadString(pass, "face_lock") ?? "Camera"));
            }
            if (renderPasses.Count > 0 && (rate > 0 || burst > 0))
                result.Add(new(id, ReadString(source, "update_material"), duration, life, (max - min) / (2 * life),
                    count, rate, burst, continuous, ReadBool(source, "lock_translation_to_bone"), renderPasses));
        }
        return result;
    }

    private static float ReadNumber(JsonElement item, string field, float fallback) =>
        item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetSingle(out float number) && float.IsFinite(number) ? number : fallback;

    private static bool ReadBool(JsonElement item, string field) =>
        item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement item, string field) =>
        item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is {Length:>0} text ? text : null;
}
