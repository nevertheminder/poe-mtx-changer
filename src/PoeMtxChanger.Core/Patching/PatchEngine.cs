using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Formats.Bundles;
using PoeMtxChanger.Core.Installation;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Storage;
using PoeMtxChanger.Core.Util;

namespace PoeMtxChanger.Core.Patching;

public enum GameFileStatus
{
    RecoveryRequired,
    /// <summary>No baseline backup has been taken yet.</summary>
    NoBaseline,
    /// <summary>Index matches the baseline backup.</summary>
    Clean,
    /// <summary>Index is the one this tool wrote last.</summary>
    PatchedByUs,
    /// <summary>Index matches neither (game update, Steam verify, or another tool).</summary>
    ChangedExternally,
    /// <summary>State exists but the baseline file is missing or corrupt.</summary>
    BaselineDamaged,
}

public sealed class PatchState
{
    public int SchemaVersion { get; set; } = 1;
    public string GameRoot { get; set; } = "";
    public string BaselineSha256 { get; set; } = "";
    public long BaselineSize { get; set; }
    public DateTime BaselineCreatedUtc { get; set; }
    public string? AppliedSha256 { get; set; }
    public DateTime? AppliedUtc { get; set; }
    public string? AppliedProfileName { get; set; }
    public List<string> CustomBundles { get; set; } = [];
    public List<ReplacementMapping> AppliedMappings { get; set; } = [];
}

public sealed class PendingPatchTransaction
{
    public string PreviousSha256 { get; set; } = "";
    public string NextSha256 { get; set; } = "";
    public string BundleName { get; set; } = "";
    public PatchState PreviousState { get; set; } = new();
}

public sealed class PatchRequest
{
    public required IReadOnlyList<ReplacementMapping> Mappings { get; init; }
    public string ProfileName { get; init; } = "";
    /// <summary>Must be set by the UI after the user confirmed the change. The engine refuses otherwise.</summary>
    public bool UserConfirmed { get; init; }
    public bool AnalyzeDependencies { get; init; } = true;
    public OodleCompressor Compressor { get; init; } = OodleCompressor.Mermaid;
    public OodleCompressionLevel Level { get; init; } = OodleCompressionLevel.Normal;
}

public sealed record PatchResult(string BundleName, int ReplacedFiles, string IndexSha256, ValidationReport Report);
public sealed record RestoreResult(bool IndexRestored, int BundlesDeleted, string Message);

public class PatchException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed class ValidationFailedException(ValidationReport report)
    : PatchException("VALIDATION", "Validation failed: " + string.Join("; ", report.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message).Take(5)))
{
    public ValidationReport Report { get; } = report;
}

/// <summary>
/// Applies content swaps to a Steam/Epic Bundles2 install as one transaction:
/// baseline backup → stage new bundle + index next to the originals → verify hashes → atomic swap → state.
/// Only _.index.bin is ever replaced; replacement data goes into new files under Bundles2/PoeMtxChanger/.
/// Every apply is built from the clean baseline, so profiles never stack.
/// </summary>
public sealed class PatchEngine : IGamePatchEngine
{
    public const string CustomBundlePrefix = "PoeMtxChanger/";
    private static readonly string[] ForeignBundlePrefixes = ["LibGGPK3/"];

    private readonly DirectoryBundleStorage _storage;
    private readonly IOodleCodec _codec;
    private readonly ILogger _log;
    private readonly Func<bool> _isGameRunning;
    private readonly ResourcePolicy _policy;

    public string BackupDirectory { get; }
    public string StatePath => Path.Combine(BackupDirectory, "state.json");
    public string BaselinePath => Path.Combine(BackupDirectory, "baseline_index.bin");
    public string JournalPath => Path.Combine(BackupDirectory, "pending_transaction.json");
    public string PreviousIndexPath => Path.Combine(BackupDirectory, "previous_index.bin");

    public PatchEngine(DirectoryBundleStorage storage, IOodleCodec codec, string backupRoot, ILogger? logger = null,
        Func<bool>? isGameRunning = null, ResourcePolicy? policy = null)
    {
        _storage = storage;
        _codec = codec;
        _log = logger ?? NullLogger.Instance;
        _isGameRunning = isGameRunning ?? (() => GameProcessGuard.IsGameRunning);
        _policy = policy ?? ResourcePolicy.Default;
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(storage.Bundles2Directory.ToLowerInvariant())))[..12];
        BackupDirectory = Path.Combine(backupRoot, key);
    }

    public PatchState? LoadState() => FileUtil.ReadJson<PatchState>(StatePath);
    public byte[] ReadCleanIndex()
    {
        var state = LoadState() ?? throw new PatchException("BASELINE", "No baseline available.");
        var bytes = File.ReadAllBytes(BaselinePath);
        if (FileUtil.Sha256(bytes) != state.BaselineSha256) throw new PatchException("BASELINE", "Baseline index hash mismatch.");
        return bytes;
    }
    private void SaveState(PatchState s) { Directory.CreateDirectory(BackupDirectory); FileUtil.WriteJsonAtomic(StatePath, s); }

    public GameFileStatus GetStatus()
    {
        if (File.Exists(JournalPath)) return GameFileStatus.RecoveryRequired;
        var state = LoadState();
        if (state is null || string.IsNullOrEmpty(state.BaselineSha256)) return GameFileStatus.NoBaseline;
        if (!File.Exists(BaselinePath)) return GameFileStatus.BaselineDamaged;
        var current = FileUtil.Sha256OfFile(_storage.IndexPath);
        if (current == state.AppliedSha256) return GameFileStatus.PatchedByUs;
        if (current == state.BaselineSha256) return GameFileStatus.Clean;
        return GameFileStatus.ChangedExternally;
    }

    private void Preflight(bool confirmed)
    {
        if (!confirmed) throw new PatchException("CONFIRM", "The change was not confirmed by the user.");
        if (_isGameRunning()) throw new PatchException("GAME_RUNNING", "Close Path of Exile before modifying or restoring files.");
        if (!_codec.IsAvailable) throw new PatchException("NO_OODLE", _codec.Description);
    }

    public Task<PatchResult> ApplyAsync(PatchRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Apply(request, progress, ct), ct);

    public PatchResult Apply(PatchRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Preflight(request.UserConfirmed);
        using var lease = AcquireOperationLock();
        RecoverInterruptedTransaction();
        var state = EnsureBaseline(progress);
        var status = GetStatus();
        if (status is GameFileStatus.ChangedExternally)
            throw new PatchException("CHANGED", "Game files changed since the backup was taken (game update or Steam verification). Adopt the current files as the new baseline first.");
        if (status is GameFileStatus.BaselineDamaged)
            throw new PatchException("BASELINE", "The baseline backup is missing. Verify the game files in Steam, then create a new baseline.");
        var previousBytes = _storage.ReadIndexBytes();
        var previousSha = FileUtil.Sha256(previousBytes);
        var expectedCurrentSha = status == GameFileStatus.PatchedByUs ? state.AppliedSha256 : state.BaselineSha256;
        if (previousSha != expectedCurrentSha) throw new PatchException("CHANGED", "Game index changed while preparing the operation.");
        var previousState = LoadState()!;

        progress?.Report("Loading clean baseline index…");
        var baseBytes = File.ReadAllBytes(BaselinePath);
        if (FileUtil.Sha256(baseBytes) != state.BaselineSha256)
            throw new PatchException("BASELINE", "Baseline backup failed its hash check; refusing to continue.");

        using var archive = GameArchive.Open(_storage, _codec, baseBytes);
        ct.ThrowIfCancellationRequested();

        progress?.Report("Validating mappings…");
        var mappings = request.Mappings.Select(m => m.Normalized()).ToList();
        var report = new MappingValidator(_policy).Validate(mappings, archive, request.AnalyzeDependencies);
        if (report.HasErrors) throw new ValidationFailedException(report);

        progress?.Report("Building replacement bundle…");
        var newIndex = archive.Index.DeepClone();
        var bundleName = $"{CustomBundlePrefix}{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..(CustomBundlePrefix.Length + 23)];
        var bundleIndex = newIndex.AddBundle(bundleName, 0);
        using var payload = new MemoryStream();
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var placedSources = new Dictionary<string, (int Offset, int Size)>(StringComparer.OrdinalIgnoreCase);
        var visualMappings = mappings.Where(m => m.VisualIdentityEdit != null).ToList();
        var editedVisualTable = visualMappings.Count > 0 ? VisualIdentityEditor.Apply(archive.Read(VisualIdentityEditor.TablePath), visualMappings) : null;
        var physicalMappings = mappings.DistinctBy(m => m.TargetPath, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var m in physicalMappings)
        {
            ct.ThrowIfCancellationRequested();
            var payloadKey = m.VisualIdentityEdit == null ? "source:" + m.SourcePath : "visual:" + m.TargetPath;
            if (!placedSources.TryGetValue(payloadKey, out var place))
            {
                var content = m.VisualIdentityEdit == null ? archive.Read(m.SourcePath) : editedVisualTable!;
                place = (checked((int)payload.Position), content.Length);
                payload.Write(content);
                placedSources[payloadKey] = place;
            }
            expected[m.TargetPath] = m.VisualIdentityEdit == null ? FileUtil.Sha256(archive.Read(m.SourcePath)) : FileUtil.Sha256(editedVisualTable!);
            if (!newIndex.TryGetFile(m.TargetPath, out var rec))
                throw new PatchException("TARGET_MISSING", $"Target vanished from index: {m.TargetPath}");
            rec.BundleIndex = bundleIndex;
            rec.Offset = place.Offset;
            rec.Size = place.Size;
        }
        newIndex.Bundles[bundleIndex].UncompressedSize = checked((int)payload.Length);

        var bundleBytes = BundleFile.Compress(payload.GetBuffer().AsSpan(0, (int)payload.Length), _codec, request.Compressor, request.Level);
        var indexBytes = BundleFile.Compress(newIndex.Serialize(), _codec, request.Compressor, request.Level);
        var indexSha = FileUtil.Sha256(indexBytes);

        var bundlePath = _storage.GetBundlePath(bundleName);
        var stagedBundle = bundlePath + ".pmc-staged";
        var stagedIndex = _storage.IndexPath + ".pmc-staged";
        bool indexSwapped = false;
        try
        {
            progress?.Report("Staging files…");
            Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!);
            WriteFlushed(stagedBundle, bundleBytes);
            WriteFlushed(stagedIndex, indexBytes);

            progress?.Report("Verifying staged files…");
            VerifyStaged(stagedIndex, stagedBundle, bundleIndex, mappings, expected, archive.Index.Files.Count);
            ct.ThrowIfCancellationRequested();

            progress?.Report("Committing…");
            if (_isGameRunning()) throw new PatchException("GAME_RUNNING", "Path of Exile was started during the operation.");
            if (FileUtil.Sha256OfFile(_storage.IndexPath) != previousSha)
                throw new PatchException("CHANGED", "Game index changed during staging; no changes were committed.");
            FileUtil.WriteAllBytesAtomic(PreviousIndexPath, previousBytes);
            FileUtil.WriteJsonAtomic(JournalPath, new PendingPatchTransaction
            {
                PreviousSha256 = previousSha, NextSha256 = indexSha, BundleName = bundleName, PreviousState = previousState,
            });
            File.Move(stagedBundle, bundlePath, overwrite: false);
            File.Replace(stagedIndex, _storage.IndexPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            indexSwapped = true;
            if (FileUtil.Sha256OfFile(_storage.IndexPath) != indexSha)
                throw new PatchException("VERIFY", "Index on disk does not match the staged index after the swap.");

            var oldBundles = state.CustomBundles.ToList();
            state.AppliedSha256 = indexSha;
            state.AppliedUtc = DateTime.UtcNow;
            state.AppliedProfileName = request.ProfileName;
            state.CustomBundles = [bundleName];
            state.AppliedMappings = mappings;
            progress?.Report("Saving transaction state…");
            SaveState(state);

            foreach (var old in oldBundles) TryDeleteBundle(old);
            ClearTransactionJournal();
            _log.LogInformation("Applied {Count} replacements into {Bundle}", mappings.Count, bundleName);
            return new PatchResult(bundleName, physicalMappings.Count, indexSha, report);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apply failed; rolling back");
            TryDelete(stagedBundle);
            TryDelete(stagedIndex);
            if (indexSwapped)
            {
                try
                {
                    if (FileUtil.Sha256OfFile(_storage.IndexPath) != indexSha)
                        throw new IOException("Index changed externally after commit; automatic rollback will not overwrite it.");
                    RestoreIndexBytes(previousBytes, previousSha, indexSha);
                    SaveState(previousState);
                }
                catch (Exception rex)
                {
                    _log.LogCritical(rex, "Rollback failed; transaction journal and bundle retained for recovery");
                    throw new PatchException("RECOVERY_REQUIRED", "Rollback could not finish. The recovery journal and replacement bundle were preserved. Close the game and retry Restore. " + rex.Message, rex);
                }
            }
            TryDelete(bundlePath);
            ClearTransactionJournal();
            throw ex is PatchException ? ex : new PatchException("APPLY", "Apply failed and was rolled back: " + ex.Message, ex);
        }
    }

    private void VerifyStaged(string stagedIndex, string stagedBundle, int bundleIndex, List<ReplacementMapping> mappings,
        Dictionary<string, string> expected, int originalFileCount)
    {
        var raw = File.ReadAllBytes(stagedIndex);
        var idx = BundleIndex.Parse(BundleFile.Decompress(raw, _codec));
        if (idx.Files.Count != originalFileCount) throw new PatchException("VERIFY", "Staged index has a different file count.");
        using var fs = File.OpenRead(stagedBundle);
        var header = BundleHeader.Read(fs);
        foreach (var m in mappings)
        {
            if (!idx.TryGetFile(m.TargetPath, out var rec) || rec.BundleIndex != bundleIndex)
                throw new PatchException("VERIFY", $"Staged index does not redirect {m.TargetPath}.");
            var data = BundleFile.ReadRange(fs, header, rec.Offset, rec.Size, _codec);
            if (FileUtil.Sha256(data) != expected[m.TargetPath])
                throw new PatchException("VERIFY", $"Hash mismatch for {m.TargetPath} in the staged bundle.");
        }
    }

    /// <summary>Creates the baseline backup on first use. Refuses if the index already contains bundles from other modding tools.</summary>
    private PatchState EnsureBaseline(IProgress<string>? progress)
    {
        var state = LoadState();
        if (state is not null && !string.IsNullOrEmpty(state.BaselineSha256)) return state;

        progress?.Report("Creating baseline backup of _.index.bin…");
        var bytes = _storage.ReadIndexBytes();
        CheckNotForeign(bytes);
        Directory.CreateDirectory(BackupDirectory);
        FileUtil.WriteAllBytesAtomic(BaselinePath, bytes);
        var sha = FileUtil.Sha256(bytes);
        if (FileUtil.Sha256OfFile(BaselinePath) != sha) throw new PatchException("BACKUP", "Backup verification failed.");
        state = new PatchState
        {
            GameRoot = Path.GetDirectoryName(_storage.Bundles2Directory) ?? "",
            BaselineSha256 = sha,
            BaselineSize = bytes.Length,
            BaselineCreatedUtc = DateTime.UtcNow,
        };
        SaveState(state);
        _log.LogInformation("Baseline backup created ({Size} bytes, sha256 {Sha})", bytes.Length, sha);
        return state;
    }

    private void CheckNotForeign(byte[] indexBytes)
    {
        var idx = BundleIndex.Parse(BundleFile.Decompress(indexBytes, _codec));
        var foreign = idx.Bundles.FirstOrDefault(b =>
            ForeignBundlePrefixes.Any(p => b.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || b.Name.StartsWith(CustomBundlePrefix, StringComparison.OrdinalIgnoreCase));
        if (foreign != null)
            throw new PatchException("FOREIGN", $"The index already references a modded bundle ({foreign.Name}). Verify game files in Steam to get clean files first.");
    }

    public Task<RestoreResult> RestoreAsync(bool userConfirmed, CancellationToken ct = default)
        => Task.Run(() => Restore(userConfirmed), ct);

    public RestoreResult Restore(bool userConfirmed)
    {
        if (!userConfirmed) throw new PatchException("CONFIRM", "The restore was not confirmed by the user.");
        if (_isGameRunning()) throw new PatchException("GAME_RUNNING", "Close Path of Exile before restoring files.");
        using var lease = AcquireOperationLock();
        RecoverInterruptedTransaction();
        var state = LoadState();
        var status = GetStatus();
        switch (status)
        {
            case GameFileStatus.NoBaseline:
                return new RestoreResult(false, 0, "Nothing to restore: this tool has not modified this installation.");
            case GameFileStatus.BaselineDamaged:
                return new RestoreResult(false, 0, "Baseline backup missing. Use Steam's \"Verify integrity of game files\".");
            case GameFileStatus.Clean:
            {
                int n = state!.CustomBundles.Count(TryDeleteBundle);
                ClearApplied(state);
                return new RestoreResult(false, n, "Index already original; leftover bundles cleaned up.");
            }
            case GameFileStatus.PatchedByUs:
            {
                RestoreIndexFromBaseline(state!);
                int n = state!.CustomBundles.Count(TryDeleteBundle);
                ClearApplied(state);
                return new RestoreResult(true, n, "Original _.index.bin restored and verified.");
            }
            default:
            {
                if (!_codec.IsAvailable)
                    return new RestoreResult(false, 0, "Game files changed externally; the current index was left untouched. Configure Oodle to clean up leftover bundles.");
                var idx = BundleIndex.Parse(BundleFile.Decompress(_storage.ReadIndexBytes(), _codec));
                var referenced = idx.Bundles.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (state!.CustomBundles.Any(referenced.Contains))
                    return new RestoreResult(false, 0, "The index changed externally but still references this tool's bundles. Use Steam's \"Verify integrity of game files\".");
                int n = state.CustomBundles.Count(TryDeleteBundle);
                ClearApplied(state);
                return new RestoreResult(false, n, "Game files were replaced externally (update/verify); leftover bundles removed. The old baseline is stale — adopt a new one before the next apply.");
            }
        }
    }

    /// <summary>After a game update: discard the stale baseline and back up the current (clean) index instead.</summary>
    public void AdoptCurrentAsBaseline(bool userConfirmed)
    {
        Preflight(userConfirmed);
        using var lease = AcquireOperationLock();
        RecoverInterruptedTransaction();
        var bytes = _storage.ReadIndexBytes();
        CheckNotForeign(bytes);
        var old = LoadState();
        if (old != null) foreach (var b in old.CustomBundles) TryDeleteBundle(b);
        TryDelete(BaselinePath);
        TryDelete(StatePath);
        EnsureBaseline(null);
    }

    private void RestoreIndexFromBaseline(PatchState state)
    {
        var bytes = File.ReadAllBytes(BaselinePath);
        if (FileUtil.Sha256(bytes) != state.BaselineSha256) throw new PatchException("BASELINE", "Baseline backup failed its hash check.");
        RestoreIndexBytes(bytes, state.BaselineSha256, state.AppliedSha256);
        _log.LogInformation("Original index restored from baseline");
    }

    private void RestoreIndexBytes(byte[] bytes, string expectedSha, string? expectedCurrentSha = null)
    {
        var staged = _storage.IndexPath + ".pmc-restore";
        try
        {
            WriteFlushed(staged, bytes);
            if (_isGameRunning()) throw new PatchException("GAME_RUNNING", "The game started during restore; restore was not committed.");
            if (expectedCurrentSha != null && FileUtil.Sha256OfFile(_storage.IndexPath) != expectedCurrentSha)
                throw new PatchException("CHANGED", "The index changed during restore; it was left untouched.");
            File.Replace(staged, _storage.IndexPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally { TryDelete(staged); }
        if (FileUtil.Sha256OfFile(_storage.IndexPath) != expectedSha)
            throw new PatchException("VERIFY", "Restored index does not match the baseline hash.");
    }

    private FileStream AcquireOperationLock()
    {
        Directory.CreateDirectory(BackupDirectory);
        try { return new FileStream(Path.Combine(BackupDirectory, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new PatchException("BUSY", "Another apply/restore operation is already running for this installation.", ex); }
    }

    private void ClearTransactionJournal()
    {
        // Remove the journal first: its rollback file must exist whenever the journal exists.
        TryDelete(JournalPath);
        if (!File.Exists(JournalPath)) TryDelete(PreviousIndexPath);
    }

    private void RecoverInterruptedTransaction()
    {
        if (!File.Exists(JournalPath)) return;
        PendingPatchTransaction pending;
        try { pending = FileUtil.ReadJson<PendingPatchTransaction>(JournalPath) ?? throw new IOException("Empty transaction journal."); }
        catch (Exception ex) { throw new PatchException("RECOVERY_REQUIRED", "Recovery journal could not be read; no game files were changed.", ex); }
        if (!pending.BundleName.StartsWith(CustomBundlePrefix, StringComparison.OrdinalIgnoreCase))
            throw new PatchException("RECOVERY_REQUIRED", "Invalid bundle name in recovery journal.");
        var current = FileUtil.Sha256OfFile(_storage.IndexPath);
        if (current == pending.NextSha256 && LoadState()?.AppliedSha256 == current)
        {
            foreach (var old in pending.PreviousState.CustomBundles.Where(b => b != pending.BundleName)) TryDeleteBundle(old);
            ClearTransactionJournal();
            return;
        }
        if (current != pending.PreviousSha256 && current != pending.NextSha256)
        {
            // A completed game update can make the pending bundle irrelevant. Never roll that update back.
            if (_codec.IsAvailable)
            {
                var index = BundleIndex.Parse(BundleFile.Decompress(_storage.ReadIndexBytes(), _codec));
                if (!index.Bundles.Any(b => b.Name.Equals(pending.BundleName, StringComparison.OrdinalIgnoreCase)))
                {
                    TryDeleteBundle(pending.BundleName);
                    ClearTransactionJournal();
                    return;
                }
            }
            throw new PatchException("RECOVERY_REQUIRED", "Game index changed outside the transaction; it was left untouched. Verify game files, then retry Restore.");
        }
        if (current == pending.NextSha256)
        {
            if (!File.Exists(PreviousIndexPath)) throw new PatchException("RECOVERY_REQUIRED", "Recovery index is missing.");
            var previous = File.ReadAllBytes(PreviousIndexPath);
            if (FileUtil.Sha256(previous) != pending.PreviousSha256) throw new PatchException("RECOVERY_REQUIRED", "Recovery index hash mismatch.");
            RestoreIndexBytes(previous, pending.PreviousSha256, pending.NextSha256);
        }
        SaveState(pending.PreviousState);
        TryDeleteBundle(pending.BundleName);
        ClearTransactionJournal();
        _log.LogWarning("Recovered interrupted transaction to the previous profile");
    }

    private void ClearApplied(PatchState state)
    {
        state.AppliedSha256 = null;
        state.AppliedUtc = null;
        state.AppliedProfileName = null;
        state.CustomBundles = [];
        state.AppliedMappings = [];
        SaveState(state);
    }

    private bool TryDeleteBundle(string name)
    {
        if (!name.StartsWith(CustomBundlePrefix, StringComparison.OrdinalIgnoreCase)) return false; // never delete game bundles
        return TryDelete(_storage.GetBundlePath(name));
    }

    private bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (IOException ex) { _log.LogWarning(ex, "Could not delete {Path}", path); return false; }
        catch (UnauthorizedAccessException ex) { _log.LogWarning(ex, "Could not delete {Path}", path); return false; }
    }

    private static void WriteFlushed(string path, byte[] data)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(data);
        fs.Flush(flushToDisk: true);
    }
}
