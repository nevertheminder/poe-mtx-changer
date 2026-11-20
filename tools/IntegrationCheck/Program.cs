// Uses the real index and Oodle, but only applies patches to COPIES under a newly-created temp directory.
using System.Text;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Formats.Dat;
using PoeMtxChanger.Core.Installation;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Patching;
using PoeMtxChanger.Core.Storage;
using PoeMtxChanger.Core.Util;

if (args.Length < 2) { Console.WriteLine("Usage: IntegrationCheck <oodle-dll> <game-root> [report.md]"); return 2; }
var install = GameInstallationDetector.Probe(args[1], "integration read-only") ?? throw new Exception("Game not found.");
if (install.UsesGgpk) throw new Exception("This copy-based check requires a Bundles2 install.");
var codec = NativeOodleCodec.TryLoad(args[0]);
using var original = new DirectoryBundleStorage(install.Bundles2Directory);
var originalSha = FileUtil.Sha256OfFile(original.IndexPath);
using var archive = GameArchive.Open(original, codec);
var data = new DataCatalogBuilder(archive, DatSchema.LoadDefault(), CatalogRules.LoadDefault()).Build();
var cases = new List<(string Name, IReadOnlyList<ReplacementMapping> Mappings, IReadOnlyList<string> Notes)>();
void Add(string name, TargetSuggestion? target, CosmeticItem? cosmetic)
{
    if (target is null || cosmetic is null) throw new Exception("Missing integration fixture: " + name);
    var plan = SkinMappingResolver.Resolve(target, null, cosmetic, archive);
    if (plan.Mappings.Count == 0) throw new Exception(name + ": " + string.Join("; ", plan.Notes));
    cases.Add((name, plan.Mappings, plan.Notes));
}
Add("Abyss portal over default town portal", data.Suggestions.Single(s => s.SlotKey == "portal"), data.Items.Single(i => i.DisplayName == "Abyss Portal Effect"));
Add("Seraph Helmet over Aventail Helmet (all classes)", data.Suggestions.Single(s => s.SlotKey == "helmet" && s.Label == "Aventail Helmet"), data.Items.Single(i => i.DisplayName == "Seraph Helmet"));
Add("Abyss Core Body Armour over Astral Plate (all classes)", data.Suggestions.Single(s => s.SlotKey == "body" && s.Label == "Astral Plate"), data.Items.Single(i => i.DisplayName == "Abyss Core Body Armour"));
var character = data.Items.Single(i => i.DisplayName == "Seraph Character Effect");
var charTarget = data.Suggestions.First(s => s.SlotKey == "character_effect" && s.Path.EndsWith(".epk", StringComparison.OrdinalIgnoreCase) && !character.PrimaryResources.Contains(s.Path));
Add("Owned character effect to Seraph Character Effect", charTarget, character);
var cyclone = data.Skills.Single(s => s.Id == "cyclone").Variants.Single(v => v.Number == 5);
if (!cyclone.CanApply) throw new Exception(cyclone.Summary);
cases.Add(("Cyclone explicit Celestial animated roles (partial)", cyclone.Mappings, cyclone.Limitations));
var aura=BuffVisualCatalogBuilder.Plan(data.Buffs.Single(b=>b.Id=="player_aura_cold_damage"),data.Buffs.Single(b=>b.Id=="player_aura_cold_damage_alternate"),archive);
if(!aura.CanApply)throw new Exception(aura.Summary);
cases.Add(("Hatred cold-damage EPK to skull aura (explicit visual roles)",aura.Mappings,aura.Limitations));
var staffBase=WeaponVisuals.BaseItem(data.Suggestions,"Staff",null)!;
var staffSkin=data.Items.Single(i=>i.DisplayName=="Celestial Staff");
var staffEffect=data.Items.Single(i=>i.DisplayName=="Celestial Weapon Effect");
var weaponPlan=SkinMappingResolver.Resolve(staffBase,null,staffSkin,archive);
if(weaponPlan.Mappings.Count!=1)throw new Exception("Expected one held weapon AO replacement.");
var equippedEffect=data.Items.Where(i=>i.IsWeaponEffect).Select(i=>WeaponVisuals.ReplaceEffect(i,staffEffect,staffBase))
    .OfType<ReplacementMapping>().First(m=>!new MappingValidator(ResourcePolicy.Default).Validate([m],archive).HasErrors);
cases.Add(("Celestial Staff held model and separate staff EPK",[..weaponPlan.Mappings,equippedEffect],weaponPlan.Notes));
foreach(string family in new[]{"beam:","pack:"})
{
    var candidate=data.Skills.SelectMany(s=>s.Variants.Select(v=>(Skill:s,Variant:v)))
        .First(pair=>pair.Variant.CanApply&&pair.Variant.Mappings.Any(m=>m.Note?.StartsWith(family)==true)
            &&!new MappingValidator(ResourcePolicy.Default).Validate(pair.Variant.Mappings,archive).HasErrors);
    cases.Add(($"{family} {candidate.Skill.Id} variant {candidate.Variant.Number}",candidate.Variant.Mappings,candidate.Variant.Limitations));
}

var scratch = Path.Combine(Path.GetTempPath(), "PoeMtxChanger-Integration-" + Guid.NewGuid().ToString("N"));
var isolated = Path.Combine(scratch, "copy", "Bundles2");
if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(install.RootPath).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
    throw new Exception("Refusing an integration directory inside the live installation.");
Directory.CreateDirectory(isolated);
using var copy = new DirectoryBundleStorage(isolated);
File.Copy(original.IndexPath, copy.IndexPath);
var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var path in cases.SelectMany(c => c.Mappings.SelectMany(m => new[] { m.TargetPath, m.SourcePath })).Distinct(StringComparer.OrdinalIgnoreCase))
{
    if (!archive.TryGetRecord(path, out var record)) throw new Exception("Not in index: " + path);
    var bundle = archive.Index.Bundles[record.BundleIndex].Name;
    if (inputs.ContainsKey(bundle)) continue;
    var from = original.GetBundlePath(bundle); var to = copy.GetBundlePath(bundle);
    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
    File.Copy(from, to);
    inputs[bundle] = FileUtil.Sha256OfFile(from);
}
Console.WriteLine($"Isolated copy: {scratch}; copied {inputs.Count} source/target bundles.");
var lines = new List<string>
{
    "# Real-resource copy integration check", "", $"UTC: {DateTime.UtcNow:O}", $"Game build: {install.SteamBuildId}",
    $"Original index SHA-256: `{originalSha}`", $"Scratch copy: `{scratch}`", "",
    "No live game files are patched. Dependencies are checked against the original read-only archive, then writes and restore are checked on a full index copy with copied source/target bundles.", "",
    "| Scenario | Mappings | Result |", "|---|---:|---|",
};
bool passed = true;
var caveats = new List<string>();
foreach (var test in cases)
{
    Console.WriteLine("Checking: " + test.Name);
    var validation = new MappingValidator(ResourcePolicy.Default).Validate(test.Mappings, archive);
    if (validation.HasErrors)
    {
        passed = false;
        var reason = string.Join("; ", validation.Issues.Where(i => i.Severity == IssueSeverity.Error).Take(5).Select(i => i.Code + ": " + i.Message));
        Console.WriteLine("BLOCKED: " + reason);
        lines.Add($"| {test.Name} | {test.Mappings.Count} | BLOCKED: {reason.Replace('|', '/')} |");
        continue;
    }
    var engine = new PatchEngine(copy, codec, Path.Combine(scratch, "backups"), isGameRunning: () => false);
    var result = engine.Apply(new PatchRequest { Mappings = test.Mappings, ProfileName = test.Name, UserConfirmed = true, AnalyzeDependencies = false });
    using (var patched = GameArchive.Open(copy, codec))
        foreach (var mapping in test.Mappings)
        {
            var expectedContent = mapping.VisualIdentityEdit == null ? archive.Read(mapping.SourcePath)
                : VisualIdentityEditor.Apply(archive.Read(VisualIdentityEditor.TablePath), test.Mappings.Where(m => m.VisualIdentityEdit != null).ToList());
            if (FileUtil.Sha256(patched.Read(mapping.TargetPath)) != FileUtil.Sha256(expectedContent))
                throw new Exception("Patched content mismatch: " + mapping.TargetPath);
            if (mapping.VisualIdentityEdit is { } edit)
            {
                var table = DatTable.Parse(patched.Read(mapping.TargetPath), DatSchema.LoadDefault().Tables["ItemVisualIdentity"]);
                if (!table.GetStringArray(edit.Row, edit.Column).SequenceEqual([mapping.SourcePath]))
                    throw new Exception("Full .ao appearance reference was not preserved.");
            }
        }
    engine.Restore(userConfirmed: true);
    if (FileUtil.Sha256OfFile(copy.IndexPath) != originalSha) throw new Exception("Restore hash mismatch.");
    Console.WriteLine($"PASS: {result.ReplacedFiles} files, exact source hashes, exact original index restored.");
    lines.Add($"| {test.Name} | {test.Mappings.Count} | PASS: apply/readback/restore SHA-256 |");
    foreach (var note in test.Notes) caveats.Add($"- {test.Name}: {note}");
    GC.Collect();
}
if (FileUtil.Sha256OfFile(original.IndexPath) != originalSha) throw new Exception("Live index changed externally during the check.");
foreach (var (bundle, sha) in inputs)
    if (FileUtil.Sha256OfFile(original.GetBundlePath(bundle)) != sha) throw new Exception("Live input bundle changed externally: " + bundle);
lines.Add("");
lines.AddRange(caveats);
lines.AddRange(["", "The live index and all source/target input bundles have their original SHA-256 hashes after the check.",
    "This verifies archive encoding, content redirection and restore. It does not verify rendering or acceptance inside the running game."]);
var report = args.Length > 2 ? args[2] : "docs/INTEGRATION_CHECK.md";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
File.WriteAllLines(report, lines, Encoding.UTF8);
Console.WriteLine("Live input hashes unchanged. Report: " + Path.GetFullPath(report));
return passed ? 0 : 1;
