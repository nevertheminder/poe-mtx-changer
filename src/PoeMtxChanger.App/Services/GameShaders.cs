using System.IO;
using PoeMtxChanger.Core.Formats.Shaders;
using PoeMtxChanger.Core.Rendering;
using PoeMtxChanger.Core.Resources;
using PoeMtxChanger.Core.Storage;
using PoeMtxChanger.Core.Util;

namespace PoeMtxChanger.App.Services;

/// <summary>A material compiled from the game's own shader fragments, ready for the Direct3D preview.</summary>
/// <param name="Textures">Texture paths and colour spaces per register t0…; loaded by the preview loader.</param>
/// <param name="BlendState">Native blend state (see <see cref="ComposedMaterialShader.PreviewBlendState"/>).</param>
public sealed record PreviewGameShader(byte[] Bytecode, IReadOnlyList<MaterialTextureBinding> Textures, PreviewOpacity? Blend,
    bool LightingDisabled, IReadOnlyList<string> Notes, uint BlendState = 0, VertexEffects Vertex = default);

/// <summary>
/// Composes and compiles preview pixel shaders for .mat files from the shader library in the user's installation.
/// The library is parsed once per opened archive; compiled bytecode is cached on disk.
/// </summary>
public static class GameShaders
{
    private static readonly Lock Gate = new();
    private static IResourceSource? _source;
    private static MaterialShaderBuilder? _builder, _particleBuilder;
    private static readonly Dictionary<string, FxGraph?> Graphs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (PreviewGameShader? Shader, string? Issue)> Materials = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HlslCompiler Compiler = new(Path.Combine(AppPaths.DataRoot, "shadercache"));

    /// <summary>Builds the shader for a material, or returns null with the reason (missing graphs, compile error …).</summary>
    /// <param name="particle">Compose the engine's particle variant (PARTICLE: omni lighting).</param>
    /// <param name="drawBlend">Blend mode of the draw, e.g. a particle emitter's blend_mode.</param>
    /// <param name="appended">Materials whose graph instances the engine appends to this one (EPK render passes of
    /// type Append, e.g. a weapon effect's glowing cracks on the weapon's own material).</param>
    public static PreviewGameShader? TryBuild(IResourceSource source, string materialPath, out string? issue, bool particle = false, string? drawBlend = null,
        IReadOnlyList<string>? appended = null)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(source, _source)) Reset(source);
            var key = $"{materialPath}|{particle}|{drawBlend}|{string.Join(";", appended ?? [])}";
            if (Materials.TryGetValue(key, out var cached)) { issue = cached.Issue; return cached.Shader; }
            var result = Build(source, materialPath, particle ? _particleBuilder : _builder, drawBlend, appended ?? []);
            Materials[key] = result;
            issue = result.Issue;
            return result.Shader;
        }
    }

    private static void Reset(IResourceSource source)
    {
        _source = source;
        Graphs.Clear();
        Materials.Clear();
        string Text(string p) => ReferenceExtractor.DecodeText(source.Read(p)) ?? "";
        var library = FfxLibrary.Parse(source.EnumeratePaths()
            .Where(p => p.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".ffx", StringComparison.OrdinalIgnoreCase))
            .Select(p => (p, Text(p))));
        FxGraph? Graph(string p) => Graphs.TryGetValue(p, out var g) ? g : Graphs[p] = source.Exists(p) ? FxGraph.Parse(p, Text(p)) : null;
        string? Include(string p) => source.Exists(p) ? Text(p) : null;
        _builder = library.Fragments.Count == 0 ? null : new MaterialShaderBuilder(library, Graph, Include);
        _particleBuilder = library.Fragments.Count == 0 ? null : new MaterialShaderBuilder(library, Graph, Include) { Particle = true };
    }

    private static (PreviewGameShader? Shader, string? Issue) Build(IResourceSource source, string materialPath, MaterialShaderBuilder? builder, string? drawBlend,
        IReadOnlyList<string> appended)
    {
        if (builder is null) return (null, "shader library not found in the game files");
        ComposedMaterialShader composed;
        FxMaterial Read(string path) => FxMaterial.Parse(path, ReferenceExtractor.DecodeText(source.Read(path)) ?? "");
        try
        {
            var material = Read(materialPath);
            foreach (var pass in appended) material.Instances.AddRange(Read(pass).Instances);
            composed = builder.Build(material, drawBlend);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        { return (null, "compose: " + ex.Message); }
        if (composed.EmittedNodes == 0) return (null, "no material nodes");
        var compiled = Compiler.Compile(composed.Hlsl, ComposedMaterialShader.EntryPoint, ComposedMaterialShader.Profile);
        if (compiled.Bytecode is null) return (null, "compile: " + FirstError(compiled.Error));
        return (new PreviewGameShader(compiled.Bytecode, composed.Textures, Blend(composed.BlendMode), composed.LightingDisabled, composed.Notes,
            composed.PreviewBlendState, composed.Vertex), null);
    }

    private static string FirstError(string? text) =>
        text?.Split('\n').FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))?.Trim() ?? text ?? "unknown error";

    /// <summary>Blend mode of the composed graphs; null keeps whatever the material reader decided.</summary>
    internal static PreviewOpacity? Blend(string? mode) => mode switch
    {
        "Opaque" or "OpaqueNoShadow" => PreviewOpacity.Opaque,
        "AlphaTest" or "AlphaTestWithShadow" or "AlphaTestNoGI" => PreviewOpacity.Cutout,
        "Additive" or "Addablend" or "SubtractiveBlend" => PreviewOpacity.Additive,
        "MultiplicitiveBlend" => PreviewOpacity.Multiply,
        "AlphaBlend" or "AlphaBlendNoGI" or "NoZWriteAlphaBlend" or "DownScaledAlphaBlend" or "ForegroundAlphaBlend" => PreviewOpacity.Transparent,
        _ => null,
    };
}
