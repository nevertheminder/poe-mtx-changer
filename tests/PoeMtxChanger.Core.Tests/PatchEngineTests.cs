using PoeMtxChanger.Core.Formats.Bundles;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Patching;
using PoeMtxChanger.Core.Storage;
using PoeMtxChanger.Core.Util;

namespace PoeMtxChanger.Core.Tests;

public class PatchEngineTests : IDisposable
{
    private const string Target = "Metadata/Effects/Misc/portal/townportal.aoc";
    private const string Source = "Metadata/Effects/Microtransactions/portals/emberportal/emberportal.aoc";

    private readonly TempDir _tmp = new();
    private readonly Dictionary<string, byte[]> _files = SyntheticGame.SampleFiles();
    private readonly DirectoryBundleStorage _storage;
    private bool _gameRunning;

    public PatchEngineTests() => _storage = SyntheticGame.Create(_tmp.Combine("game"), _files);
    public void Dispose() { _storage.Dispose(); _tmp.Dispose(); }

    private PatchEngine Engine() => new(_storage, SyntheticGame.Codec, _tmp.Combine("backups"), isGameRunning: () => _gameRunning);

    private static PatchRequest Request(bool confirmed = true, params ReplacementMapping[] mappings) => new()
    {
        Mappings = mappings.Length > 0 ? mappings : [new ReplacementMapping { TargetPath = Target, SourcePath = Source }],
        UserConfirmed = confirmed,
        ProfileName = "test",
    };

    private byte[] ReadLive(string path)
    {
        using var a = GameArchive.Open(_storage, SyntheticGame.Codec);
        return a.Read(path);
    }

    [Fact]
    public void Apply_redirects_target_and_restore_brings_back_original()
    {
        var engine = Engine();
        var originalIndexSha = FileUtil.Sha256OfFile(_storage.IndexPath);
        var result = engine.Apply(Request());

        Assert.Equal(GameFileStatus.PatchedByUs, engine.GetStatus());
        Assert.Equal(_files[Source], ReadLive(Target));
        Assert.Equal(_files[Source], ReadLive(Source));                       // source itself untouched
        Assert.Equal(_files["Art/Models/Effects/portal/portal.ast"], ReadLive("Art/Models/Effects/portal/portal.ast"));
        Assert.True(File.Exists(_storage.GetBundlePath(result.BundleName)));
        Assert.Equal(1, result.ReplacedFiles);

        var restore = engine.Restore(userConfirmed: true);
        Assert.True(restore.IndexRestored);
        Assert.Equal(1, restore.BundlesDeleted);
        Assert.Equal(originalIndexSha, FileUtil.Sha256OfFile(_storage.IndexPath));
        Assert.Equal(_files[Target], ReadLive(Target));
        Assert.False(File.Exists(_storage.GetBundlePath(result.BundleName)));
        Assert.Equal(GameFileStatus.Clean, engine.GetStatus());
    }

    [Fact]
    public void Second_apply_starts_from_baseline_and_removes_previous_bundle()
    {
        var engine = Engine();
        var first = engine.Apply(Request());
        var second = engine.Apply(Request(true, new ReplacementMapping
        {
            TargetPath = "Art/Models/Effects/portal/portal.ast",
            SourcePath = "Art/Microtransactions/portals/emberportal/ember.ast",
        }));

        Assert.False(File.Exists(_storage.GetBundlePath(first.BundleName)));
        Assert.True(File.Exists(_storage.GetBundlePath(second.BundleName)));
        Assert.Equal(_files[Target], ReadLive(Target)); // first profile no longer applied: no stacking
        Assert.Equal(_files["Art/Microtransactions/portals/emberportal/ember.ast"], ReadLive("Art/Models/Effects/portal/portal.ast"));
    }

    [Fact]
    public void Apply_requires_confirmation_and_a_closed_game()
    {
        var engine = Engine();
        var before = FileUtil.Sha256OfFile(_storage.IndexPath);
        Assert.Equal("CONFIRM", Assert.Throws<PatchException>(() => engine.Apply(Request(confirmed: false))).Code);
        _gameRunning = true;
        Assert.Equal("GAME_RUNNING", Assert.Throws<PatchException>(() => engine.Apply(Request())).Code);
        Assert.Equal(before, FileUtil.Sha256OfFile(_storage.IndexPath));
    }

    [Fact]
    public void Policy_and_validation_errors_leave_files_untouched()
    {
        var engine = Engine();
        var before = FileUtil.Sha256OfFile(_storage.IndexPath);

        var dat = Assert.Throws<ValidationFailedException>(() => engine.Apply(Request(true,
            new ReplacementMapping { TargetPath = "Data/BaseItemTypes.dat64", SourcePath = "Data/BaseItemTypes.dat64" })));
        Assert.Contains(dat.Report.Issues, i => i.Code == "POLICY");

        var mock = Assert.Throws<ValidationFailedException>(() => engine.Apply(Request(true,
            new ReplacementMapping { TargetPath = Target, SourcePath = Source, SourceCosmeticId = "mock/portal/x" })));
        Assert.Contains(mock.Report.Issues, i => i.Code == "MOCK");

        var kind = Assert.Throws<ValidationFailedException>(() => engine.Apply(Request(true,
            new ReplacementMapping { TargetPath = Target, SourcePath = "Art/Microtransactions/portals/emberportal/ember.dds" })));
        Assert.Contains(kind.Report.Issues, i => i.Code == "KIND");

        Assert.Equal(before, FileUtil.Sha256OfFile(_storage.IndexPath));
        Assert.Empty(Directory.GetFiles(_storage.Bundles2Directory, "*.pmc-staged", SearchOption.AllDirectories));
    }

    [Fact]
    public void External_change_is_detected_and_blocks_apply_until_new_baseline()
    {
        var engine = Engine();
        engine.Apply(Request());

        // Simulate a game update: index replaced with a different, clean one.
        File.Delete(_storage.IndexPath);
        var updated = new Dictionary<string, byte[]>(_files) { ["Art/new_in_patch.dds"] = [9, 9, 9] };
        SyntheticGame.Create(_tmp.Combine("game"), updated).Dispose();
        Assert.Equal(GameFileStatus.ChangedExternally, engine.GetStatus());
        Assert.Equal("CHANGED", Assert.Throws<PatchException>(() => engine.Apply(Request())).Code);

        var restore = engine.Restore(userConfirmed: true);
        Assert.False(restore.IndexRestored); // never overwrite an index we did not write
        engine.AdoptCurrentAsBaseline(userConfirmed: true);
        Assert.Equal(GameFileStatus.Clean, engine.GetStatus());
        engine.Apply(Request());
        Assert.Equal(_files[Source], ReadLive(Target));
        Assert.Equal([9, 9, 9], ReadLive("Art/new_in_patch.dds"));
    }

    [Fact]
    public void Baseline_is_refused_for_an_index_already_modded_by_another_tool()
    {
        var idxRaw = File.ReadAllBytes(_storage.IndexPath);
        var idx = BundleIndex.Parse(BundleFile.Decompress(idxRaw, SyntheticGame.Codec));
        idx.AddBundle("LibGGPK3/0", 0);
        File.WriteAllBytes(_storage.IndexPath, BundleFile.Compress(idx.Serialize(), SyntheticGame.Codec,
            Compression.OodleCompressor.Mermaid, Compression.OodleCompressionLevel.Normal));
        Assert.Equal("FOREIGN", Assert.Throws<PatchException>(() => Engine().Apply(Request())).Code);
    }

    [Fact]
    public void Tampered_baseline_backup_is_detected()
    {
        var engine = Engine();
        engine.Apply(Request());
        var bytes = File.ReadAllBytes(engine.BaselinePath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(engine.BaselinePath, bytes);
        Assert.Equal("BASELINE", Assert.Throws<PatchException>(() => engine.Restore(userConfirmed: true)).Code);
        Assert.Equal(_files[Source], ReadLive(Target)); // patched index left as is, not replaced by a bad backup
    }

    [Fact]
    public void Restore_without_any_change_is_a_no_op()
    {
        var r = Engine().Restore(userConfirmed: true);
        Assert.False(r.IndexRestored);
        Assert.Equal(0, r.BundlesDeleted);
    }

    private sealed class CallbackProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }

    [Fact]
    public void Interrupted_commit_is_recovered_using_the_persisted_journal()
    {
        var engine = Engine();
        var result = engine.Apply(Request());
        var oldState = engine.LoadState()!;
        oldState.AppliedSha256 = null;
        oldState.AppliedUtc = null;
        oldState.AppliedProfileName = null;
        oldState.CustomBundles = [];
        oldState.AppliedMappings = [];
        FileUtil.WriteJsonAtomic(engine.StatePath, oldState);
        File.Copy(engine.BaselinePath, engine.PreviousIndexPath);
        FileUtil.WriteJsonAtomic(engine.JournalPath, new PendingPatchTransaction
        {
            PreviousSha256 = oldState.BaselineSha256, NextSha256 = result.IndexSha256,
            BundleName = result.BundleName, PreviousState = oldState,
        });
        Assert.Equal(GameFileStatus.RecoveryRequired, engine.GetStatus());
        engine.Restore(userConfirmed: true);
        Assert.Equal(GameFileStatus.Clean, engine.GetStatus());
        Assert.Equal(_files[Target], ReadLive(Target));
        Assert.False(File.Exists(engine.JournalPath));
        Assert.False(File.Exists(_storage.GetBundlePath(result.BundleName)));
    }

    [Fact]
    public void Concurrent_apply_is_refused_and_a_missing_baseline_is_never_recreated_from_a_patch()
    {
        var engine = Engine();
        engine.Apply(Request(), new CallbackProgress(stage =>
        {
            if (stage == "Committing…") Assert.Equal("BUSY", Assert.Throws<PatchException>(() => Engine().Apply(Request())).Code);
        }));
        File.Delete(engine.BaselinePath);
        Assert.Equal("BASELINE", Assert.Throws<PatchException>(() => engine.Apply(Request())).Code);
        Assert.False(File.Exists(engine.BaselinePath));
    }

    [Fact]
    public void External_index_change_during_staging_is_never_overwritten()
    {
        var engine = Engine();
        byte[] external = [1, 2, 3, 4];
        var ex = Assert.Throws<PatchException>(() => engine.Apply(Request(), new CallbackProgress(stage =>
        {
            if (stage == "Committing…") File.WriteAllBytes(_storage.IndexPath, external);
        })));
        Assert.Equal("CHANGED", ex.Code);
        Assert.Equal(external, File.ReadAllBytes(_storage.IndexPath));
    }

    [Fact]
    public void Failure_after_swapping_a_second_profile_restores_the_previous_profile()
    {
        var engine = Engine();
        var first = engine.Apply(Request());
        var before = File.ReadAllBytes(_storage.IndexPath);
        Assert.Throws<PatchException>(() => engine.Apply(Request(true, new ReplacementMapping
        {
            TargetPath = "Art/Models/Effects/portal/portal.ast", SourcePath = "Art/Microtransactions/portals/emberportal/ember.ast",
        }), new CallbackProgress(stage =>
        {
            if (stage == "Saving transaction state…") throw new IOException("simulated disk failure");
        })));
        Assert.Equal(before, File.ReadAllBytes(_storage.IndexPath));
        Assert.Equal(first.IndexSha256, engine.LoadState()!.AppliedSha256);
        Assert.True(File.Exists(_storage.GetBundlePath(first.BundleName)));
        Assert.Equal(_files[Source], ReadLive(Target));
    }
}
