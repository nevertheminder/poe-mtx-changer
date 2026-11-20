// Dev tool (read-only): renders real game materials with the shaders composed from the game's own fragments on a
// sphere through the preview's native renderer, and writes PNGs for visual checks.
// Usage: dotnet run --project tools/MaterialRender -c Release -- <oodle-dll> <game-root> <preview-dll-dir> <out-dir> <material.mat>...
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Formats.Dds;
using PoeMtxChanger.Core.Formats.Effects;
using PoeMtxChanger.Core.Formats.Shaders;
using PoeMtxChanger.Core.Installation;
using PoeMtxChanger.Core.Rendering;
using PoeMtxChanger.Core.Resources;
using PoeMtxChanger.Core.Storage;

if (args is ["--bloom-check" or "--refraction-check" or "--reference-post-check", var nativeDirectory, var outputDirectory])
{
    ResolveNativeLibrary(nativeDirectory);
    var output = Directory.CreateDirectory(outputDirectory).FullName;
    foreach (bool warp in new[] { false, true })
    {
        var preview = CreateRenderer(warp);
        try
        {
            string device = warp ? "warp" : "default";
            if (args[0] == "--bloom-check") CheckBloom(preview, output, device);
            else if (args[0] == "--reference-post-check") CheckReferencePost(preview, output, device);
            else CheckRefraction(preview, output, device);
        }
        finally { Native.preview_destroy(preview); }
    }
    return;
}

var codec = NativeOodleCodec.TryLoad(args[0]);
var inst = GameInstallationDetector.Probe(args[1], "arg") ?? throw new InvalidOperationException("no install");
ResolveNativeLibrary(args[2]);
var outDir = Directory.CreateDirectory(args[3]).FullName;
using var archive = GameArchive.Open(inst.OpenStorage(), codec);
string Text(string p) => ReferenceExtractor.DecodeText(archive.Read(p)) ?? "";
var lib = FfxLibrary.Parse(archive.EnumeratePaths().Where(p => p.EndsWith(".ffx", StringComparison.OrdinalIgnoreCase)).Select(p => (p, Text(p))));
var graphs = new Dictionary<string, FxGraph?>(StringComparer.OrdinalIgnoreCase);
var builder = new MaterialShaderBuilder(lib,
    p => graphs.TryGetValue(p, out var g) ? g : graphs[p] = archive.Exists(p) ? FxGraph.Parse(p, Text(p)) : null,
    p => archive.Exists(p) ? Text(p) : null);
var compiler = new HlslCompiler(null);

var renderer = CreateRenderer();

// UV sphere, radius 50, in the preview's world space (Y up); UVs tile twice around.
var verts = new List<Vertex>();
var idx = new List<int>();
const int Rings = 48, Segs = 96;
for (int r = 0; r <= Rings; r++)
    for (int s = 0; s <= Segs; s++)
    {
        float v = (float)r / Rings, u = (float)s / Segs;
        float th = v * MathF.PI, ph = u * MathF.Tau;
        var n = new Vector3(MathF.Sin(th) * MathF.Cos(ph), MathF.Cos(th), MathF.Sin(th) * MathF.Sin(ph));
        verts.Add(new(n * 50, n, new(u * 2, v), Vector4.One));
    }
for (int r = 0; r < Rings; r++)
    for (int s = 0; s < Segs; s++)
    {
        int a = r * (Segs + 1) + s, b = a + Segs + 1;
        idx.AddRange([a, b, a + 1, a + 1, b, b + 1]);
    }

const int W = 512, H = 512;
var eye = new Vector3(0, 30, 150);
var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.75f, 1, 1, 1000);
var pixels = new byte[W * H * 4];
// "find:<substring>:<count>" picks matching .mat paths from the index.
var allPaths = archive.EnumeratePaths().Where(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
var materials = args.Skip(4).SelectMany(a => a.StartsWith("find:") && a.Split(':') is [_, var pattern, var n]
    ? allPaths.Where(p => p.Contains(pattern, StringComparison.OrdinalIgnoreCase)).Take(int.Parse(n))
    : [a]).ToList();
for (int materialIndex = 0; materialIndex < materials.Count; materialIndex++)
{
    var mat = materials[materialIndex];
    if (mat.StartsWith("hlsl:"))
    {
        // "hlsl:<material.mat>[:<draw blend>]" writes the composed particle-variant shader next to the renders.
        var parts = mat.Split(':');
        var particleBuilder = new MaterialShaderBuilder(lib,
            p => graphs.TryGetValue(p, out var g) ? g : graphs[p] = archive.Exists(p) ? FxGraph.Parse(p, Text(p)) : null,
            p => archive.Exists(p) ? Text(p) : null) { Particle = true };
        var shader = particleBuilder.Build(FxMaterial.Parse(parts[1], Text(parts[1])), parts.Length > 2 ? parts[2] : null);
        var target = Path.Combine(outDir, Path.GetFileNameWithoutExtension(parts[1]) + ".hlsl");
        File.WriteAllText(target, shader.Hlsl);
        Console.WriteLine($"{target}: blend {shader.BlendMode} -> {shader.PreviewBlendState}, {shader.EmittedNodes} nodes, textures {string.Join(", ", shader.Textures.Select(t => $"t{t.Slot}={Path.GetFileName(t.Path)}"))}");
        foreach (var n in shader.Notes.Where(n => !n.StartsWith("engine uniform defaulted"))) Console.WriteLine("  note: " + n);
        continue;
    }
    if (mat.StartsWith("param:") && mat.Split(':') is [_, var graphName, var paramName, var maxCount])
    {
        // "param:<graph file name>:<custom parameter>:<n>" prints how materials set one custom parameter.
        var seen = new Dictionary<string, int>();
        foreach (var m in archive.EnumeratePaths().Where(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)))
        {
            string t; try { t = Text(m); } catch { continue; }
            if (!t.Contains(graphName, StringComparison.OrdinalIgnoreCase)) continue;
            using var json = System.Text.Json.JsonDocument.Parse(t);
            if (!json.RootElement.TryGetProperty("graphinstances", out var gis)) continue;
            foreach (var gi in gis.EnumerateArray())
            {
                if (gi.TryGetProperty("parent", out var parent) && parent.GetString()?.Contains(graphName, StringComparison.OrdinalIgnoreCase) != true) continue;
                string value = "(not set)";
                if (gi.TryGetProperty("custom_parameters", out var cps))
                    foreach (var cp in cps.EnumerateArray())
                        if (cp.GetProperty("name").GetString() == paramName) value = cp.GetProperty("parameters").GetRawText();
                seen[value] = seen.GetValueOrDefault(value) + 1;
            }
            if (seen.Values.Sum() >= int.Parse(maxCount)) break;
        }
        foreach (var (k, v) in seen.OrderByDescending(x => x.Value)) Console.WriteLine($"{v,6}  {k}");
        continue;
    }
    if (mat.StartsWith("astscan:") && mat.Split(':') is [_, var astScope, var astMax])
    {
        // Lists .ast files under a path fragment whose header declares lights, then dumps their records.
        int shown = 0;
        foreach (var ast in archive.EnumeratePaths().Where(p => p.EndsWith(".ast", StringComparison.OrdinalIgnoreCase) && p.Contains(astScope, StringComparison.OrdinalIgnoreCase)))
        {
            byte[] head; try { head = archive.Read(ast); } catch { continue; }
            if (head.Length < 8 || head[7] == 0) continue;
            materials.Add("astlights:" + ast);
            if (++shown >= int.Parse(astMax)) break;
        }
        Console.WriteLine($"astscan: {shown} skeletons with lights queued");
        continue;
    }
    if (mat.StartsWith("astlights:"))
    {
        // Prints the raw light records of an .ast (name length, 55(+4) bytes, name) as bytes and floats.
        var b = archive.Read(mat[10..]);
        using var r = new BinaryReader(new MemoryStream(b));
        int version = r.ReadByte(); int bones = r.ReadByte(); r.ReadByte(); int anims = r.ReadUInt16(); r.ReadByte(); r.ReadByte(); int lights = r.ReadByte();
        Console.WriteLine($"{mat[10..]}: v{version}, {bones} bones, {anims} clips, {lights} lights");
        var boneNames = new List<string>();
        for (int i = 0; i < bones; i++) { r.ReadBytes(2 + 64); int len = r.ReadByte(); r.ReadByte(); boneNames.Add(System.Text.Encoding.ASCII.GetString(r.ReadBytes(len))); }
        Console.WriteLine("  bones: " + string.Join(", ", boneNames.Take(12)));
        for (int i = 0; i < lights; i++)
        {
            int len = r.ReadByte();
            var body = r.ReadBytes(55 + (version >= 9 ? 4 : 0));
            var lightName = System.Text.Encoding.ASCII.GetString(r.ReadBytes(len));
            Console.WriteLine($"  light '{lightName}': {Convert.ToHexString(body)}");
            for (int o = 0; o + 4 <= body.Length; o++)
            {
                float f = BitConverter.ToSingle(body, o);
                if (float.IsFinite(f) && Math.Abs(f) > 1e-4 && Math.Abs(f) < 1e5) Console.Write($" @{o}={f:G4}");
            }
            Console.WriteLine();
        }
        continue;
    }
    if (mat.StartsWith("texinfo:"))
    {
        var texPath = mat[8..];
        var raw = archive.Read(texPath);
        var image = DdsDecoder.Decode(raw, p => archive.Exists(p) ? archive.Read(p) : null);
        int opaque = 0, clear = 0;
        double sr = 0, sg = 0, sb = 0; int counted = 0;
        for (int i = 3; i < image.Bgra.Length; i += 4)
        {
            if (image.Bgra[i] > 250) opaque++; else if (image.Bgra[i] < 5) clear++;
            if (image.Bgra[i] > 128) { sb += image.Bgra[i - 3]; sg += image.Bgra[i - 2]; sr += image.Bgra[i - 1]; counted++; }
        }
        if (counted > 0) Console.WriteLine($"  mean rgb of opaque texels: {sr / counted:F0} {sg / counted:F0} {sb / counted:F0}");
        Console.WriteLine($"{texPath}: {raw.Length} bytes, decoded {image.Width}x{image.Height} {image.Format}, alpha opaque {opaque}, clear {clear}, header {Convert.ToHexString(raw.AsSpan(0, Math.Min(32, raw.Length)))}");
        var related = archive.EnumeratePaths().Where(p => p.StartsWith(Path.ChangeExtension(texPath, null), StringComparison.OrdinalIgnoreCase)).Take(8);
        foreach (var r in related) Console.WriteLine("  sibling: " + r);
        continue;
    }
    if (mat.StartsWith("dump:")) { Console.WriteLine("==== " + mat[5..] + Environment.NewLine + (archive.Exists(mat[5..]) ? Text(mat[5..]) : "(missing)")); continue; }
    if (mat.StartsWith("blocks:") && mat.Split(':') is [_, var bext, var scope, var bmax])
    {
        var counts = new Dictionary<string, int>();
        foreach (var f in archive.EnumeratePaths().Where(p => p.EndsWith(bext, StringComparison.OrdinalIgnoreCase) && p.Contains(scope, StringComparison.OrdinalIgnoreCase)).Take(int.Parse(bmax)))
        {
            string t; try { t = Text(f); } catch { continue; }
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(t, @"\{\s*([A-Za-z_]+)"))
                counts[m.Groups[1].Value] = counts.GetValueOrDefault(m.Groups[1].Value) + 1;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(t, "\"([^\"]+\\.(\\w+))\""))
                counts["ref ." + m.Groups[2].Value.ToLowerInvariant()] = counts.GetValueOrDefault("ref ." + m.Groups[2].Value.ToLowerInvariant()) + 1;
        }
        foreach (var (k, v) in counts.OrderByDescending(x => x.Value).Take(30)) Console.WriteLine($"{k,-28} {v}");
        continue;
    }
    if (mat.StartsWith("exts:"))
    {
        foreach (var g in archive.EnumeratePaths().Where(p => p.Contains(mat[5..], StringComparison.OrdinalIgnoreCase))
                     .GroupBy(p => Path.GetExtension(p).ToLowerInvariant()).OrderByDescending(g => g.Count()).Take(25))
            Console.WriteLine($"{g.Key,-16} {g.Count()}");
        continue;
    }
    if (mat.StartsWith("grep:") && mat.Split(':') is [_, var ext, var needle, var max])
    {
        foreach (var hit in archive.EnumeratePaths().Where(p => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)).Take(200000)
                     .Where(p => { try { return Text(p).Contains(needle, StringComparison.OrdinalIgnoreCase); } catch { return false; } }).Take(int.Parse(max)))
            Console.WriteLine("hit: " + hit);
        continue;
    }
    if (mat.StartsWith("effect:") || mat.StartsWith("trail:"))
    {
        RenderEffect(mat);
        continue;
    }
    Native.preview_clear_textures(renderer);
    // "base.mat+pass.mat" appends the pass's graph instances, like an EPK render pass of type Append.
    var layers = mat.Split('+');
    var combined = FxMaterial.Parse(layers[0], Text(layers[0]));
    foreach (var extra in layers.Skip(1)) combined.Instances.AddRange(FxMaterial.Parse(extra, Text(extra)).Instances);
    var composed = builder.Build(combined);
    var compiled = compiler.Compile(composed.Hlsl, ComposedMaterialShader.EntryPoint, ComposedMaterialShader.Profile);
    if (compiled.Bytecode is null)
    {
        Console.WriteLine($"{mat}: compile failed: {compiled.Error?.Split('\n').FirstOrDefault(l => l.Contains("error"))}");
        continue;
    }
    uint material = Native.preview_material(renderer, compiled.Bytecode, (uint)compiled.Bytecode.Length);
    var draw = new Draw
    {
        Count = (uint)idx.Count, Tint = Vector4.One, Material = material,
        Blend = composed.PreviewBlendState,
    };
    foreach (var t in composed.Textures)
    {
        if (t.Path is null || !archive.Exists(t.Path) || t.Slot >= 16) continue;
        try
        {
            var img = DdsDecoder.Decode(archive.Read(t.Path), p => archive.Exists(p) ? archive.Read(p) : null);
            draw.Slots[t.Slot] = Native.preview_texture(renderer, img.Bgra, (uint)img.Width, (uint)img.Height, t.Srgb ? 1 : 0);
        }
        catch (Exception ex) { Console.WriteLine($"  texture {t.Path}: {ex.Message}"); }
    }
    var frame = new Frame { ViewProjection = view * proj, Eye = new(eye, 1), Settings = new(1.5f, 1f / W, 1f / H, 0), Depth = new(1, 1000, 0, 0) };
    int hr = Native.preview_draw(renderer, frame, verts.ToArray(), (uint)verts.Count, idx.ToArray(), (uint)idx.Count, [draw], 1, W, H, pixels, W * 4);
    if (hr < 0) { Console.WriteLine($"{mat}: draw failed 0x{hr:X8}"); continue; }
    var name = string.Join("+", layers.Select(Path.GetFileNameWithoutExtension));
    Png.Write(Path.Combine(outDir, name + ".png"), W, H, pixels);
    Console.WriteLine($"{mat}: ok, {composed.EmittedNodes} nodes, textures {composed.Textures.Count(t => t.Path != null)}/{composed.Textures.Count}, " +
        $"blend {composed.BlendMode ?? "-"}, lighting {composed.LightingModel}");
    foreach (var n in composed.Notes.Take(8)) Console.WriteLine("    note: " + n);
}
Native.preview_destroy(renderer);

static void ResolveNativeLibrary(string directory) => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly,
    (name, _, _) => name == "PoeMtxPreview.dll" ? NativeLibrary.Load(Path.Combine(directory, name)) : 0);

static nint CreateRenderer(bool warp = false)
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
    {
        string shader = Path.Combine(directory.FullName, "native", "preview", "Preview.hlsl");
        if (!File.Exists(shader)) continue;
        byte[] source = File.ReadAllBytes(shader);
        var error = new byte[4096];
        nint preview = Native.preview_create(source, (uint)source.Length, error, (uint)error.Length, warp ? 1 : 0);
        return preview != 0 ? preview
            : throw new InvalidOperationException(System.Text.Encoding.UTF8.GetString(error).TrimEnd('\0'));
    }
    throw new FileNotFoundException("Cannot locate native/preview/Preview.hlsl.");
}

static void CheckRefraction(nint renderer, string outDir, string device)
{
    const int size = 256;
    var compiler = new HlslCompiler(null);
    uint Material(string shader)
    {
        var result = compiler.Compile(shader, "main", "ps_5_0");
        if (result.Bytecode == null) throw new InvalidOperationException(result.Error);
        uint id = Native.preview_material(renderer, result.Bytecode, (uint)result.Bytecode.Length);
        if (id == 0) throw new InvalidOperationException("Test shader did not register.");
        return id;
    }
    uint stripes = Material("""
        float4 main(float4 p : SV_Position) : SV_Target {
            float v = .10 + .35 * (.5 + .5 * sin(p.x * .29));
            return float4(v, v, v, 1);
        }
        """);
    uint displacement = Material("float4 main() : SV_Target { return float4(.5, 0, 0, 0); }");
    uint biased = Material("float4 main(out float depth : SV_Depth) : SV_Target { depth = .35; return float4(.5, 0, 0, 0); }");
    Vertex[] vertices = [.. Quad(1, .65f), .. Quad(.5f, .35f), .. Quad(.5f, .85f)];
    int[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 8, 9, 10, 8, 10, 11];
    var frame = new Frame
    {
        ViewProjection = Matrix4x4.Identity, Eye = new(0, 0, 1, 1),
        Settings = new(0, 1f / size, 1f / size, 0), Depth = new(1, 1000, 0, 0)
    };
    var backdrop = new Draw { Material = stripes, Count = 6 };
    byte[] plain = Render([backdrop]);
    byte[] refracted = Render([backdrop, new Draw { Material = displacement, Blend = 7, First = 6, Count = 6 }]);
    byte[] occluded = Render([backdrop, new Draw { Material = displacement, Blend = 7, First = 12, Count = 6 }]);
    byte[] movedInFront = Render([backdrop, new Draw { Material = biased, Blend = 7, First = 12, Count = 6 }]);
    Png.Write(Path.Combine(outDir, $"refraction-{device}.png"), size, size, refracted);
    if (!plain.SequenceEqual(occluded)) throw new InvalidOperationException("Objects behind opaque surfaces must not distort them.");
    if (!refracted.SequenceEqual(movedInFront)) throw new InvalidOperationException("Authored depth must control refraction visibility.");
    if (!plain.SequenceEqual(Render([backdrop]))) throw new InvalidOperationException("Refraction must clear when the effect disappears.");
    int inside = 0;
    for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int offset = (y * size + x) * 4;
            if (x is > 80 and < 174 && y is > 80 and < 174)
            {
                int expected = offset + 16; // .5 × 8 px of horizontal displacement
                if (Math.Abs(refracted[offset] - plain[expected]) > 2)
                    throw new InvalidOperationException("The distortion must move existing scene colours by the authored offset.");
                if (Math.Abs(plain[offset] - refracted[offset]) > 8) inside++;
            }
            else if ((x is < 55 or > 200 || y is < 55 or > 200) && refracted[offset] != plain[offset])
                throw new InvalidOperationException("The distortion escaped the emitter.");
        }
    if (inside < 4000) throw new InvalidOperationException("No visible distortion in the centre of the emitter.");
    Console.WriteLine($"Refraction {device}: shifted {inside} pixels, with unchanged background, depth occlusion and authored depth override.");

    byte[] Render(Draw[] draws)
    {
        var pixels = new byte[size * size * 4];
        int result = Native.preview_draw(renderer, frame, vertices, (uint)vertices.Length, indices, (uint)indices.Length,
            draws, (uint)draws.Length, size, size, pixels, size * 4);
        Marshal.ThrowExceptionForHR(result);
        return pixels;
    }
    static Vertex[] Quad(float extent, float depth) =>
    [
        new(new(-extent,-extent,depth), Vector3.UnitZ, Vector2.Zero, Vector4.One),
        new(new(extent,-extent,depth), Vector3.UnitZ, Vector2.UnitX, Vector4.One),
        new(new(extent,extent,depth), Vector3.UnitZ, Vector2.One, Vector4.One),
        new(new(-extent,extent,depth), Vector3.UnitZ, Vector2.UnitY, Vector4.One),
    ];
}

static void CheckBloom(nint renderer, string outDir, string device)
{
    // A tiny HDR emitter over the normal studio backdrop exposes the grid of the bloom mip chain.
    const int radius = 32;
    uint white = Native.preview_texture(renderer, [255, 255, 255, 255], 1, 1, 0);
    int[] triangles = [0, 1, 2, 0, 2, 3];

    void Render(int width, int height, int x, int y, Vector3 tint, float intensity, byte[] destination)
    {
        var frame = new Frame { ViewProjection = Matrix4x4.Identity, Eye = new(0, 0, 1, 1),
            Settings = new(0, 1f / width, 1f / height, 0) };
        Vertex Corner(float px, float py) => new(new(2 * px / width - 1, 1 - 2 * py / height, .5f),
            Vector3.UnitZ, Vector2.Zero, Vector4.One);
        Vertex[] quad = [Corner(x - 3, y - 3), Corner(x + 3, y - 3),
            Corner(x + 3, y + 3), Corner(x - 3, y + 3)];
        var draw = new Draw { Count = 6, Glow = white, Blend = 2, Maps = new(0, 0, 0, 3),
            Tint = new(tint, 1), Motion = new(0, 0, intensity, 0) };
        int hr = Native.preview_draw(renderer, frame, quad, 4, triangles, 6, [draw], 1, (uint)width, (uint)height,
            destination, (uint)width * 4);
        Marshal.ThrowExceptionForHR(hr);
    }

    foreach (var (width, height) in new[] { (512, 512), (511, 383) })
    {
        int centreY = height / 2 - 5;
        var reference = new byte[width * height * 4];
        var image = new byte[reference.Length];
        Render(width, height, width / 2, centreY, Vector3.One, 0, reference);
        int min = int.MaxValue, max = 0;
        foreach (int x in new[] { width / 2 - 7, width / 2 + 1, width / 2 + 9 })
        {
            Render(width, height, x, centreY, Vector3.One, 12, image);
            for (int ray = 0; ray < 8; ray++)
            {
                double angle = ray * Math.PI / 4;
                int px = x + (int)Math.Round(radius * Math.Cos(angle));
                int py = centreY + (int)Math.Round(radius * Math.Sin(angle));
                int index = (py * width + px) * 4 + 1;
                int contribution = image[index] - reference[index];
                min = Math.Min(min, contribution);
                max = Math.Max(max, contribution);
            }
        }
        Console.WriteLine($"Bloom {device}, {width}×{height} at {radius}px: +{min}…{max} display levels across angles and emitter positions.");
        if (min < 2 || max - min > 9)
            throw new InvalidOperationException("The bloom halo disappeared or varies with emitter position.");

        Render(width, height, width / 2, centreY, new(1f, .25f, .03f), 12, image);
        int centre = (centreY * width + width / 2) * 4;
        if (image[centre + 2] - image[centre + 1] < 35 || image[centre + 1] - image[centre] < 35)
            throw new InvalidOperationException("The HDR roll-off washed out the emitter colour.");
        Png.Write(Path.Combine(outDir, $"bloom-impulse-{device}-{width}.png"), width, height, image);
    }
    Render(15, 13, 7, 6, Vector3.One, 12, new byte[15 * 13 * 4]);
}

static void CheckReferencePost(nint renderer, string outDir, string device)
{
    uint white = Native.preview_texture(renderer, [255, 255, 255, 255], 1, 1, 0);
    int[] triangles = [0, 1, 2, 0, 2, 3];
    foreach (var (width, height) in new[] { (512, 512), (511, 383) })
    {
        int x = width / 2, y = height / 2;
        int centre = (y * width + x) * 4, halo = (y * width + x + 42) * 4;
        Vertex Corner(int px, int py) => new(new(2f * px / width - 1, 1 - 2f * py / height, .5f),
            Vector3.UnitZ, Vector2.Zero, Vector4.One);
        Vertex[] quad = [Corner(x - 8, y - 8), Corner(x + 8, y - 8), Corner(x + 8, y + 8), Corner(x - 8, y + 8)];

        byte[] Render(float exposure, float intensity, Vector3 tint, bool additive = false)
        {
            var frame = new Frame
            {
                ViewProjection = Matrix4x4.Identity, Eye = new(0, 0, 1, 1),
                Settings = new(0, 1f / width, 1f / height, 0), PostProcess = new(exposure, 0, 0, 0)
            };
            var draw = new Draw
            {
                Count = 6, Glow = white, Blend = additive ? 2u : 0u,
                Maps = new(0, 0, 0, 3), Tint = new(tint, 1), Motion = new(0, 0, intensity, 0)
            };
            var pixels = new byte[width * height * 4];
            Marshal.ThrowExceptionForHR(Native.preview_draw(renderer, frame, quad, 4, triangles, 6,
                [draw], 1, (uint)width, (uint)height, pixels, (uint)width * 4));
            return pixels;
        }

        static int Srgb(float linear) => (int)MathF.Round(255 * (linear < .0031308f
            ? 12.92f * linear : 1.055f * MathF.Pow(linear, 1f / 2.4f) - .055f));

        var low = Render(.5f, .25f, Vector3.One);
        var high = Render(2, .25f, Vector3.One);
        if (Math.Abs(low[centre] - Srgb(.125f)) > 2 || Math.Abs(high[centre] - Srgb(1 - MathF.Exp(-.5f))) > 2)
            throw new InvalidOperationException("Reference exposure did not produce the expected linear and exponential responses.");

        var studioBefore = Render(0, 6, Vector3.One, additive: true);
        var baseline = Render(2, 0, Vector3.One, additive: true);
        var belowThreshold = Render(2, 1.2f, Vector3.UnitX, additive: true);
        var lit = Render(2, 6, Vector3.One, additive: true);
        if (!belowThreshold.AsSpan(halo, 4).SequenceEqual(baseline.AsSpan(halo, 4)) || lit[halo] < baseline[halo] + 3)
            throw new InvalidOperationException("Reference bloom failed the mean-RGB cutoff or did not reach the coarse mip levels.");
        if (!Render(2, 0, Vector3.One, additive: true).SequenceEqual(baseline) ||
            !Render(0, 6, Vector3.One, additive: true).SequenceEqual(studioBefore))
            throw new InvalidOperationException("Reference bloom leaked into a subsequent frame or studio mode.");
        Console.WriteLine($"Reference post {device}, {width}×{height}: exposure verified, bloom +{lit[halo] - baseline[halo]} levels at 42px.");
        Png.Write(Path.Combine(outDir, $"reference-post-{device}-{width}.png"), width, height, lit);
    }
}

// "effect:<file.pet>[:seconds]" renders the particles of a .pet along a vertical 120-unit line at 0.4 s steps;
// "trail:<file.trl>[:material-name]" swings an anchor in an arc and renders all or one of the ribbon layers.
void RenderEffect(string spec)
{
    var parts = spec.Split(':');
    bool trailMode = parts[0] == "trail";
    string path = parts[1];
    Native.preview_clear_textures(renderer);
    var particleBuilder = new MaterialShaderBuilder(lib,
        p => graphs.TryGetValue(p, out var g) ? g : graphs[p] = archive.Exists(p) ? FxGraph.Parse(p, Text(p)) : null,
        p => archive.Exists(p) ? Text(p) : null) { Particle = true };
    (uint Material, uint Blend, Slots Slots)? Prepare(string material, string? blend)
    {
        if (!archive.Exists(material)) { Console.WriteLine($"  missing {material}"); return null; }
        var composed = particleBuilder.Build(FxMaterial.Parse(material, Text(material)), blend);
        var compiled = compiler.Compile(composed.Hlsl, ComposedMaterialShader.EntryPoint, ComposedMaterialShader.Profile);
        if (compiled.Bytecode is null) { Console.WriteLine($"  {material}: compile failed"); return null; }
        var slots = new Slots();
        foreach (var t in composed.Textures)
            if (t.Path != null && archive.Exists(t.Path) && t.Slot < 16)
                try
                {
                    var img = DdsDecoder.Decode(archive.Read(t.Path), p => archive.Exists(p) ? archive.Read(p) : null);
                    slots[t.Slot] = Native.preview_texture(renderer, img.Bgra, (uint)img.Width, (uint)img.Height, t.Srgb ? 1 : 0);
                }
                catch (Exception ex) { Console.WriteLine($"  texture {t.Path}: {ex.Message}"); }
        Console.WriteLine($"  {Path.GetFileName(material)}: blend {composed.BlendMode ?? "-"} -> state {composed.PreviewBlendState}, {composed.EmittedNodes} nodes");
        return (Native.preview_material(renderer, compiled.Bytecode, (uint)compiled.Bytecode.Length), composed.PreviewBlendState, slots);
    }
    static Vector3 ToWorld(Vector3 v) => new(v.X, -v.Z, v.Y);
    float centreX = trailMode ? 65 : 0;
    var eye2 = new Vector3(centreX, 70, 260);
    var target = new Vector3(centreX, 60, 0);
    var view2 = Matrix4x4.CreateLookAt(eye2, target, Vector3.UnitY);
    var forward = Vector3.Normalize(target - eye2);
    var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
    var up = Vector3.Cross(right, forward);
    var name = Path.GetFileNameWithoutExtension(path);

    if (!trailMode)
    {
        var emitters = PetFile.Parse(Text(path));
        Console.WriteLine($"{path}: {emitters.Count} emitters");
        var prepared = new List<(ParticleSimulator Sim, uint Material, uint Blend, Slots Slots)>();
        foreach (var (e, i) in emitters.Select((e, i) => (e, i)))
        {
            if (e.Mesh != null) { Console.WriteLine($"  emitter {i}: mesh particle {e.Mesh} skipped"); continue; }
            if (Prepare(e.Material, e.Blend) is { } m) prepared.Add((new ParticleSimulator(e, 17 + i), m.Material, m.Blend, m.Slots));
        }
        foreach (var t in new[] { 0.4f, 0.8f, 1.6f })
        {
            var vertices = new List<Vertex>(); var indices = new List<int>(); var draws = new List<Draw>();
            var sprites = new List<ParticleSprite>();
            foreach (var p in prepared)
            {
                sprites.Clear();
                p.Sim.Evaluate(t, u => new Vector3(0, 0, -120 * u), sprites);
                int first = indices.Count;
                foreach (var s in sprites.OrderByDescending(s => Vector3.DistanceSquared(ToWorld(s.Position), eye2)))
                {
                    var c = ToWorld(s.Position);
                    float cs = MathF.Cos(s.Rotation), sn = MathF.Sin(s.Rotation);
                    var x = (right * cs + up * sn) * s.Width * .5f; var y = (up * cs - right * sn) * s.Height * .5f;
                    var vel = ToWorld(s.Velocity); var planar = vel - forward * Vector3.Dot(vel, forward);
                    if (p.Sim.Emitter.RotationLock == "Velocity" && planar.LengthSquared() > 1e-4f)
                    {
                        var yy = Vector3.Normalize(planar);
                        x = Vector3.Normalize(Vector3.Cross(yy, forward)) * s.Width * .5f; y = yy * s.Height * .5f;
                    }
                    var a = s.Atlas;
                    int v0 = vertices.Count;
                    vertices.Add(new(c - x - y, -forward, new(a.X, a.Y + a.W), s.Colour));
                    vertices.Add(new(c + x - y, -forward, new(a.X + a.Z, a.Y + a.W), s.Colour));
                    vertices.Add(new(c + x + y, -forward, new(a.X + a.Z, a.Y), s.Colour));
                    vertices.Add(new(c - x + y, -forward, new(a.X, a.Y), s.Colour));
                    indices.AddRange([v0, v0 + 1, v0 + 2, v0, v0 + 2, v0 + 3]);
                }
                draws.Add(new Draw { First = (uint)first, Count = (uint)(indices.Count - first), Tint = Vector4.One, Material = p.Material, Blend = p.Blend, Slots = p.Slots });
                Console.WriteLine($"  t={t}: {sprites.Count} particles of {Path.GetFileName(p.Sim.Emitter.Material)}");
            }
            if (vertices.Count == 0) { vertices.Add(default); indices.AddRange([0, 0, 0]); }
            Draw(vertices, indices, draws, view2, eye2, $"{name}_{t:0.0}");
        }
        return;
    }

    var trails = TrlTrail.Parse(Text(path));
    Console.WriteLine($"{path}: {trails.Count} trails");
    var sims = new List<(TrailSimulator Sim, uint Material, uint Blend, Slots Slots)>();
    if (parts.Length > 2)
    {
        trails = trails.Where(t => Path.GetFileNameWithoutExtension(t.Material).Equals(parts[2], StringComparison.OrdinalIgnoreCase)).ToArray();
        name += "-" + parts[2];
    }
    foreach (var tr in trails)
        if (Prepare(tr.Material, "Additive") is { } m) sims.Add((new TrailSimulator(tr, 20), m.Material, m.Blend, m.Slots));
    var ribbon = new List<TrailPoint>();
    var lastDraws = new List<Draw>(); var lastVertices = new List<Vertex>(); var lastIndices = new List<int>();
    for (int frameIndex = 0; frameIndex <= 60; frameIndex++)
    {
        float t = frameIndex / 60f;
        float angle = -1.2f + 2.4f * t;
        var a = new Vector3(MathF.Sin(angle) * 60, MathF.Cos(angle) * 20, -60 - MathF.Cos(angle) * 60);
        var b = a + Vector3.Normalize(a - new Vector3(0, 0, -60)) * 50;
        lastDraws.Clear(); lastVertices.Clear(); lastIndices.Clear();
        foreach (var s in sims)
        {
            s.Sim.Update(t, a, b, Vector3.UnitX, ribbon);
            int first = lastIndices.Count, v0 = lastVertices.Count;
            float length = ribbon.Count > 0 ? Math.Max(1e-3f, ribbon[^1].Distance) : 1;
            foreach (var p in ribbon)
            {
                lastVertices.Add(new(ToWorld(p.Left), Vector3.UnitY, new(0, p.Distance / length), p.Colour));
                lastVertices.Add(new(ToWorld(p.Right), Vector3.UnitY, new(1, p.Distance / length), p.Colour));
            }
            for (int j = 0; j + 1 < ribbon.Count; j++)
            {
                int k = v0 + j * 2;
                lastIndices.AddRange([k, k + 1, k + 2, k + 1, k + 3, k + 2]);
            }
            lastDraws.Add(new Draw { First = (uint)first, Count = (uint)(lastIndices.Count - first), Tint = Vector4.One, Material = s.Material, Blend = s.Blend, Slots = s.Slots });
        }
    }
    Console.WriteLine($"  ribbon points: {ribbon.Count}");
    if (lastVertices.Count == 0) { lastVertices.Add(default); lastIndices.AddRange([0, 0, 0]); }
    Draw(lastVertices, lastIndices, lastDraws, view2, eye2, name);
}

void Draw(List<Vertex> vertices, List<int> indices, List<Draw> draws, Matrix4x4 view2, Vector3 eye2, string file)
{
    var proj2 = Matrix4x4.CreatePerspectiveFieldOfView(0.75f, 1, 1, 2000);
    var frame = new Frame { ViewProjection = view2 * proj2, Eye = new(eye2, 1), Settings = new(1.0f, 1f / W, 1f / H, 0), Depth = new(1, 2000, 0, 0) };
    int hr = Native.preview_draw(renderer, frame, vertices.ToArray(), (uint)vertices.Count, indices.ToArray(), (uint)indices.Count, draws.ToArray(), (uint)draws.Count, W, H, pixels, W * 4);
    if (hr < 0) { Console.WriteLine($"  draw failed 0x{hr:X8}"); return; }
    Png.Write(Path.Combine(outDir, file + ".png"), W, H, pixels);
}

[StructLayout(LayoutKind.Sequential)] record struct Vertex(Vector3 Position, Vector3 Normal, Vector2 UV, Vector4 Colour);
[StructLayout(LayoutKind.Sequential)] struct Frame { public Matrix4x4 ViewProjection; public Vector4 Eye, Settings, P0, P1, P2, C0, C1, C2, Depth, PostProcess; }
[StructLayout(LayoutKind.Sequential)]
struct Draw
{
    public uint First, Count, Base, Glow, Normal, Reflectance, Blend;
    public Vector4 Maps, Tint, Motion, Edge, Modulation, UvTransform;
    public uint Mask, Material;
    public Slots Slots;
}
[System.Runtime.CompilerServices.InlineArray(19)] struct Slots { private uint _e; }

static class Native
{
    [DllImport("PoeMtxPreview.dll")] public static extern nint preview_create(byte[] shader, uint length, [Out] byte[] error, uint errorLength, int forceWarp);
    [DllImport("PoeMtxPreview.dll")] public static extern void preview_destroy(nint r);
    [DllImport("PoeMtxPreview.dll")] public static extern void preview_clear_textures(nint r);
    [DllImport("PoeMtxPreview.dll")] public static extern uint preview_material(nint r, byte[] code, uint length);
    [DllImport("PoeMtxPreview.dll")] public static extern uint preview_texture(nint r, byte[] pixels, uint w, uint h, int srgb);
    [DllImport("PoeMtxPreview.dll")]
    public static extern int preview_draw(nint r, in Frame frame, [In] Vertex[] points, uint pointCount, [In] int[] indices, uint indexCount,
        [In] Draw[] draws, uint drawCount, uint w, uint h, [Out] byte[] pixels, uint stride);
}

static class Png
{
    public static void Write(string path, int w, int h, byte[] bgra)
    {
        var raw = new byte[h * (w * 4 + 1)];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 4, d = y * (w * 4 + 1) + 1 + x * 4;
                raw[d] = bgra[s + 2]; raw[d + 1] = bgra[s + 1]; raw[d + 2] = bgra[s]; raw[d + 3] = 255;
            }
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(raw);
        using var f = File.Create(path);
        f.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Chunk(f, "IHDR", [.. BE(w), .. BE(h), 8, 6, 0, 0, 0]);
        Chunk(f, "IDAT", ms.ToArray());
        Chunk(f, "IEND", []);
    }

    private static byte[] BE(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(BE(data.Length)); s.Write(t); s.Write(data);
        s.Write(BE((int)Crc([.. t, .. data])));
    }

    private static uint Crc(byte[] d)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in d) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        return ~c;
    }
}
