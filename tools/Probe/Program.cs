// Read-only probe against a real installation: decompresses the index, resolves paths, reads data tables,
// decodes icons and round-trips compression. Never writes into the game folder.
// Usage: dotnet run --project tools/Probe -- <oodle-dll> [<game-root>]
using System.Diagnostics;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Formats.Bundles;
using PoeMtxChanger.Core.Formats.Dat;
using PoeMtxChanger.Core.Formats.Dds;
using PoeMtxChanger.Core.Installation;
using PoeMtxChanger.Core.Storage;

var dll = args[0];
var inst = args.Length > 1 ? GameInstallationDetector.Probe(args[1], "arg") : GameInstallationDetector.DetectAll().FirstOrDefault();
if (inst is null) { Console.WriteLine("no installation"); return 1; }
Console.WriteLine($"install: {inst.RootPath} ({inst.Kind}, build {inst.SteamBuildId})");

var codec = NativeOodleCodec.TryLoad(dll);
Console.WriteLine($"codec: {(codec.IsAvailable ? "ok" : "FAIL")} {codec.Description}");
if (!codec.IsAvailable) return 1;

var sw = Stopwatch.StartNew();
using var archive = GameArchive.Open(inst.OpenStorage(), codec);
if (Environment.GetEnvironmentVariable("PROBE_EXPLORE") is { } needle)
{
    Explore.Run(archive, DatSchema.LoadDefault(), [needle]);
    return 0;
}
Console.WriteLine($"index: {archive.Index.Files.Count:N0} files, {archive.Index.Bundles.Count:N0} bundles, {archive.Index.Directories.Count:N0} dirs, " +
                  $"hash {archive.Index.HashAlgorithm}, unresolved generated paths {archive.UnresolvedPathCount}, " +
                  $"files without path {archive.Index.Files.Count(f => f.Path is null)} ({sw.ElapsedMilliseconds} ms)");
var foreign = archive.Index.Bundles.Where(b => b.Name.Contains('/') && (b.Name.StartsWith("LibGGPK3") || b.Name.StartsWith("PoeMtxChanger"))).ToList();
Console.WriteLine($"foreign/modded bundles in index: {foreign.Count}");

foreach (var p in new[] { "Data/MtxTypes.datc64", "Data/BaseItemTypes.datc64", "Data/ItemVisualIdentity.datc64", "Data/MicrotransactionPortalVariations.datc64" })
    Console.WriteLine($"  exists {p}: {archive.Exists(p)}");
Console.WriteLine("sample paths: " + string.Join(" | ", archive.EnumeratePaths().Where(p => p.Contains("Microtransaction", StringComparison.OrdinalIgnoreCase)).Take(5)));

sw.Restart();
var schema = DatSchema.LoadDefault();
var result = new DataCatalogBuilder(archive, schema, CatalogRules.LoadDefault()).Build();
Console.WriteLine($"\ndata catalog: {result.Items.Count:N0} items, {result.Suggestions.Count:N0} slot targets ({sw.ElapsedMilliseconds} ms)");
Console.WriteLine($"skill catalog: {result.Skills.Count} skills, {result.Skills.Sum(s => s.Variants.Count(v => v.CanApply))} variants with explicit mappings");
foreach (var skill in result.Skills.Where(s => s.Id.Contains("cyclone", StringComparison.OrdinalIgnoreCase)).Take(3))
    foreach (var variant in skill.Variants.Where(v => v.CanApply).Take(4))
        Console.WriteLine($"  {skill.Id} / {variant.Name}: {variant.Mappings.Count} files; limitations {variant.Limitations.Count}");
foreach (var d in result.Diagnostics) Console.WriteLine($"  {(d.Ok ? "OK " : "ERR")} {d.Table}: {d.Message}");
foreach (var g in result.Items.GroupBy(i => (i.Category, i.Status)).OrderBy(g => g.Key.Category))
    Console.WriteLine($"  {g.Key.Category,-20} {g.Key.Status,-13} {g.Count(),6}");
Console.WriteLine("sample items:");
foreach (var i in result.Items.Where(i => i.PrimaryResources.Count > 0).Take(12))
    Console.WriteLine($"  [{i.Category}] {i.DisplayName} :: {i.PrimaryResources[0]} :: icon {i.IconPath}");
Console.WriteLine("sample targets:");
foreach (var s in result.Suggestions.GroupBy(s => s.SlotKey).Select(g => g.First())) Console.WriteLine($"  {s.SlotKey}: {s.Label} -> {s.Path}");

Console.WriteLine("portal targets:");
foreach (var s in result.Suggestions.Where(s => s.SlotKey == "portal")) Console.WriteLine($"  {s.Path}  policy: {PoeMtxChanger.Core.Mapping.ResourcePolicy.Default.WhyTargetDenied(s.Path) ?? "ok"}");

// Resolve + validate real cosmetic → base pairs (read-only)
foreach (var (cosName, slot, baseName) in new[] { ("Seraph Helmet", "helmet", "Aventail Helmet"), ("Abyss Portal Effect", "portal", ""), ("", "body", "Astral Plate") })
{
    var cos = cosName.Length > 0 ? result.Items.FirstOrDefault(i => i.DisplayName == cosName)
        : result.Items.FirstOrDefault(i => i.Category == CosmeticCategory.BodyArmour && i.ClassVariants.Count == 7);
    var target = baseName.Length > 0 ? result.Suggestions.FirstOrDefault(s => s.SlotKey == slot && s.Label == baseName) : result.Suggestions.FirstOrDefault(s => s.SlotKey == slot);
    if (cos is null || target is null) { Console.WriteLine($"\nresolve {cosName}/{baseName}: not found"); continue; }
    var res = PoeMtxChanger.Core.Mapping.SkinMappingResolver.Resolve(target, null, cos, archive);
    var rep = new PoeMtxChanger.Core.Mapping.MappingValidator(PoeMtxChanger.Core.Mapping.ResourcePolicy.Default).Validate(res.Mappings, archive);
    Console.WriteLine($"\nresolve '{cos.DisplayName}' → '{target.Label}': {res.Mappings.Count} mappings, status {rep.Status}");
    foreach (var m in res.Mappings.Take(3)) Console.WriteLine($"  [{m.Note}] {m.TargetPath} <- {m.SourcePath}");
    foreach (var n in res.Notes.Distinct()) Console.WriteLine($"  note: {n}");
    foreach (var i in rep.Issues.Where(i => i.Severity != PoeMtxChanger.Core.Mapping.IssueSeverity.Info).Take(5)) Console.WriteLine($"  {i.Severity} {i.Code}: {i.Message} {i.Path}");
}

var formats = new Dictionary<string, int>();
int ok = 0, tried = 0;
foreach (var i in result.Items.Where(i => i.IconPath != null).Take(60))
{
    tried++;
    try
    {
        if (!archive.Exists(i.IconPath!)) { formats["(missing)"] = formats.GetValueOrDefault("(missing)") + 1; continue; }
        var img = DdsDecoder.Decode(archive.Read(i.IconPath!), p => archive.Exists(p) ? archive.Read(p) : null);
        formats[img.Format] = formats.GetValueOrDefault(img.Format) + 1;
        ok++;
    }
    catch (Exception ex) { var k = ex.GetType().Name + ": " + ex.Message; formats[k] = formats.GetValueOrDefault(k) + 1; }
}
Console.WriteLine($"\nicons decoded {ok}/{tried}:");
foreach (var (k, v) in formats) Console.WriteLine($"  {v,4}  {k}");

// Compression round trip through the same library, on real data.
var sampleRec = archive.Index.Files.FirstOrDefault(f => f.Path?.EndsWith(".ao", StringComparison.OrdinalIgnoreCase) == true && f.Size > 2000)
                ?? archive.Index.Files.First(f => f.Size > 2000);
Console.WriteLine($"roundtrip sample: {sampleRec.Path}");
var sample = archive.Read(sampleRec);
foreach (var comp in new[] { OodleCompressor.Mermaid, OodleCompressor.Kraken, OodleCompressor.Leviathan })
{
    var bundle = BundleFile.Compress(sample, codec, comp, OodleCompressionLevel.Normal);
    var back = BundleFile.Decompress(bundle, codec);
    Console.WriteLine($"roundtrip {comp}: {sample.Length} -> {bundle.Length} bytes, equal={back.AsSpan().SequenceEqual(sample)}");
}
return 0;
