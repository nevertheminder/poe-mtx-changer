using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Formats.Ggpk;
using PoeMtxChanger.Core.Installation;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Storage;
using PoeMtxChanger.Core.Util;

namespace PoeMtxChanger.Core.Patching;

/// <summary>Standalone client: full backup, isolated bundle staging, streamed GGPK rebuild, hash-checked atomic replacement.</summary>
public sealed class StandalonePatchEngine(string ggpkPath, IOodleCodec codec, string backupRoot, Func<bool>? gameRunning = null) : IGamePatchEngine
{
    private readonly AtomicFilePatch _file = new(ggpkPath, backupRoot, gameRunning);
    public GameFileStatus GetStatus() => _file.GetStatus();
    public PatchState? LoadState() => _file.LoadState() is { } s ? new PatchState
    {
        GameRoot = Path.GetDirectoryName(s.TargetPath)!, BaselineSha256 = s.BaselineSha256, BaselineSize = s.BaselineSize,
        BaselineCreatedUtc = s.BaselineCreatedUtc, AppliedSha256 = s.AppliedSha256, AppliedUtc = s.AppliedUtc, AppliedProfileName = s.ProfileName,
    } : null;
    public byte[] ReadCleanIndex()
    {
        var state = _file.LoadState() ?? throw new PatchException("BASELINE", "No GGPK backup available.");
        if (FileUtil.Sha256OfFile(_file.BaselinePath) != state.BaselineSha256) throw new PatchException("BASELINE", "GGPK backup hash mismatch.");
        using var storage = new GgpkBundleStorage(_file.BaselinePath); return storage.ReadIndexBytes();
    }
    public Task<PatchResult> ApplyAsync(PatchRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Apply(request, progress, ct), ct);
    public PatchResult Apply(PatchRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!codec.IsAvailable) throw new PatchException("NO_OODLE", codec.Description);
        if (!request.UserConfirmed) throw new PatchException("CONFIRM", "Change was not confirmed.");
        if ((gameRunning ?? (() => GameProcessGuard.IsGameRunning))()) throw new PatchException("GAME_RUNNING", "Close the game before applying.");
        if (_file.GetStatus() == GameFileStatus.NoBaseline)
        {
            using var check = GameArchive.Open(new GgpkBundleStorage(ggpkPath), codec);
            if (check.Index.Bundles.Any(b => b.Name.StartsWith("LibGGPK3/", StringComparison.OrdinalIgnoreCase) || b.Name.StartsWith(PatchEngine.CustomBundlePrefix, StringComparison.OrdinalIgnoreCase)))
                throw new PatchException("FOREIGN", "GGPK already contains modded bundles; restore a clean installation first.");
        }
        PatchResult? result = null;
        _file.Apply(request.UserConfirmed, request.ProfileName, (baseline, stage, token) =>
        {
            using var archive = GameArchive.Open(new GgpkBundleStorage(baseline), codec);
            var report = new MappingValidator(ResourcePolicy.Default).Validate(request.Mappings, archive, request.AnalyzeDependencies);
            if (report.HasErrors) throw new ValidationFailedException(report);
            string work = Path.Combine(_file.BackupDirectory, "build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                using var disk = new DirectoryBundleStorage(Path.Combine(work, "Bundles2"));
                Directory.CreateDirectory(disk.Bundles2Directory);
                File.WriteAllBytes(disk.IndexPath, archive.Storage.ReadIndexBytes());
                using var ggpk = GgpkArchive.OpenRead(baseline);
                foreach (var resource in request.Mappings.SelectMany(m => new[] { m.SourcePath, m.TargetPath }).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!archive.TryGetRecord(resource, out var record)) throw new PatchException("SOURCE_MISSING", resource);
                    var name = archive.Index.Bundles[record.BundleIndex].Name;
                    string destination = disk.GetBundlePath(name);
                    if (File.Exists(destination)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = ggpk.OpenFile(ggpk.BundleFiles["Bundles2/" + name + ".bundle.bin"]);
                    using var output = File.Create(destination); input.CopyTo(output);
                }
                var plan = new PatchEngine(disk, codec, Path.Combine(work, "index-backup"), isGameRunning: gameRunning);
                result = plan.Apply(new PatchRequest { UserConfirmed = true, Mappings = request.Mappings, ProfileName = request.ProfileName,
                    AnalyzeDependencies = false, Compressor = request.Compressor, Level = request.Level }, progress, token) with { Report = report };
                var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Bundles2/_.index.bin"] = disk.IndexPath,
                    ["Bundles2/" + result.BundleName + ".bundle.bin"] = disk.GetBundlePath(result.BundleName),
                };
                GgpkRebuilder.Rebuild(baseline, stage, replacements, progress, token);
            }
            finally { Directory.Delete(work, recursive: true); }
        }, (stage, token) =>
        {
            using var staged = GameArchive.Open(new GgpkBundleStorage(stage), codec);
            using var baseline = GameArchive.Open(new GgpkBundleStorage(_file.BaselinePath), codec);
            var structured = request.Mappings.Where(m => m.VisualIdentityEdit != null).ToList();
            foreach (var m in request.Mappings)
            {
                token.ThrowIfCancellationRequested();
                var expected = m.VisualIdentityEdit == null ? baseline.Read(m.SourcePath) : VisualIdentityEditor.Apply(baseline.Read(VisualIdentityEditor.TablePath), structured);
                if (FileUtil.Sha256(staged.Read(m.TargetPath)) != FileUtil.Sha256(expected)) throw new PatchException("VERIFY", "Staged GGPK content mismatch: " + m.TargetPath);
            }
        }, progress, ct);
        return result ?? throw new PatchException("APPLY", "No GGPK patch result.");
    }
    public Task<RestoreResult> RestoreAsync(bool userConfirmed, CancellationToken ct = default) => Task.Run(() =>
    {
        bool restored = _file.Restore(userConfirmed, ct: ct);
        return new RestoreResult(restored, 0, restored ? "Original Content.ggpk restored and verified." : "Content.ggpk is already original.");
    }, ct);
    public void AdoptCurrentAsBaseline(bool userConfirmed) => _file.Adopt(userConfirmed);
}
