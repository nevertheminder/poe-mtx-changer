using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PoeMtxChanger.Core.Formats.Shaders;

namespace PoeMtxChanger.Core.Rendering;

public sealed record MaterialTextureBinding(int Slot, string? Path, bool Srgb, string Node);

/// <summary>A pixel shader generated for one .mat from the game's own fragment code.</summary>
public sealed class ComposedMaterialShader
{
    public required string MaterialPath { get; init; }
    public required string Hlsl { get; init; }
    public const string EntryPoint = "PmcMaterialPixel";
    public const string Profile = "ps_5_0";
    public required IReadOnlyList<MaterialTextureBinding> Textures { get; init; }
    public string? BlendMode { get; init; }
    public required string LightingModel { get; init; }
    public bool LightingDisabled { get; init; }

    /// <summary>
    /// Native preview blend state for <see cref="BlendMode"/>: 0 opaque / alpha test (writes depth), 1 alpha blend,
    /// 3 multiply, 4 additive (one/one), 5 premultiplied alpha, 6 subtractive, 7 refraction.
    /// </summary>
    public uint PreviewBlendState => MaterialShaderBuilder.BlendDefine(BlendMode) switch
    {
        "ALPHA_BLEND" => 1,
        "MULTIPLICITIVE_BLEND" => 3,
        "ADDITIVE_BLEND" => 4,
        "PREMULTIPLIED_ALPHA_BLEND" => 5,
        "SUBTRACTIVE_BLEND" => 6,
        "PMC_SHIMMER" => 7,
        _ => 0,
    };
    /// <summary>Things the preview does not reproduce yet (vertex animation, engine inputs, unknown nodes …).</summary>
    public required IReadOnlyList<string> Notes { get; init; }
    public int EmittedNodes { get; init; }
    /// <summary>Vertex inflation and depth bias the preview's vertex shader applies for this material.</summary>
    public VertexEffects Vertex { get; init; }
}

/// <summary>
/// Composes the pixel stages of a material (all graph instances of a .mat, in stage order) into HLSL using the
/// fragment bodies from the game's Shaders/Renderer/*.ffx. Vertex stages, scene inputs and the engine's own lighting
/// pass are substituted by preview equivalents and listed in <see cref="ComposedMaterialShader.Notes"/>.
/// </summary>
public sealed partial class MaterialShaderBuilder(FfxLibrary library, Func<string, FxGraph?> loadGraph, Func<string, string?>? includeSource = null)
{
    private static readonly string[] BaseStages =
    [
        "VertexInit", "LocalTransform", "WorldTransform", "ProjectionTransform", "VertexOutput", "VertexToPixel",
        "PixelInit", "UVSetup", "Texturing", "PreLighting", "Lighting", "CustomLighting", "LightingEnd", "PostLighting",
        "FogStage", "AlphaClip", "PixelOutput",
    ];
    private static readonly string[] Phases = ["_Init", "", "_Calc", "_Final", "_Pass"];

    public static int StageRank(string? stage)
    {
        if (stage is null) return -1;
        foreach (var (phase, p) in Phases.Select((x, i) => (x, i)).OrderByDescending(x => x.x.Length))
            if (phase.Length > 0 ? stage.EndsWith(phase, StringComparison.Ordinal) : true)
            {
                var b = phase.Length > 0 ? stage[..^phase.Length] : stage;
                int bi = Array.IndexOf(BaseStages, b);
                if (bi >= 0) return bi * 10 + p;
            }
        return 10_000;
    }
    public static readonly int PixelStart = StageRank("PixelInit_Init");

    private static readonly (int Rank, string Code)[] EngineSteps =
    [
        (StageRank("PreLighting_Calc"), "PmcResolveWorldNormal(surfaceData);"),
        (StageRank("Lighting_Init"), "PmcBeginLighting(surfaceData, emissionData);"),
        (StageRank("LightingEnd_Init"), "PmcEndLighting(surfaceData, emissionData);"),
        (StageRank("AlphaClip_Init"), "PmcAlphaTestClip(surfaceData);"),
    ];

    /// <summary>Compose for a particle draw (the engine's PARTICLE variant: omni lighting, particle inputs).</summary>
    public bool Particle { get; init; }

    /// <summary>Emitter spellings (AdditiveBlend …) to the graph blend mode names.</summary>
    public static string? NormaliseBlend(string? mode) => mode switch
    {
        null or "" => null,
        "AdditiveBlend" => "Additive",
        "Multiplicitive" or "Multiplicative" or "MultiplicativeBlend" => "MultiplicitiveBlend",
        "AdditiveAlphaBlend" or "AddAlphaBlend" or "AddablendBlend" => "Addablend",
        "SubtractiveBlend" => "SubtractiveBlend",
        _ => mode,
    };

    /// <summary>The engine macro for a graph blend mode, as tested by the game's own shader code.</summary>
    public static string? BlendDefine(string? blendMode) => blendMode switch
    {
        "Additive" => "ADDITIVE_BLEND",
        "Addablend" => "PREMULTIPLIED_ALPHA_BLEND",
        "MultiplicitiveBlend" => "MULTIPLICITIVE_BLEND",
        "SubtractiveBlend" => "SUBTRACTIVE_BLEND",
        "Shimmer" => "PMC_SHIMMER",
        "AlphaTest" or "AlphaTestNoGI" => "ALPHA_TEST",
        "AlphaTestWithShadow" => "ALPHA_TEST_WITH_SHADOW",
        "AlphaBlend" or "AlphaBlendNoGI" or "NoZWriteAlphaBlend" or "DownScaledAlphaBlend" or "ForegroundAlphaBlend" => "ALPHA_BLEND",
        "GBufferSurfaces" => "GBUFFER_BLENDMODE",
        _ => null,
    };

    /// <summary>Engine state bound through port semantics.</summary>
    private sealed record EngineVar(string Name, string Type);
    private static readonly Dictionary<string, EngineVar> SemanticVars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["surface_data"] = new("surfaceData", "SurfaceData"),
        ["emission_data"] = new("emissionData", "EmissionData"),
        ["semantics_data"] = new("semanticsData", "SemanticsData"),
        ["TEXCOORD0"] = new("pin.uv", "float2"),
        ["SV_IsFrontFace"] = new("pmc_front", "bool"),
        ["RANDOM_SEED"] = new("pmc_random_seed", "float"),
    };

    /// <summary>Vertex-stage values a pixel graph may read; the preview supplies them from interpolants.</summary>
    private static readonly Dictionary<string, string> VertexInputs = new(StringComparer.Ordinal)
    {
        ["VertexUV"] = "pin.uv", ["VertexUV2"] = "pin.uv", ["UV2"] = "pin.uv",
        ["VertexColor"] = "pin.color", ["VertexMeshColor"] = "pin.color", ["VertexAnimatedColor"] = "pin.color",
        ["VertexNormal"] = "normalize(pin.normal)", ["VertexPosition"] = "float4(pin.world, 1.0f)", ["VertexMeshPosition"] = "float4(pin.world, 1.0f)",
        ["VertexTangent"] = "float4(pmc_tangent, 1.0f)", ["VertexBinormal"] = "pmc_binormal",
        ["VertexProjectedPosition"] = "pin.position", ["VertexFlipbookOffset"] = "0.0f", ["VertexGroupID"] = "0u",
        // Trail vertices (the preview's ribbon): uv.x across the ribbon, uv.y from head (0) to tail (1).
        ["TrailUVMovingNormalized"] = "pin.uv", ["TrailUVStandingNormalized"] = "pin.uv",
        ["TrailUVMovingSquare"] = "pin.uv", ["TrailUVStandingSquare"] = "pin.uv",
        ["VertexTrailUVMovingNormalized"] = "pin.uv", ["VertexTrailUVStandingNormalized"] = "pin.uv",
        ["VertexTrailUVMovingSquare"] = "pin.uv", ["VertexTrailUVStandingSquare"] = "pin.uv",
        ["TrailRatio"] = "pin.uv.y", ["TrailGradient"] = "pin.uv.y", ["TrailPhase"] = "pin.uv.y",
        ["VertexTrailRatio"] = "pin.uv.y", ["VertexTrailGradient"] = "pin.uv.y", ["VertexTrailPhase"] = "pin.uv.y",
        ["TrailWidth"] = "1.0f", ["TrailScale"] = "1.0f", ["TrailTextureStretch"] = "1.0f", ["TrailRowMask"] = "1.0f",
        ["VertexTrailWidth"] = "1.0f", ["VertexTrailScale"] = "1.0f", ["VertexTrailTextureStretch"] = "1.0f", ["VertexTrailRowMask"] = "1.0f",
        ["TrailStartTime"] = "0.0f", ["TrailStartDistance"] = "0.0f", ["VertexTrailStartTime"] = "0.0f", ["VertexTrailStartDistance"] = "0.0f",
    };

    /// <summary>Engine-scoped uniforms with a preview value; anything else becomes 0.</summary>
    private static readonly Dictionary<string, string> EngineUniforms = new(StringComparer.Ordinal)
    {
        ["time"] = "pmc_time.x", ["global_time"] = "pmc_time.x", ["object_time"] = "pmc_time.x", ["start_time"] = "0.0f",
        ["camera_position"] = "pmc_eye.xyz", ["eye_position"] = "pmc_eye.xyz",
        // GetCameraPosition() of the CameraPos node (e.g. the depth bias of wp_outline_2pass) reads this one.
        ["scene_camera_position"] = "pmc_eye.xyz",
        // Render-target state of a full-resolution pass (also keeps the engine's integer divisions defined).
        ["pass_downscale"] = "1.0f", ["frame_resolution"] = "float4(pmc_time.zw, 1.0f / pmc_time.zw)",
        ["frame_to_dynamic_scale"] = "float4(1.0f, 1.0f, 1.0f, 1.0f)",
    };

    private sealed class Instance
    {
        public required int Index;
        public required FxGraph Graph;
        public required FxGraphInstance Mat;
        public Dictionary<FxNodeId, FxNode> Nodes = [];
        public Dictionary<(FxNodeId, string), List<FxLink>> Incoming = [];
    }

    private sealed class Ctx
    {
        public StringBuilder Code = new();
        public HashSet<string> Includes = new(StringComparer.Ordinal);
        public Dictionary<(int, FxNodeId), bool> Emitted = [];
        public HashSet<(int, FxNodeId)> Visiting = [];
        /// <summary>(instance, node, variable) → (hlsl variable, type)</summary>
        public Dictionary<(int, FxNodeId, string), (string Var, string Type)> Values = [];
        public List<MaterialTextureBinding> Textures = [];
        public SortedSet<string> Notes = new(StringComparer.Ordinal);
        public HashSet<string> Defines = new(StringComparer.Ordinal);
        /// <summary>Engine-provided uniforms (pass/object/pipeline scope), declared once at global scope.</summary>
        public Dictionary<string, string> EngineGlobals = new(StringComparer.Ordinal);
        /// <summary>Baked spline curves (LUT_WIDTH samples each), indexed by track number.</summary>
        public List<float[]> SplineTracks = [];
        public int Count;
    }

    public const int SplineLutWidth = 128; // LUT_WIDTH in the game's CommonDeclarations.inc

    /// <summary>Bakes one curve ({"points":[{"time","value","type"}]}) like the engine's spline LUT; returns its track index.</summary>
    private static int AddSplineTrack(Ctx ctx, JsonElement curve)
    {
        var points = curve.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.Array
            ? pts.EnumerateArray().Select(p => (T: Num(p, "time"), V: Num(p, "value"), Step: Str(p, "type") is "Constant" or "Step")).OrderBy(p => p.T).ToList()
            : [];
        var samples = new float[SplineLutWidth];
        for (int i = 0; i < SplineLutWidth; i++)
        {
            float t = i / (float)(SplineLutWidth - 1);
            samples[i] = points.Count switch
            {
                0 => 0f,
                _ when t <= points[0].T => points[0].V,
                _ when t >= points[^1].T => points[^1].V,
                _ => Interpolate(points, t),
            };
        }
        ctx.SplineTracks.Add(samples);
        return ctx.SplineTracks.Count - 1;

        static float Interpolate(List<(float T, float V, bool Step)> pts, float t)
        {
            int k = pts.FindLastIndex(p => p.T <= t);
            var a = pts[k];
            var b = pts[Math.Min(k + 1, pts.Count - 1)];
            if (a.Step || b.T <= a.T) return a.V;
            return a.V + (b.V - a.V) * (t - a.T) / (b.T - a.T);
        }
        static float Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : 0f;
    }

    /// <summary>Packs baked tracks into the engine's LUTData layout (two halves per uint, even sample in the low bits).</summary>
    private static string SplineLutDeclaration(Ctx ctx)
    {
        var sb = new StringBuilder($"static const LUTData spline_lut[{ctx.SplineTracks.Count}] = {{\n");
        foreach (var track in ctx.SplineTracks)
        {
            var words = Enumerable.Range(0, SplineLutWidth / 2).Select(i =>
                ((uint)BitConverter.HalfToUInt16Bits((Half)track[2 * i + 1]) << 16 | BitConverter.HalfToUInt16Bits((Half)track[2 * i])) + "u");
            sb.AppendLine($"    {{ {{ {string.Join(", ", words)} }} }},");
        }
        sb.Append("};");
        return sb.ToString();
    }

    private Dictionary<string, List<string>>? _symbolOwners;

    /// <summary>symbol (struct / function / macro name) → DECLARATIONS blocks defining it.</summary>
    private Dictionary<string, List<string>> SymbolOwners()
    {
        if (_symbolOwners != null) return _symbolOwners;
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var d in library.Declarations.Values)
        {
            var symbols = Regex.Matches(d.Body, @"\bstruct\s+(\w+)").Select(m => m.Groups[1].Value)
                // "type name(" at line start; "return float3(" / "else saturate(" are statements, not definitions.
                .Concat(Regex.Matches(d.Body, @"(?m)^\s*(?:inline\s+)?([A-Za-z_][\w<>]*)\s+([A-Za-z_]\w{3,})\s*\(")
                    .Where(m => !StatementKeywords.Contains(m.Groups[1].Value) && !BuiltinType().IsMatch(m.Groups[2].Value))
                    .Select(m => m.Groups[2].Value))
                .Concat(Regex.Matches(d.Body, @"(?m)^\s*#define\s+([A-Za-z_]\w{3,})").Select(m => m.Groups[1].Value))
                .Distinct();
            foreach (var s in symbols)
            {
                if (!owners.TryGetValue(s, out var list)) owners[s] = list = [];
                list.Add(d.Name);
            }
        }
        return _symbolOwners = owners;
    }

    /// <summary>
    /// Orders declarations so that blocks defining a symbol come before blocks using it (explicit includes keep
    /// their order; implicit uses found by AutoInclude are added as dependencies).
    /// </summary>
    internal List<FfxBlock> OrderByUse(IReadOnlyList<FfxBlock> blocks)
    {
        var owners = SymbolOwners();
        var set = blocks.ToDictionary(b => b.Name, StringComparer.Ordinal);
        var result = new List<FfxBlock>();
        var state = new Dictionary<string, bool>(StringComparer.Ordinal);
        void Visit(FfxBlock b)
        {
            if (state.ContainsKey(b.Name)) return;
            state[b.Name] = false;
            // Comments are stripped first: a name mentioned in a comment would create a false (often cyclic) edge.
            var code = Regex.Replace(b.Body, @"//[^\n]*|/\*.*?\*/", " ", RegexOptions.Singleline);
            var deps = b.Includes.Concat(Regex.Matches(code, @"\b[A-Za-z_]\w{3,}\b").Select(m => m.Value).Distinct()
                    // Owners are resolved within the chosen set: a symbol defined by several library blocks is
                    // still unambiguous when only one of them is part of this shader.
                    .Select(id => owners.TryGetValue(id, out var l) && l.Where(set.ContainsKey).Take(2).ToList() is [var only] ? only : null)
                    .OfType<string>())
                .Where(n => n != b.Name && set.ContainsKey(n)).Distinct();
            foreach (var d in deps)
                if (!state.TryGetValue(d, out var done) || done) Visit(set[d]); // skip back-edges (cycles)
            state[b.Name] = true;
            result.Add(b);
        }
        foreach (var b in blocks) Visit(b);
        return result;
    }

    /// <summary>Adds DECLARATIONS for symbols the generated code uses without an explicit include (unambiguous owners only).</summary>
    private void AutoInclude(Ctx ctx)
    {
        var owners = SymbolOwners();
        for (int pass = 0; pass < 8; pass++)
        {
            var text = ctx.Code + string.Concat(library.ResolveDeclarations(ctx.Includes).Select(d => d.Body));
            var used = Regex.Matches(text, @"\b[A-Za-z_]\w{3,}\b").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
            int before = ctx.Includes.Count;
            foreach (var id in used)
                if (owners.TryGetValue(id, out var list) && list.Count == 1) ctx.Includes.Add(list[0]);
            if (ctx.Includes.Count == before) return;
        }
    }

    /// <summary>Global declaration for an engine-provided uniform, with a preview value.</summary>
    private static void DeclareEngineGlobal(Ctx ctx, FfxPort u)
    {
        if (ctx.EngineGlobals.ContainsKey(u.Name)) return;
        var generic = Regex.Match(u.Type, @"^(\w+)<(.+)>$");
        string? decl = generic.Success ? generic.Groups[1].Value switch
            {
                "structBuffer" => $"StructuredBuffer<{generic.Groups[2].Value}> {u.Name};",
                "rw_structBuffer" => $"RWStructuredBuffer<{generic.Groups[2].Value}> {u.Name};",
                "texture" or "texture2d" => $"Texture2D<{generic.Groups[2].Value}> {u.Name};",
                "texelBuffer" => $"Buffer<{generic.Groups[2].Value}> {u.Name};",
                "rw_texture" => $"RWTexture2D<{generic.Groups[2].Value}> {u.Name};",
                _ => null,
            }
            : u.Type switch
            {
                "texture" => $"Texture2D {u.Name};",
                "texture3d" => $"Texture3D {u.Name};",
                "textureCube" => $"TextureCube {u.Name};",
                "sampler" => $"SamplerState {u.Name};",
                "byteBuffer" => $"ByteAddressBuffer {u.Name};",
                "rw_byteBuffer" => $"RWByteAddressBuffer {u.Name};",
                "texelBuffer" => $"Buffer<float4> {u.Name};",
                _ when Numeric().IsMatch(u.Type) => $"static {u.Type} {u.Name} = {EngineValue(u)};",
                _ => null,
            };
        if (decl is null) { ctx.Notes.Add($"engine uniform type not supported: {u.Type} {u.Name}"); return; }
        ctx.EngineGlobals[u.Name] = decl;
        if (!EngineUniforms.ContainsKey(u.Name) && Numeric().IsMatch(u.Type)) ctx.Notes.Add("engine uniform defaulted: " + u.Name);
    }

    private Dictionary<string, FfxPort>? _engineUniformIndex;

    /// <summary>Every engine-provided uniform declared anywhere in the library, by name (first declaration wins).</summary>
    private Dictionary<string, FfxPort> EngineUniformIndex()
    {
        if (_engineUniformIndex != null) return _engineUniformIndex;
        var index = new Dictionary<string, FfxPort>(StringComparer.Ordinal);
        foreach (var b in library.Fragments.Values.Concat(library.ExtensionPoints.Values).Concat(library.Declarations.Values))
            foreach (var u in b.Uniforms.Where(u => u.IsEngineUniform && u.Condition is null)) index.TryAdd(u.Name, u);
        return _engineUniformIndex = index;
    }

    private static string EngineValue(FfxPort u)
    {
        if (EngineUniforms.TryGetValue(u.Name, out var v))
            return Convert(v, v.EndsWith(".xyz") ? "float3" : v.StartsWith("float4(", StringComparison.Ordinal) ? "float4" : "float", u.Type);
        if (u.Type == "float4x4") return "float4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1)";
        if (u.Type == "float3x3") return "float3x3(1,0,0, 0,1,0, 0,0,1)";
        return u.DefaultValue != null ? Literal(u.Type, null, u.DefaultValue) : $"({u.Type})0";
    }

    /// <param name="drawBlend">Blend mode of the draw, such as the particle emitter's blend_mode.
    /// An explicit override in a graph instance still takes precedence over the draw.</param>
    public ComposedMaterialShader Build(FxMaterial material, string? drawBlend = null)
    {
        var ctx = new Ctx();
        var instances = new List<Instance>();
        string? blend = NormaliseBlend(drawBlend) ?? material.DefaultGraph?.BlendMode;
        string? lighting = material.DefaultGraph?.LightingModel;
        bool lightingDisabled = material.DefaultGraph?.LightingDisabled ?? false;
        int? alphaTestThreshold = material.DefaultGraph?.AlphaTestThreshold;
        foreach (var (mi, index) in material.Instances.Select((x, i) => (x, i)))
        {
            var g = loadGraph(mi.Parent);
            if (g is null) { ctx.Notes.Add("missing graph " + mi.Parent); continue; }
            if (g.ShaderGroups.Count > 0 && !g.ShaderGroups.Contains("Material")) { ctx.Notes.Add($"non-material graph skipped ({string.Join(",", g.ShaderGroups)}): {mi.Parent}"); continue; }
            blend = g.BlendMode ?? blend;
            lighting = g.LightingModel ?? lighting;
            lightingDisabled |= g.LightingDisabled;
            alphaTestThreshold = mi.AlphaTestThreshold ?? g.AlphaTestThreshold ?? alphaTestThreshold;
            var inst = new Instance { Index = index, Graph = g, Mat = mi };
            foreach (var n in g.Nodes)
                if (!inst.Nodes.TryAdd(n.Id, n)) ctx.Notes.Add($"duplicate node {n.Id} in {mi.Parent}");
            foreach (var l in g.Links)
            {
                var key = (l.Target.Node, l.Target.Variable);
                if (!inst.Incoming.TryGetValue(key, out var list)) inst.Incoming[key] = list = [];
                list.Add(l);
            }
            instances.Add(inst);
        }

        string model = lighting switch { "SpecGlossPbrMaterial" => "GGX", "Anisotropy" => "ANISOTROPY", _ => "PHONG" };
        ctx.Defines.Add("GGG_POE_1");
        ctx.Defines.Add(model);
        // The engine compiles every material once per blend mode with these macros (fog.ffx, internalonly.ffx ...).
        if (BlendDefine(blend) is { } blendDefine) ctx.Defines.Add(blendDefine);
        bool alphaTest = blend is "AlphaTest" or "AlphaTestWithShadow" or "AlphaTestNoGI";
        if (alphaTest) ctx.Defines.Add("HAS_ALPHA_TEST_CLIPPING");
        if (alphaTestThreshold.HasValue || alphaTest)
        {
            string threshold = alphaTestThreshold is int cut ? $"({cut}.0f / 255.0f)" : "0.5f";
            ctx.Defines.Add("PMC_ALPHA_TEST_THRESHOLD " + threshold);
            ctx.EngineGlobals["alpha_test_desc"] = "static float4 alpha_test_desc = float4(1.0f, PMC_ALPHA_TEST_THRESHOLD, 0.001f, 1.0f);";
        }
        if (!lightingDisabled) ctx.Defines.Add("HAS_LIGHTING");
        if (Particle) ctx.Defines.Add("PARTICLE");
        ctx.Defines.Add("OUTPUT_COLOR");
        // Developer aid: PMC_SHADER_DEBUG=alpha|colour shows the material's final alpha or unblended colour.
        switch (Environment.GetEnvironmentVariable("PMC_SHADER_DEBUG"))
        {
            case "alpha": ctx.Defines.Add("PMC_DEBUG_ALPHA"); break;
            case "colour": ctx.Defines.Add("PMC_DEBUG_COLOUR"); break;
        }
        ctx.Includes.UnionWith(["material_types", "pbr_materials", "lighting_models", "point_light"]);
        foreach (var decl in library.Declarations.Values.Where(d => Regex.IsMatch(d.Body, @"\bstruct\s+(SurfaceData|EmissionData|SemanticsData|TBN)\b|\bvoid\s+Init(Surface|Emission|SemanticsData)\b")))
            ctx.Includes.Add(decl.Name);

        // Extension-point nodes of the pixel stages, in pipeline order (graph order, reads before writes).
        var schedule = instances.SelectMany(inst => inst.Nodes.Values.Select(n => (inst, n)))
            .Select(x => (x.inst, x.n, ep: ExtensionPointOf(x.n.Id.Type, out bool read), read))
            .Where(x => x.ep != null)
            .Select(x => (x.inst, x.n, x.ep, x.read, rank: StageRank(x.n.Id.Stage ?? x.ep!.Attr("defaultstage"))))
            .ToList();
        var vertex = ReadVertexEffects(instances, ctx);
        foreach (var vertexWrite in schedule.Where(x => !x.read && x.rank < PixelStart).Select(x => x.n.Id.Type).Distinct())
            if (!(vertex.NormalOffset != 0 && vertexWrite is "VertexPosition" or "VertexNormal"))
                ctx.Notes.Add("vertex-stage write not previewed: " + vertexWrite);

        // Engine steps the game inserts between the graph's stages (internalonly.ffx), at the start of their stage.
        var hooks = new Queue<(int Rank, string Code)>(EngineSteps);
        void RunHooks(int rank)
        {
            while (hooks.Count > 0 && hooks.Peek().Rank <= rank) ctx.Code.AppendLine("    " + hooks.Dequeue().Code);
        }
        foreach (var s in schedule.Where(x => x.rank >= PixelStart).OrderBy(x => x.rank).ThenBy(x => x.inst.Index).ThenBy(x => x.read ? 0 : 1).ThenBy(x => x.n.Id.Index))
        {
            RunHooks(s.rank);
            if (s.read) EmitRead(ctx, s.inst, s.n, s.ep!);
            else EmitWrite(ctx, s.inst, s.n, s.ep!);
        }
        RunHooks(int.MaxValue);

        var hlsl = Assemble(ctx);
        return new ComposedMaterialShader
        {
            MaterialPath = material.Path,
            Hlsl = hlsl,
            Textures = ctx.Textures,
            BlendMode = blend,
            LightingModel = model,
            LightingDisabled = lightingDisabled,
            Notes = ctx.Notes.ToList(),
            EmittedNodes = ctx.Count,
            Vertex = vertex,
        };
    }

    private FfxBlock? ExtensionPointOf(string type, out bool read)
    {
        read = false;
        if (library.ExtensionPoints.TryGetValue(type, out var ep)) return ep;
        if (type.StartsWith("Input", StringComparison.Ordinal) && library.ExtensionPoints.TryGetValue(type[5..], out ep)) { read = true; return ep; }
        return null;
    }

    private static string Ident(string s) => Regex.Replace(s, @"[^A-Za-z0-9_]", "_");
    private static string VarName(Instance inst, FxNodeId id, string variable)
        => $"g{inst.Index}_{Ident(id.Type)}_{id.Index}{(id.Stage is null ? "" : "_" + Ident(id.Stage))}_{Ident(variable)}";

    // ---------------------------------------------------------------- extension points

    private void EmitRead(Ctx ctx, Instance inst, FxNode node, FfxBlock ep)
    {
        var key = (inst.Index, node.Id);
        if (ctx.Emitted.ContainsKey(key)) return;
        ctx.Emitted[key] = true;
        string type = ep.Attr("type") ?? "float";
        string v = VarName(inst, node.Id, "output");
        ctx.Values[(inst.Index, node.Id, "output")] = (v, type);
        ctx.Values[(inst.Index, node.Id, "value")] = (v, type);
        ctx.Code.AppendLine($"    {type} {v} = ({type})0; // read {ep.Name}");
        if (ep.Code.TryGetValue("read", out var readCode) && readCode.Trim().Length > 0)
        {
            ctx.Code.AppendLine("    {");
            ctx.Code.AppendLine($"        {type} value = {v};");
            ctx.Code.AppendLine(Bind(ctx, ep, readCode));
            ctx.Code.AppendLine($"        {v} = value;");
            ctx.Code.AppendLine("    }");
        }
        else if (VertexInputs.TryGetValue(ep.Name, out var engine))
            ctx.Code.AppendLine($"    {v} = {Convert(engine, GuessType(ep.Name), type)};");
        else ctx.Notes.Add("engine value not provided: " + ep.Name);
        ctx.Count++;
    }

    private void EmitWrite(Ctx ctx, Instance inst, FxNode node, FfxBlock ep)
    {
        var key = (inst.Index, node.Id);
        if (ctx.Emitted.ContainsKey(key)) return;
        ctx.Emitted[key] = true;
        string type = ep.Attr("type") ?? "float";
        var input = InputExpression(ctx, inst, node.Id, "input", type);
        if (input is null) return; // nothing connected: the stage keeps its current value
        string v = VarName(inst, node.Id, "input");
        ctx.Code.AppendLine($"    {type} {v} = {input}; // write {ep.Name}");
        ctx.Values[(inst.Index, node.Id, "output")] = (v, type);
        if (ep.Code.TryGetValue("write", out var writeCode) && writeCode.Trim().Length > 0)
        {
            ctx.Code.AppendLine("    {");
            ctx.Code.AppendLine($"        {type} value = {v};");
            ctx.Code.AppendLine(Bind(ctx, ep, writeCode));
            ctx.Code.AppendLine("    }");
        }
        else ctx.Notes.Add("engine-handled write not previewed: " + ep.Name);
        ctx.Count++;
    }

    /// <summary>Wraps a code snippet so that semantic-bound ports refer to the engine variables.</summary>
    private string Bind(Ctx ctx, FfxBlock block, string code)
    {
        var sb = new StringBuilder();
        var defined = new List<string>();
        foreach (var p in block.Ports.Where(p => p.Semantic != null))
        {
            if (SemanticVars.TryGetValue(p.Semantic!, out var ev))
            {
                if (p.Name != ev.Name) { sb.AppendLine($"        #define {p.Name} {ev.Name}"); defined.Add(p.Name); }
            }
            else
            {
                sb.AppendLine($"        {p.Type} {p.Name} = ({p.Type})0;");
                ctx.Notes.Add($"engine semantic not provided: {p.Semantic}");
            }
        }
        foreach (var inc in block.Includes) ctx.Includes.Add(inc);
        foreach (var u in block.Uniforms) DeclareEngineGlobal(ctx, u);
        sb.AppendLine(Indent(code, "        "));
        foreach (var d in defined) sb.AppendLine($"        #undef {d}");
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- fragments

    /// <summary>HLSL expression for the value arriving at (node, variable), or null when nothing is connected.</summary>
    private string? InputExpression(Ctx ctx, Instance inst, FxNodeId node, string variable, string targetType)
    {
        if (!inst.Incoming.TryGetValue((node, variable), out var links) || links.Count == 0) return null;
        if (links.Count == 1 && links[0].Target.Swizzle is null)
        {
            var src = SourceValue(ctx, inst, links[0].Source);
            return src is null ? null : Convert(src.Value.Expr, src.Value.Type, targetType);
        }
        // Several links (or a destination swizzle) writing parts of one input: resolve sources first, then assemble.
        var sources = links.Select(l => (Link: l, Value: SourceValue(ctx, inst, l.Source))).ToList();
        var tmp = $"pmc_in{ctx.Values.Count}_{ctx.Count}_{Ident(variable)}";
        ctx.Code.AppendLine($"    {targetType} {tmp} = ({targetType})0;");
        foreach (var (l, src) in sources)
        {
            if (src is null) continue;
            var sw = l.Target.Swizzle;
            ctx.Code.AppendLine(sw is null
                ? $"    {tmp} = {Convert(src.Value.Expr, src.Value.Type, targetType)};"
                : $"    {tmp}.{sw} = {Convert(src.Value.Expr, src.Value.Type, SwizzleType(targetType, sw.Length))};");
        }
        return tmp;
    }

    private (string Expr, string Type)? SourceValue(Ctx ctx, Instance inst, FxPortRef src)
    {
        if (!inst.Nodes.TryGetValue(src.Node, out var node))
        {
            ctx.Notes.Add($"link from unknown node {src.Node} in {inst.Mat.Parent}");
            return null;
        }
        var ep = ExtensionPointOf(node.Id.Type, out bool read);
        if (ep != null)
        {
            var rank = StageRank(node.Id.Stage ?? ep.Attr("defaultstage"));
            if (rank < PixelStart) // vertex-stage value read by pixel code
            {
                string type = ep.Attr("type") ?? "float";
                if (VertexInputs.TryGetValue(ep.Name, out var engine)) return Swz(Convert(engine, GuessType(ep.Name), type), type, src.Swizzle);
                ctx.Notes.Add("vertex value not provided: " + ep.Name);
                return Swz($"({type})0", type, src.Swizzle);
            }
            if (!ctx.Emitted.ContainsKey((inst.Index, node.Id)))
            {
                if (read) EmitRead(ctx, inst, node, ep); else EmitWrite(ctx, inst, node, ep);
            }
        }
        else if (library.Fragments.TryGetValue(node.Id.Type, out var frag)) EmitFragment(ctx, inst, node, frag);
        else
        {
            ctx.Notes.Add("unknown node type: " + node.Id.Type);
            return null;
        }
        if (!ctx.Values.TryGetValue((inst.Index, node.Id, src.Variable), out var v))
        {
            ctx.Notes.Add($"{node.Id.Type} has no output '{src.Variable}'");
            return null;
        }
        return Swz(v.Var, v.Type, src.Swizzle);
    }

    private static (string, string) Swz(string expr, string type, string? swizzle)
        => swizzle is null ? (expr, type) : ($"({expr}).{swizzle}", SwizzleType(type, swizzle.Length));

    private void EmitFragment(Ctx ctx, Instance inst, FxNode node, FfxBlock frag)
    {
        var key = (inst.Index, node.Id);
        if (ctx.Emitted.ContainsKey(key)) return;
        if (!ctx.Visiting.Add(key)) { ctx.Notes.Add("cycle at " + node.Id); return; }
        if (frag.Name == "OutCustomDepth") ctx.Defines.Add("PMC_CUSTOM_DEPTH");

        // Resolve inputs first (this may emit upstream fragments).
        var inputs = new List<(FfxPort Port, string? Expr)>();
        foreach (var p in frag.Inputs.Where(p => p.Semantic is null && Active(ctx, p)))
            inputs.Add((p, InputExpression(ctx, inst, node.Id, p.Name, p.Type)));

        ctx.Visiting.Remove(key);
        ctx.Emitted[key] = true;
        foreach (var inc in frag.Includes) ctx.Includes.Add(inc);

        var outs = frag.Outputs.Where(p => p.Semantic is null && Active(ctx, p)).ToList();
        foreach (var o in outs)
        {
            var v = VarName(inst, node.Id, o.Name);
            ctx.Values[(inst.Index, node.Id, o.Name)] = (v, o.Type);
            ctx.Code.AppendLine($"    {HlslType(o.Type)} {v} = {DefaultFor(o.Type)};");
        }
        var sb = new StringBuilder();
        sb.AppendLine($"    {{ // {frag.Name} #{node.Id.Index} ({Path.GetFileName(inst.Mat.Parent)})");
        // Engine macro: a stable per-node id (used for per-node random seeds).
        int seed = PreviewSeed.Create(inst.Mat.Parent + "\0" + node.Id.Type, node.Id.Index);
        sb.AppendLine($"        #define UNIQUE_NODE_ID {(uint)seed & 0x7FFFFFFF}u");
        var undefs = new List<string> { "UNIQUE_NODE_ID" };
        foreach (var (p, expr) in inputs)
            sb.AppendLine($"        {HlslType(p.Type)} {p.Name} = {expr ?? DefaultFor(p.Type)};");
        foreach (var o in outs.Where(o => o.Kind == FfxPortKind.Out))
            sb.AppendLine($"        {HlslType(o.Type)} {o.Name} = {DefaultFor(o.Type)};");
        // The game's source builder pairs every texture port with a bindless "<name>_index"; resources are bound
        // to fixed slots here, so the index is only a placeholder.
        foreach (var p in inputs.Select(x => x.Port).Concat(outs.Where(o => o.Kind == FfxPortKind.Out)))
            if (HlslType(p.Type).StartsWith("Texture", StringComparison.Ordinal) && !frag.Ports.Any(q => q.Name == p.Name + "_index"))
                sb.AppendLine($"        uint {p.Name}_index = 0u;");
        UniformDeclarations(ctx, inst, node, frag, sb, undefs);
        foreach (var p in frag.Ports.Where(p => p.Semantic != null && p.Kind != FfxPortKind.Uniform))
        {
            if (SemanticVars.TryGetValue(p.Semantic!, out var ev)) { if (p.Name != ev.Name) { sb.AppendLine($"        #define {p.Name} {ev.Name}"); undefs.Add(p.Name); } }
            else { sb.AppendLine($"        {HlslType(p.Type)} {p.Name} = {DefaultFor(p.Type)};"); ctx.Notes.Add("engine semantic not provided: " + p.Semantic); }
        }
        if (frag.Name == "DepthDistance" && outs.Any(o => o.Name == "distance" && o.Type == "float"))
            sb.AppendLine("        distance = PmcDepthDistance(pin.position, surfaceData.world_pos.xyz);");
        else if (frag.Name == "Project" && outs.Any(o => o.Name == "projected_pos" && o.Type == "float3")
            && inputs.Any(i => i.Port.Name == "world_pos" && i.Port.Type == "float3"))
            sb.AppendLine("        projected_pos = PmcProject(world_pos);");
        else
            sb.AppendLine(Indent(frag.Body, "        "));
        foreach (var o in outs) sb.AppendLine($"        {VarName(inst, node.Id, o.Name)} = {o.Name};");
        foreach (var u in undefs) sb.AppendLine($"        #undef {u}");
        sb.AppendLine("    }");
        ctx.Code.Append(sb);
        ctx.Count++;
    }

    private static bool Active(Ctx ctx, FfxPort p) => p.Condition is null || p.Condition.Split("&&").All(c => c.StartsWith('!') ? !ctx.Defines.Contains(c[1..]) : ctx.Defines.Contains(c));

    private void UniformDeclarations(Ctx ctx, Instance inst, FxNode node, FfxBlock frag, StringBuilder sb, List<string> undefs)
    {
        var nodeParams = node.Parameters.ValueKind == JsonValueKind.Array ? node.Parameters.EnumerateArray().ToList() : [];
        List<JsonElement> overrides = node.CustomParameter != null && inst.Mat.CustomParameters.TryGetValue(node.CustomParameter, out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().ToList() : [];
        int pi = 0;
        foreach (var u in frag.Uniforms)
        {
            if (u.IsEngineUniform)
            {
                DeclareEngineGlobal(ctx, u);
                if (u.Type.StartsWith("texture", StringComparison.OrdinalIgnoreCase)) ctx.Notes.Add("engine texture not provided: " + u.Name);
                continue;
            }
            JsonElement p = pi < nodeParams.Count ? nodeParams[pi] : default;
            JsonElement ov = pi < overrides.Count ? overrides[pi] : default;
            pi++;
            switch (u.Type)
            {
                case "texture" or "texture2d":
                {
                    string? path = Str(ov, "path") ?? Str(p, "path");
                    bool srgb = Bool(ov, "srgb") ?? Bool(p, "srgb") ?? false;
                    int slot = ctx.Textures.Count;
                    ctx.Textures.Add(new MaterialTextureBinding(slot, path, srgb, $"{node.Id} {u.Name}"));
                    if (path is null) ctx.Notes.Add($"texture without path: {node.Id}.{u.Name}");
                    sb.AppendLine($"        #define {u.Name} pmc_tex{slot}");
                    sb.AppendLine($"        #define {u.Name}_index {slot}u");
                    undefs.Add(u.Name);
                    undefs.Add(u.Name + "_index");
                    break;
                }
                case "sampler":
                    string sampler = (Str(ov, "value") ?? Str(p, "value") ?? u.DefaultValue) switch
                    {
                        "SamplerLinearClamp" or "SamplerLinearClampNoBias" or "SamplerLinearClampOffsetNoBias" => "pmc_sampler_clamp",
                        "SamplerPointClamp" or "SamplerPointClampNoBias" => "pmc_sampler_point",
                        _ => "pmc_sampler_wrap",
                    };
                    sb.AppendLine($"        #define {u.Name} {sampler}");
                    undefs.Add(u.Name);
                    break;
                case "splineGrayscale" or "splineColour" or "splineVariance":
                {
                    // The engine bakes curves into spline_lut and passes track indices; do the same.
                    var spline = ov.ValueKind == JsonValueKind.Object ? ov : p;
                    string[] channels = u.Type switch { "splineColour" => ["R", "G", "B", "A"], "splineVariance" => ["A", "B"], _ => ["A"] };
                    var curves = spline.ValueKind == JsonValueKind.Object
                        ? spline.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.Object && x.Value.TryGetProperty("points", out _)).ToList()
                        : [];
                    var tracks = new List<int>();
                    for (int c = 0; c < channels.Length; c++)
                    {
                        var curve = curves.FirstOrDefault(x => x.Name == channels[c]);
                        if (curve.Value.ValueKind != JsonValueKind.Object && c < curves.Count) curve = curves[c];
                        tracks.Add(curve.Value.ValueKind == JsonValueKind.Object ? AddSplineTrack(ctx, curve.Value) : -1);
                    }
                    var t = u.Type == "splineColour" ? $"int4({string.Join(", ", tracks)})" : u.Type == "splineVariance" ? $"int2({tracks[0]}, {tracks[1]})" : $"{tracks[0]}";
                    sb.AppendLine($"        {u.Type} {u.Name} = {t};");
                    break;
                }
                case "textureCube" or "texture3d":
                    // Not bound by the preview yet: a neutral engine dummy keeps the node compiling.
                    ctx.Notes.Add($"{u.Type} parameter not bound: {node.Id}.{u.Name}");
                    sb.AppendLine($"        #define {u.Name} {DefaultFor(u.Type)}");
                    sb.AppendLine($"        #define {u.Name}_index 0u");
                    undefs.Add(u.Name);
                    undefs.Add(u.Name + "_index");
                    break;
                default:
                    if (Regex.IsMatch(u.Type, @"^(float|int|uint|bool)\d?(x\d)?$"))
                        sb.AppendLine($"        {u.Type} {u.Name} = {Literal(u.Type, Value(ov) ?? Value(p), u.DefaultValue)};");
                    else
                    {
                        ctx.Notes.Add($"uniform type not supported: {u.Type} ({frag.Name}.{u.Name})");
                        sb.AppendLine($"        {u.Type} {u.Name} = ({u.Type})0;");
                    }
                    break;
            }
        }
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool? Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static JsonElement? Value(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("value", out var v) ? v : null;

    /// <summary>HLSL literal for a uniform from its JSON value (number, bool or array), or the declared default.</summary>
    internal static string Literal(string type, JsonElement? value, string? fallback)
    {
        var nums = new List<string>();
        if (value is { } v)
        {
            if (v.ValueKind == JsonValueKind.Array) foreach (var x in v.EnumerateArray()) nums.Add(Num(x));
            else nums.Add(Num(v));
        }
        else if (fallback != null) nums.AddRange(fallback.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Fmt(d) : "0"));
        if (nums.Count == 0) return $"({type})0";
        if (type == "bool") return nums[0] is "0" or "false" or "0.0" ? "false" : "true";
        // Saved parameters can carry more components than the port has (e.g. a float stored with a UI range).
        var shape = Numeric().Match(type);
        if (shape.Success)
        {
            int width = (shape.Groups[2].Success ? int.Parse(shape.Groups[2].Value) : 1) * (shape.Groups[3].Success ? int.Parse(shape.Groups[3].Value) : 1);
            if (nums.Count > width) nums = nums.Take(width).ToList();
        }
        return nums.Count == 1 ? $"({type})({nums[0]})" : $"{type}({string.Join(", ", nums)})";

        static string Num(JsonElement x) => x.ValueKind switch
        {
            JsonValueKind.Number => Fmt(x.GetDouble()),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => "0",
        };
    }

    private static string Fmt(double d) => double.IsFinite(d) ? d.ToString("0.0########", CultureInfo.InvariantCulture) : "0.0";

    // ---------------------------------------------------------------- types

    private static string GuessType(string engineName) => engineName switch
    {
        "VertexUV" or "VertexUV2" or "UV2" => "float2",
        "VertexColor" or "VertexMeshColor" or "VertexAnimatedColor" or "VertexPosition" or "VertexMeshPosition" or "VertexTangent" or "VertexProjectedPosition" => "float4",
        "VertexNormal" or "VertexBinormal" => "float3",
        "VertexGroupID" => "uint",
        _ => "float",
    };

    [GeneratedRegex(@"^(float|half|int|uint|bool)(\d)?(?:x(\d))?$")]
    private static partial Regex Numeric();

    [GeneratedRegex(@"^(float|half|int|uint|bool|double|min16float|min16int|min16uint)\d?(x\d)?$")]
    private static partial Regex BuiltinType();

    private static readonly HashSet<string> StatementKeywords =
        ["return", "else", "case", "if", "for", "while", "do", "switch", "break", "continue", "discard", "in", "out", "inout", "uniform", "const"];

    internal static string SwizzleType(string type, int length)
    {
        var m = Numeric().Match(type);
        var b = m.Success ? m.Groups[1].Value : "float";
        return length == 1 ? b : b + length;
    }

    /// <summary>Converts between scalar/vector numeric types the way the editor's implicit connections do.</summary>
    /// <summary>ffx resource type names → HLSL.</summary>
    internal static string HlslType(string t) => t switch
    {
        "texture" or "texture2d" => "Texture2D",
        "texture3d" => "Texture3D",
        "textureCube" => "TextureCube",
        "sampler" => "SamplerState",
        _ => t,
    };

    /// <summary>Value of an unconnected port: zero for data, a dummy resource for textures/samplers.</summary>
    internal static string DefaultFor(string t) => HlslType(t) switch
    {
        "Texture2D" => "pmc_engine_texture",
        "Texture3D" => "pmc_engine_texture3d",
        "TextureCube" => "pmc_engine_cube",
        "SamplerState" => "pmc_sampler_wrap",
        var h => $"({h})0",
    };

    internal static string Convert(string expr, string from, string to)
    {
        if (from == to || HlslType(from) == HlslType(to)) return expr;
        if (HlslType(from) != from || HlslType(to) != to) return expr; // resources: let the compiler judge
        var a = Numeric().Match(from);
        var b = Numeric().Match(to);
        if (!a.Success || !b.Success || a.Groups[3].Success || b.Groups[3].Success) return $"({to})({expr})";
        int na = a.Groups[2].Success ? int.Parse(a.Groups[2].Value) : 1;
        int nb = b.Groups[2].Success ? int.Parse(b.Groups[2].Value) : 1;
        string baseTo = b.Groups[1].Value;
        if (na == nb || na == 1) return $"(({to})({expr}))";
        if (na > nb) return $"(({to})(({expr}).{"xyzw"[..nb]}))";
        // Widening: pad with zeros, alpha/w with one.
        var pad = Enumerable.Range(na, nb - na).Select(i => i == 3 ? "1" : "0");
        string castFrom = a.Groups[1].Value == baseTo ? expr : $"({baseTo}{(na == 1 ? "" : na.ToString())})({expr})";
        return $"{to}({castFrom}, {string.Join(", ", pad)})";
    }

    private static string Indent(string code, string prefix)
        => string.Join("\n", code.Replace("\r", "").Split('\n').Select(l => l.Length == 0 ? l : prefix + l));

    // ---------------------------------------------------------------- assembly

    private string Assemble(Ctx ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Generated by PoE MTX Changer from the game's own shader fragments (local preview only).");
        sb.AppendLine("#pragma pack_matrix(row_major)");
        foreach (var d in ctx.Defines.Order()) sb.AppendLine($"#define {d}");
        sb.AppendLine(Prelude);
        AutoInclude(ctx);
        var declarations = OrderByUse(library.ResolveDeclarations(ctx.Includes.Order(StringComparer.Ordinal)));
        foreach (var decl in declarations)
            foreach (var u in decl.Uniforms) DeclareEngineGlobal(ctx, u);
        // Declarations also read engine uniforms that only some fragment/EP lists among its ports.
        var engineByName = EngineUniformIndex();
        var usedIds = Regex.Matches(string.Concat(declarations.Select(d => d.Body)), @"\b[A-Za-z_]\w*\b").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var id in usedIds)
            if (!ctx.EngineGlobals.ContainsKey(id) && engineByName.TryGetValue(id, out var eu)) DeclareEngineGlobal(ctx, eu);
        var structTypes = declarations.SelectMany(d => Regex.Matches(d.Body, @"\bstruct\s+(\w+)").Select(m => m.Groups[1].Value)).ToHashSet();
        var commonStructs = UnconditionalStructs(includeSource?.Invoke("Shaders/CommonDeclarations.inc") ?? "");
        // Buffers of structs that only exist for other draw types (e.g. BoneData under GPU_PARTICLE_DRAW_CALL) are left out.
        foreach (var g in ctx.EngineGlobals.ToList())
        {
            var elem = Regex.Match(g.Value, @"<(\w+)>");
            if (elem.Success && !structTypes.Contains(elem.Groups[1].Value) && !commonStructs.Contains(elem.Groups[1].Value)
                && !Numeric().IsMatch(elem.Groups[1].Value))
            {
                ctx.EngineGlobals.Remove(g.Key);
                ctx.Notes.Add($"engine buffer not declared for material draws: {g.Key}");
            }
        }
        if (ctx.SplineTracks.Count > 0) ctx.EngineGlobals["spline_lut"] = SplineLutDeclaration(ctx);
        // Globals whose element type is declared by a DECLARATIONS block must follow it.
        var early = ctx.EngineGlobals.Where(g => !structTypes.Any(t => g.Value.Contains("<" + t + ">"))).ToList();
        var late = ctx.EngineGlobals.Except(early).ToList();
        sb.AppendLine("// ---- engine-provided uniforms (preview values)");
        foreach (var g in early.OrderBy(g => g.Key, StringComparer.Ordinal)) sb.AppendLine(g.Value);
        foreach (var decl in declarations)
        {
            sb.AppendLine($"// ---- DECLARATIONS {decl.Name} ({decl.File})");
            sb.AppendLine(decl.Body);
        }
        foreach (var g in late.OrderBy(g => g.Key, StringComparer.Ordinal)) sb.AppendLine(g.Value);
        for (int i = 0; i < Math.Max(1, ctx.Textures.Count); i++) sb.AppendLine($"Texture2D pmc_tex{i} : register(t{i});");
        sb.AppendLine(PixelEntryStart);
        sb.Append(ctx.Code);
        sb.AppendLine(PixelEntryEnd);
        return InlineIncludes(ctx, sb.ToString());
    }

    /// <summary>Struct names declared outside any #if block.</summary>
    internal static HashSet<string> UnconditionalStructs(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        int depth = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#if", StringComparison.Ordinal)) depth++;
            else if (line.StartsWith("#endif", StringComparison.Ordinal)) depth = Math.Max(0, depth - 1);
            else if (depth == 0 && Regex.Match(line, @"^struct\s+(\w+)") is { Success: true } m) result.Add(m.Groups[1].Value);
        }
        return result;
    }

    [GeneratedRegex(@"^[ \t]*#include\s+""([^""]+)""[^\n]*$", RegexOptions.Multiline)]
    private static partial Regex IncludeLine();

    /// <summary>Replaces #include "Shaders/…" with the game's own include files (each at most once).</summary>
    private string InlineIncludes(Ctx ctx, string hlsl)
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int pass = 0; pass < 16 && IncludeLine().IsMatch(hlsl); pass++)
            hlsl = IncludeLine().Replace(hlsl, m =>
            {
                var path = m.Groups[1].Value.Replace('\\', '/');
                if (!done.Add(path)) return $"// (already included) {path}";
                var text = includeSource?.Invoke(path);
                if (text is null) { ctx.Notes.Add("include not found: " + path); return $"// (missing include) {path}"; }
                return $"// ---- include {path}\n{text}\n";
            });
        return hlsl;
    }

    /// <summary>Engine macros the game's platform layer normally provides, mapped to D3D11.</summary>
    private const string Prelude = """
        #define TEXTURE2D_DECL(name) Texture2D name
        #define TEXTURE3D_DECL(name) Texture3D name
        #define TEXTURECUBE_DECL(name) TextureCube name
        #define SAMPLER_DECL(name) SamplerState name
        #define SAMPLE_TEX2D(t, s, uv) (t).Sample(s, uv)
        #define SAMPLE_TEX2DLOD(t, s, uvw) (t).SampleLevel(s, (uvw).xy, (uvw).w)
        #define SAMPLE_TEX2DGRAD(t, s, uv, dx, dy) (t).SampleGrad(s, uv, dx, dy)
        #define SAMPLE_TEX2DPROJ(t, s, uvw) (t).Sample(s, (uvw).xy / (uvw).w)
        #define SAMPLE_TEX_BIAS(t, s, uv, b) (t).SampleBias(s, uv, b)
        #define SAMPLE_TEX3D(t, s, uvw) (t).Sample(s, uvw)
        #define SAMPLE_TEX3DLOD(t, s, uvw) (t).SampleLevel(s, (uvw).xyz, (uvw).w)
        #define SAMPLE_TEXCUBELOD(t, s, uvzw) (t).SampleLevel(s, (uvzw).xyz, (uvzw).w)
        #define CALCULATE_TEX2DLOD(t, s, uv) (t).CalculateLevelOfDetail(s, uv)
        #define LOAD_TEX(t, loc) (t).Load(loc)
        #define GET_DIMENSIONS_2D(t, x, y) (t).GetDimensions(x, y)
        #define GET_DIMENSIONS_2D_LOD(t, l, x, y, z) (t).GetDimensions(l, x, y, z)
        #define GATHER_R(t, s, p) (t).GatherRed(s, p)
        #define GATHER_G(t, s, p) (t).GatherGreen(s, p)
        #define GATHER_B(t, s, p) (t).GatherBlue(s, p)
        #define GATHER_A(t, s, p) (t).GatherAlpha(s, p)
        #define SAMPLER_FEEDBACK(t, uv)
        #define SAMPLE_TEXCUBE(t, s, dir) (t).Sample(s, dir)
        #define SAMPLE_CMPLEVELZERO(t, s, uvzw) (t).SampleCmpLevelZero(s, (uvzw).xy, (uvzw).z)
        #define SAMPLER_CMPDECL(name) SamplerComparisonState name
        #define BIND_SLOT_SAMPLER(n)
        #define STRUCTURED_BUFFER_DECL(name, type) StructuredBuffer<type> name
        #define RWSTRUCTURED_BUFFER_DECL(name, type) RWStructuredBuffer<type> name
        #define BYTE_ADDRESS_BUFFER_DECL(name) ByteAddressBuffer name
        #define RWBYTE_ADDRESS_BUFFER_DECL(name) RWByteAddressBuffer name
        #define TEXEL_BUFFER_DECL(name, type) Buffer<type> name
        #define RWTEXEL_BUFFER_DECL(name, type) RWBuffer<type> name
        #define TEXTURE2D_T_DECL(name, type) Texture2D<type> name
        #define RWTEXTURE2D_DECL(name, type) RWTexture2D<type> name
        #define RWTEXTURE2D_DECL_GLC(name, type) globallycoherent RWTexture2D<type> name
        #define VERTEX_HAS_COLOR(f) 0
        #define VERTEX_HAS_COLOR3(f) 0
        #define VERTEX_HAS_NORMAL(f) 1
        #define VERTEX_HAS_BLENDINDICES(f) 0
        #define VERTEX_HAS_BLENDWEIGHT(f) 0
        #define ATOMIC_ADD(b, a, v) (b).InterlockedAdd(a, v)
        #define ATOMIC_FETCH_ADD(b, a, v, o) (b).InterlockedAdd(a, v, o)
        #define ATOMIC_OR(b, a, v) (b).InterlockedOr(a, v)
        #define ATOMIC_FETCH_OR(b, a, v, o) (b).InterlockedOr(a, v, o)
        #define ATOMIC_MAX(b, a, v) (b).InterlockedMax(a, v)
        #define ATOMIC_EXCHANGE(b, a, v, o) (b).InterlockedExchange(a, v, o)
        #define ATOMIC_FETCH_ADD_V(d, v, o) InterlockedAdd(d, v, o)
        #define ATOMIC_EXCHANGE_V(d, v, o) InterlockedExchange(d, v, o)
        #define ATOMIC_MAX_V(d, v) InterlockedMax(d, v)
        #define ATOMIC_MIN_V(d, v) InterlockedMin(d, v)
        #define COUNTBITS(x) countbits(x)
        #define FIRSTBITHIGH(x) firstbithigh(x)
        #define FIRSTBITLOW(x) firstbitlow(x)
        #define MEMORY_BARRIER_GROUP GroupMemoryBarrierWithGroupSync
        #define MEMORY_BARRIER_SUB_GROUP GroupMemoryBarrier
        #define DX11_UNROLL [unroll]
        #define DX11_UNROLL_N(n) [unroll(n)]
        #define COMPUTE_DECL_NUM_THREADS(x, y, z) [numthreads(x, y, z)]
        // Spline handles are LUT track indices (curves are baked into spline_lut).
        #define splineGrayscale int
        #define splineColour int4
        #define splineVariance int2
        #define CBUFFER_BEGIN(name) cbuffer name {
        #define CBUFFER_END };
        // The engine's common header (structs such as LightInfo, bindless macro variants) comes from the game files.
        #include "Shaders/CommonDeclarations.inc"

        cbuffer PmcFrame : register(b0)
        {
            float4 pmc_time;        // x = seconds, y = lighting preset
            float4 pmc_eye;         // camera position
            float4 pmc_light_dir;   // direction towards the key light
            float4 pmc_light_color;
            float4 pmc_ambient;
            float4 pmc_depth_info;   // near, far, samples
            row_major float4x4 pmc_view_projection;
            float4 pmc_effect_light_count;            // x = number of lights below
            float4 pmc_effect_light_position[8];      // world position
            float4 pmc_effect_light_colour[8];        // rgb colour, a = cut-off radius
        };
        SamplerState pmc_sampler_wrap : register(s0);
        SamplerState pmc_sampler_clamp : register(s1);
        SamplerState pmc_sampler_point : register(s2);
        SamplerComparisonState pmc_sampler_depth : register(s3);
        Texture2D pmc_engine_texture : register(t31);
        Texture3D pmc_engine_texture3d : register(t30);
        TextureCube pmc_engine_cube : register(t29);
        #define pmc_engine_sampler pmc_sampler_clamp
        // Engine-wide samplers (declared by the game's platform layer, not in its shader files).
        #define SamplerLinearWrap pmc_sampler_wrap
        #define SamplerLinearWrapOffsetNoBias pmc_sampler_wrap
        #define SamplerLinearWrapNoBias pmc_sampler_wrap
        #define SamplerLinearWrapClampWrap pmc_sampler_wrap
        #define SamplerLinearClampWrapWrap pmc_sampler_wrap
        #define SamplerDynamicWrap pmc_sampler_wrap
        #define SamplerLinearClamp pmc_sampler_clamp
        #define SamplerLinearClampOffsetNoBias pmc_sampler_clamp
        #define SamplerLinearClampNoBias pmc_sampler_clamp
        #define SamplerLinearBorder pmc_sampler_clamp
        #define SamplerLinearBorderOffsetNoBias pmc_sampler_clamp
        #define SamplerPointClamp pmc_sampler_point
        #define SamplerPointClampNoBias pmc_sampler_point
        #define SamplerPointWrap pmc_sampler_point
        #define SamplerPointWrapNoBias pmc_sampler_point
        #define SamplerDepth pmc_sampler_depth
        """;

    private const string PixelEntryStart = """
        struct PmcPixelIn
        {
            float4 position : SV_Position;
            float3 world : TEXCOORD0;
            float3 normal : TEXCOORD1;
            float2 uv : TEXCOORD2;
            float4 color : COLOR0;
        };

        Texture2D<float> pmc_scene_depth : register(t16);
        Texture2DMS<float> pmc_scene_depth_ms : register(t17);

        float PmcLinearDepth(float z)
        {
            float n = pmc_depth_info.x, f = pmc_depth_info.y;
            return n * f / max(.00001f, f - z * (f - n));
        }

        float PmcMaterialDepth(float3 world, float rasterDepth)
        {
        #if defined(PMC_CUSTOM_DEPTH)
            float4 clip = mul(float4(world, 1.0f), pmc_view_projection);
            if (clip.w > .00001f) return saturate(clip.z / clip.w);
        #endif
            return rasterDepth;
        }

        float3 PmcProject(float3 world)
        {
            float4 clip = mul(float4(world, 1.0f), pmc_view_projection);
            float3 ndc = clip.xyz / max(clip.w, .00001f);
            return float3((ndc.xy * float2(.5f, -.5f) + .5f) * pmc_time.zw, ndc.z);
        }

        float PmcDepthDistance(float4 position, float3 world)
        {
            int2 pixel = int2(position.xy);
            float behind = 0;
            int samples = (int)pmc_depth_info.z;
            if (samples > 1)
            {
                [loop] for (int i = 0; i < samples; i++)
                    behind = max(behind, pmc_scene_depth_ms.Load(pixel, i));
            }
            else
                behind = pmc_scene_depth.Load(int3(pixel, 0));
            if (behind <= 0) return pmc_depth_info.y; // no usable depth surface for this pass
            return max(0, PmcLinearDepth(behind) - PmcLinearDepth(PmcMaterialDepth(world, position.z)));
        }

        void PmcResolveWorldNormal(inout SurfaceData s)
        {
            float3 n = s.tbn_normal;
            s.world_normal = normalize(s.tbn_basis.tangent * n.x + s.tbn_basis.binormal * n.y + s.tbn_basis.normal * n.z);
        }

        void PmcAddLight(float3 towardLight, float3 colour, SurfaceData surfaceData, inout EmissionData emissionData)
        {
            LightData light = (LightData)0;
            light.direction = normalize(towardLight);
            light.intensity = colour;
            ComputeLight(light, surfaceData, emissionData);
        }

        // BeginLighting (internalonly.ffx) plus the preview's studio lights in place of the scene's lights and
        // environment cubes; the lights go through the game's ComputeLight for the material's lighting model.
        void PmcBeginLighting(inout SurfaceData surfaceData, inout EmissionData emissionData)
        {
            surfaceData.world_normal /= max(1e-7f, length(surfaceData.world_normal));
            surfaceData.view_dir = normalize(surfaceData.world_pos.xyz - pmc_eye.xyz);
            surfaceData.reflected_dir = reflect(surfaceData.view_dir, surfaceData.world_normal);
            emissionData.emission += surfaceData.material.emissive_color;
        #if defined(HAS_LIGHTING)
            // Lights authored in the scene objects' skeletons (fire, glows), with the game's point-light falloff.
            [loop] for (int pmc_l = 0; pmc_l < (int)pmc_effect_light_count.x; pmc_l++)
            {
                LightData pmc_point = (LightData)0;
                ComputePointLightParamsLegacy(float4(pmc_effect_light_position[pmc_l].xyz, 1.0f), pmc_effect_light_colour[pmc_l],
                    surfaceData.world_pos.xyz, pmc_point);
                ComputeLight(pmc_point, surfaceData, emissionData);
            }
            if (pmc_time.y == 2.0f)
            {
                // Dark wardrobe scene: the key is overhead, with weak blue skylight behind it.
                PmcAddLight(float3(-.30f, .91f, -.28f), float3(.74f, .54f, .31f), surfaceData, emissionData);
                PmcAddLight(float3(.64f, .58f, .51f), float3(.11f, .18f, .31f), surfaceData, emissionData);
                float sky = saturate(surfaceData.world_normal.y * .70f + .35f);
                float3 room = lerp(float3(.015f, .016f, .022f), float3(.083f, .096f, .14f), sky);
                emissionData.ambient += room * surfaceData.material.albedo_color.rgb;
            }
            else
            {
                PmcAddLight(pmc_light_dir.xyz, pmc_light_color.rgb, surfaceData, emissionData);
                // Neutral: white fill and rim, so metals keep their own tint (a strong blue fill turned brass silver,
                // e.g. the Hallowed Cryptkeeper mask). Warm keeps its blue counter-light.
                bool warmPreset = pmc_time.y == 1.0f;
                float3 fill = warmPreset ? float3(.83f, 1.31f, 2.6f) : float3(1.30f, 1.27f, 1.22f);
                PmcAddLight(float3(.77f, .45f, .58f), fill, surfaceData, emissionData);
                PmcAddLight(float3(.17f, .32f, -.94f), warmPreset ? float3(.65f, .68f, .78f) : float3(.62f, .61f, .60f), surfaceData, emissionData);
                float3 room = warmPreset
                    ? lerp(float3(.16f, .20f, .27f), float3(.32f, .34f, .40f), saturate(.5f + .5f * surfaceData.world_normal.y))
                    : lerp(float3(.17f, .17f, .18f), float3(.30f, .30f, .30f), saturate(.5f + .5f * surfaceData.world_normal.y));
                emissionData.ambient += room * surfaceData.material.albedo_color.rgb;
            }
        #endif
        }

        // EndLighting / FullBrightDirectLight (internalonly.ffx), without GI and ray tracing.
        void PmcEndLighting(SurfaceData surfaceData, inout EmissionData emissionData)
        {
        #if defined(HAS_LIGHTING)
            float3 specular = emissionData.specular;
            specular /= max(10.0f, max(specular.r, max(specular.g, specular.b))) / 10.0f; // ClampSpecularLight
            emissionData.specular = max(0.0f, specular);
            float3 direct_light = emissionData.diffuse + emissionData.emission;
            float3 indirect_light = surfaceData.material.indirect_color.rgb * (surfaceData.material.albedo_color.rgb + surfaceData.material.subsurface_color.rgb)
                + emissionData.ambient + emissionData.specular + emissionData.subsurface;
            emissionData.pixel_color.rgb = direct_light + indirect_light;
            emissionData.pixel_color.a = surfaceData.material.albedo_color.a;
        #else
            emissionData.pixel_color = surfaceData.material.albedo_color + float4(surfaceData.material.emissive_color, 0.0f);
        #endif
        }

        // Apply the material's threshold to cutout passes.
        void PmcAlphaTestClip(SurfaceData surfaceData)
        {
        #if defined(HAS_ALPHA_TEST_CLIPPING)
            clip(surfaceData.material.albedo_color.a - PMC_ALPHA_TEST_THRESHOLD + 0.001f);
        #endif
        }

        float4 PmcMaterialPixel(PmcPixelIn pin, bool pmc_front : SV_IsFrontFace
        #if defined(PMC_CUSTOM_DEPTH)
            , out float pmc_out_depth : SV_Depth
        #endif
        ) : SV_Target
        {
            float pmc_random_seed = 0.5f;
            float3 pmc_n = normalize(pin.normal);
            // Tangent frame from screen-space derivatives (the preview mesh carries no tangents).
            float3 dp1 = ddx(pin.world), dp2 = ddy(pin.world);
            float2 duv1 = ddx(pin.uv), duv2 = ddy(pin.uv);
            float3 dp2perp = cross(dp2, pmc_n), dp1perp = cross(pmc_n, dp1);
            float3 pmc_tangent = dp2perp * duv1.x + dp1perp * duv2.x;
            float3 pmc_binormal = dp2perp * duv1.y + dp1perp * duv2.y;
            float pmc_inv = rsqrt(max(1e-12f, max(dot(pmc_tangent, pmc_tangent), dot(pmc_binormal, pmc_binormal))));
            pmc_tangent *= pmc_inv; pmc_binormal *= pmc_inv;

            SurfaceData surfaceData = (SurfaceData)0;
            InitSurface(surfaceData);
            surfaceData.world_pos = pin.world;
            surfaceData.world_normal = pmc_n;
            surfaceData.uv = pin.uv;
            surfaceData.view_dir = normalize(pin.world - pmc_eye.xyz); // engine convention: camera towards surface
            surfaceData.tbn_basis.normal = pmc_n;
            surfaceData.tbn_basis.tangent = pmc_tangent;
            surfaceData.tbn_basis.binormal = pmc_binormal;
            // albedo_color keeps InitMaterial's value (0, 1, 0, 1): effect materials that only write alpha and
            // emission (e.g. Lightbringer caustics) rely on it, exactly as in the game.
            EmissionData emissionData = (EmissionData)0;
            InitEmission(emissionData);
            SemanticsData semanticsData = (SemanticsData)0;
            InitSemanticsData(semanticsData);
            // InitPixelData (internalonly.ffx): the interpolated vertex outputs. FromVertexUV and similar nodes read
            // these, e.g. the radial masks of particle materials.
            semanticsData.uv1 = pin.uv;
            semanticsData.uv_set_2 = pin.uv;
            semanticsData.world_pos = float4(pin.world, 1.0f);
            semanticsData.normal = pmc_n;
            semanticsData.tangent = pmc_tangent;
            semanticsData.binormal = pmc_binormal;
            semanticsData.color0 = pin.color;

        """;

    /// <summary>PixelOutputColor (internalonly.ffx): the colour the engine writes for the material's blend mode.</summary>
    private const string PixelEntryEnd = """

        #if defined(PMC_CUSTOM_DEPTH)
            pmc_out_depth = PmcMaterialDepth(surfaceData.world_pos.xyz, pin.position.z);
        #endif
        #if defined(PMC_SHIMMER)
            // The material separates positive and negative displacement into two channel pairs.
            float4 packedOffset = surfaceData.material.albedo_color;
            return float4(clamp(packedOffset.xy - packedOffset.zw, -1.0f, 1.0f), 0.0f, 0.0f);
        #else
            float4 pmc_out = emissionData.pixel_color;
        #if defined(MULTIPLICITIVE_BLEND)
            pmc_out = float4(1.0f - (1.0f - pmc_out.rgb) * pmc_out.a, 1.0f);
        #endif
        #if defined(PMC_DEBUG_ALPHA)
            pmc_out = float4(saturate(emissionData.pixel_color.aaa), 1.0f);
        #elif defined(PMC_DEBUG_COLOUR)
            pmc_out = float4(emissionData.pixel_color.rgb, 1.0f);
        #endif
            return pmc_out;
        #endif
        }
        """;
}
