// Dev tool (read-only): composes pixel shaders for real materials from the game's own fragments and compiles them with
// the system D3D compiler. Reports how many compile and why the rest fail.
// Usage: dotnet run --project tools/ShaderCheck -c Release -- <oodle-dll> <game-root> [count] [dump-dir]
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Formats.Shaders;
using PoeMtxChanger.Core.Installation;
using PoeMtxChanger.Core.Rendering;
using PoeMtxChanger.Core.Resources;
using PoeMtxChanger.Core.Storage;

var codec = NativeOodleCodec.TryLoad(args[0]);
var inst = GameInstallationDetector.Probe(args[1], "arg") ?? throw new InvalidOperationException("no install");
int count = args.Length > 2 ? int.Parse(args[2]) : 600;
string? dump = args.Length > 3 ? args[3] : null;
using var archive = GameArchive.Open(inst.OpenStorage(), codec);
var paths = archive.EnumeratePaths().ToList();
string Text(string p) => ReferenceExtractor.DecodeText(archive.Read(p)) ?? "";

var lib = FfxLibrary.Parse(paths.Where(p => p.EndsWith(".ffx", StringComparison.OrdinalIgnoreCase)).Select(p => (p, Text(p))));
Console.WriteLine($"library: {lib.Fragments.Count} fragments, {lib.ExtensionPoints.Count} extension points, {lib.Declarations.Count} declarations, {lib.Warnings.Count} warnings");
foreach (var w in lib.Warnings.Take(5)) Console.WriteLine("  warn: " + w);

var graphCache = new Dictionary<string, FxGraph?>(StringComparer.OrdinalIgnoreCase);
FxGraph? LoadGraph(string p) => graphCache.TryGetValue(p, out var g) ? g : graphCache[p] = archive.Exists(p) ? FxGraph.Parse(p, Text(p)) : null;
var builder = new MaterialShaderBuilder(lib, LoadGraph, p => archive.Exists(p) ? Text(p) : null);

// Deterministic spread over item / microtransaction / effect materials.
var mats = paths.Where(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)
        && (p.Contains("/items/") || p.Contains("microtransactions") || p.Contains("/effects/") || p.Contains("/characters/")))
    .OrderBy(p => p, StringComparer.Ordinal).ToList();
int step = Math.Max(1, mats.Count / count);
var sample = mats.Where((_, i) => i % step == 0).Take(count).ToList();
Console.WriteLine($"materials considered: {mats.Count}, sampled: {sample.Count}");

int ok = 0, fail = 0, composeErr = 0, totalNodes = 0;
var errors = new Dictionary<string, int>(); var notes = new Dictionary<string, int>(); var examples = new Dictionary<string, string>();
foreach (var path in sample)
{
    ComposedMaterialShader shader;
    try { shader = builder.Build(FxMaterial.Parse(path, Text(path))); }
    catch (Exception ex) { composeErr++; var k = "compose: " + ex.GetType().Name + ": " + ex.Message; errors[k] = errors.GetValueOrDefault(k) + 1; continue; }
    totalNodes += shader.EmittedNodes;
    foreach (var n in shader.Notes) { var k = Regex.Replace(n, @"[: ].*", "") is var head && n.Length > 0 ? Regex.Replace(n, @"(graph|skipped).*", "$1") : n; notes[k] = notes.GetValueOrDefault(k) + 1; }
    var error = D3D.Compile(shader.Hlsl, ComposedMaterialShader.EntryPoint, ComposedMaterialShader.Profile);
    if (error is null) ok++;
    else
    {
        fail++;
        var first = error.Split('\n').FirstOrDefault(l => l.Contains("error")) ?? error.Split('\n')[0];
        var key = Regex.Replace(first, @"^.*?\(\d+,\d+(-\d+)?\):\s*", "").Trim();
        errors[key] = errors.GetValueOrDefault(key) + 1;
        if (!examples.ContainsKey(key))
        {
            examples[key] = path;
            if (dump != null) { Directory.CreateDirectory(dump); File.WriteAllText(Path.Combine(dump, $"fail{examples.Count:00}.hlsl"), $"// {path}\n// {first}\n" + shader.Hlsl); }
        }
    }
}
Console.WriteLine($"\ncompiled OK: {ok}/{sample.Count} ({100.0 * ok / Math.Max(1, sample.Count):F1}%), compile errors {fail}, compose errors {composeErr}, avg nodes {totalNodes / Math.Max(1, ok + fail):F0}");
Console.WriteLine("top errors:");
foreach (var (k, v) in errors.OrderByDescending(x => x.Value).Take(15)) Console.WriteLine($"  {v,4}  {k}   e.g. {examples.GetValueOrDefault(k)}");
Console.WriteLine("top preview notes:");
foreach (var (k, v) in notes.OrderByDescending(x => x.Value).Take(20)) Console.WriteLine($"  {v,4}  {k}");

static class D3D
{
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] src, nint size, string? name, nint defines, nint include, string entry, string target, uint flags1, uint flags2, out nint code, out nint errors);

    public static unsafe string? Compile(string hlsl, string entry, string profile)
    {
        var src = Encoding.UTF8.GetBytes(hlsl);
        int hr = D3DCompile(src, src.Length, "material.hlsl", 0, 0, entry, profile, 0 /* default optimisation, non-strict like the game */, 0, out var code, out var err);
        string? message = null;
        if (err != 0) { message = Marshal.PtrToStringAnsi(Ptr(err), (int)Size(err)); Release(err); }
        if (code != 0) Release(code);
        return hr >= 0 ? null : message ?? $"HRESULT 0x{hr:X8}";
    }

    private static unsafe nint Ptr(nint blob) => ((delegate* unmanaged<nint, nint>)(*(nint**)blob)[3])(blob);
    private static unsafe nint Size(nint blob) => ((delegate* unmanaged<nint, nint>)(*(nint**)blob)[4])(blob);
    private static unsafe void Release(nint blob) => ((delegate* unmanaged<nint, uint>)(*(nint**)blob)[2])(blob);
}
