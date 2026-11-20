using System.Security.Cryptography;
using System.Text;
using PoeMtxChanger.Core.Installation;
using PoeMtxChanger.Core.Util;

namespace PoeMtxChanger.Core.Patching;

public sealed class FilePatchState
{
    public string TargetPath { get; set; } = "";
    public string BaselineSha256 { get; set; } = "";
    public long BaselineSize { get; set; }
    public DateTime BaselineCreatedUtc { get; set; }
    public string? AppliedSha256 { get; set; }
    public DateTime? AppliedUtc { get; set; }
    public string? ProfileName { get; set; }
}

internal sealed class PendingFileCommit
{
    public string PreviousSha256 { get; set; } = "";
    public string NextSha256 { get; set; } = "";
    public FilePatchState PreviousState { get; set; } = new();
    public FilePatchState NextState { get; set; } = new();
}

/// <summary>Full-file staging for standalone GGPK and loose audio banks. Never modifies a live file before verified commit.</summary>
public sealed class AtomicFilePatch
{
    public string TargetPath { get; }
    public string BackupDirectory { get; }
    public string BaselinePath => Path.Combine(BackupDirectory, "baseline.bin");
    public string StatePath => Path.Combine(BackupDirectory, "file-state.json");
    public string JournalPath => Path.Combine(BackupDirectory, "file-pending.json");
    private string PreviousPath => TargetPath + ".pmc-previous";
    private readonly Func<bool> _running;

    public AtomicFilePatch(string targetPath, string backupRoot, Func<bool>? gameRunning = null)
    {
        TargetPath = Path.GetFullPath(targetPath);
        string key = FileUtil.Sha256(Encoding.UTF8.GetBytes(TargetPath.ToLowerInvariant()))[..16];
        BackupDirectory = Path.Combine(backupRoot, "files", key);
        _running = gameRunning ?? (() => GameProcessGuard.IsGameRunning);
    }

    public FilePatchState? LoadState()
    {
        var state = FileUtil.ReadJson<FilePatchState>(StatePath);
        if (state != null && !state.TargetPath.Equals(TargetPath, StringComparison.OrdinalIgnoreCase)) throw new PatchException("STATE", "Backup target path mismatch.");
        return state;
    }

    public GameFileStatus GetStatus()
    {
        if (File.Exists(JournalPath)) return GameFileStatus.RecoveryRequired;
        var state = LoadState();
        if (state is null) return GameFileStatus.NoBaseline;
        if (!File.Exists(BaselinePath)) return GameFileStatus.BaselineDamaged;
        if (!File.Exists(TargetPath)) return GameFileStatus.ChangedExternally;
        string current = FileUtil.Sha256OfFile(TargetPath);
        return current == state.AppliedSha256 ? GameFileStatus.PatchedByUs
            : current == state.BaselineSha256 ? GameFileStatus.Clean : GameFileStatus.ChangedExternally;
    }

    public FilePatchState Apply(bool confirmed, string profileName, Action<string, string, CancellationToken> build,
        Action<string, CancellationToken> validate, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Preflight(confirmed);
        using var lease = Lock(); Recover();
        var state = EnsureBaseline(ct);
        var status = GetStatus();
        if (status is not (GameFileStatus.Clean or GameFileStatus.PatchedByUs)) throw new PatchException("CHANGED", "The file changed or its backup is missing. Restore/verify the installation before applying.");
        if(status==GameFileStatus.Clean){state.AppliedSha256=null;state.AppliedUtc=null;state.ProfileName=null;}
        if (FileUtil.Sha256OfFile(BaselinePath) != state.BaselineSha256) throw new PatchException("BASELINE", "Backup hash mismatch.");
        string stage = TargetPath + ".pmc-stage-" + Guid.NewGuid().ToString("N");
        try
        {
            progress?.Report("Building a separate file…");
            build(BaselinePath, stage, ct); ct.ThrowIfCancellationRequested();
            validate(stage, ct); ct.ThrowIfCancellationRequested();
            var next = new FilePatchState
            {
                TargetPath = TargetPath, BaselineSha256 = state.BaselineSha256, BaselineSize = state.BaselineSize,
                BaselineCreatedUtc = state.BaselineCreatedUtc, AppliedSha256 = FileUtil.Sha256OfFile(stage),
                AppliedUtc = DateTime.UtcNow, ProfileName = profileName,
            };
            Commit(stage, state, next, progress);
            return next;
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    public bool Restore(bool confirmed, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Preflight(confirmed); using var lease = Lock(); Recover();
        var state = LoadState();
        if (state is null) return false;
        var status = GetStatus();
        if (status == GameFileStatus.Clean) return false;
        if (status != GameFileStatus.PatchedByUs) throw new PatchException("CHANGED", "The current file is not the one this tool wrote; it was left untouched.");
        if (FileUtil.Sha256OfFile(BaselinePath) != state.BaselineSha256) throw new PatchException("BASELINE", "Backup hash mismatch.");
        string stage = TargetPath + ".pmc-stage-" + Guid.NewGuid().ToString("N");
        try
        {
            CopyFlushed(BaselinePath, stage, ct);
            Commit(stage, state, new FilePatchState { TargetPath = TargetPath, BaselineSha256 = state.BaselineSha256,
                BaselineSize = state.BaselineSize, BaselineCreatedUtc = state.BaselineCreatedUtc }, progress);
            return true;
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    public void Adopt(bool confirmed)
    {
        Preflight(confirmed); using var lease = Lock(); Recover();
        if (GetStatus() == GameFileStatus.PatchedByUs) throw new PatchException("PATCHED", "Restore the active modification before adopting a baseline.");
        string stage=BaselinePath+".adopt-"+Guid.NewGuid().ToString("N"),previous=stage+".previous";
        bool installed=false,hadBaseline=File.Exists(BaselinePath);
        try
        {
            string sha=FileUtil.Sha256OfFile(TargetPath);CopyFlushed(TargetPath,stage,default);
            if(FileUtil.Sha256OfFile(stage)!=sha||FileUtil.Sha256OfFile(TargetPath)!=sha)throw new PatchException("BACKUP","File changed while adopting baseline.");
            if(_running())throw new PatchException("GAME_RUNNING","Close the game before adopting a baseline.");
            if(hadBaseline)File.Replace(stage,BaselinePath,previous);else File.Move(stage,BaselinePath);installed=true;
            FileUtil.WriteJsonAtomic(StatePath,new FilePatchState{TargetPath=TargetPath,BaselineSha256=sha,BaselineSize=new FileInfo(TargetPath).Length,BaselineCreatedUtc=DateTime.UtcNow});
        }
        catch
        {
            if(installed){if(hadBaseline)File.Replace(previous,BaselinePath,null);else File.Delete(BaselinePath);}
            throw;
        }
        finally{if(File.Exists(stage))File.Delete(stage);}
        if(File.Exists(previous))File.Delete(previous);
    }

    private void Commit(string stage, FilePatchState before, FilePatchState after, IProgress<string>? progress)
    {
        string previousSha = before.AppliedSha256 ?? before.BaselineSha256;
        string nextSha = after.AppliedSha256 ?? after.BaselineSha256;
        if (_running()) throw new PatchException("GAME_RUNNING", "Close Path of Exile before committing changes.");
        if (File.Exists(PreviousPath)) throw new PatchException("RECOVERY_REQUIRED", "An earlier recovery file exists and was preserved.");
        if (FileUtil.Sha256OfFile(TargetPath) != previousSha) throw new PatchException("CHANGED", "File changed during staging; no changes were committed.");
        FileUtil.WriteJsonAtomic(JournalPath, new PendingFileCommit { PreviousSha256 = previousSha, NextSha256 = nextSha, PreviousState = before, NextState = after });
        bool swapped = false;
        try
        {
            File.Replace(stage, TargetPath, PreviousPath); swapped = true;
            if (FileUtil.Sha256OfFile(TargetPath) != nextSha || FileUtil.Sha256OfFile(PreviousPath) != previousSha) throw new PatchException("VERIFY", "Committed/recovery file hash mismatch.");
            progress?.Report("Saving the file state…");
            FileUtil.WriteJsonAtomic(StatePath, after);
            File.Delete(PreviousPath); File.Delete(JournalPath);
        }
        catch (Exception ex)
        {
            if (swapped)
            {
                try
                {
                    if (_running() || FileUtil.Sha256OfFile(TargetPath) != nextSha || FileUtil.Sha256OfFile(PreviousPath) != previousSha)
                        throw new IOException("Recovery hash/state changed.");
                    File.Replace(PreviousPath, TargetPath, null); FileUtil.WriteJsonAtomic(StatePath, before); File.Delete(JournalPath);
                }
                catch (Exception recovery) { throw new PatchException("RECOVERY_REQUIRED", "Commit failed; recovery data was preserved. " + recovery.Message, recovery); }
            }
            else if (File.Exists(JournalPath)) File.Delete(JournalPath);
            throw new PatchException("APPLY", "File change failed and was rolled back: " + ex.Message, ex);
        }
    }

    private void Recover()
    {
        if (!File.Exists(JournalPath)) return;
        if(_running())throw new PatchException("GAME_RUNNING","Close the game before recovering a file.");
        var pending = FileUtil.ReadJson<PendingFileCommit>(JournalPath) ?? throw new PatchException("RECOVERY_REQUIRED", "Invalid recovery journal.");
        if(!pending.PreviousState.TargetPath.Equals(TargetPath,StringComparison.OrdinalIgnoreCase)||!pending.NextState.TargetPath.Equals(TargetPath,StringComparison.OrdinalIgnoreCase))
            throw new PatchException("RECOVERY_REQUIRED","Recovery target identity mismatch.");
        string current = File.Exists(TargetPath) ? FileUtil.Sha256OfFile(TargetPath) : "";
        var state = LoadState();
        if (current == pending.NextSha256 && (state?.AppliedSha256 ?? state?.BaselineSha256) == current)
        { if (File.Exists(PreviousPath)) File.Delete(PreviousPath); File.Delete(JournalPath); return; }
        if (current == pending.NextSha256 || current == "")
        {
            if (!File.Exists(PreviousPath) || FileUtil.Sha256OfFile(PreviousPath) != pending.PreviousSha256)
                throw new PatchException("RECOVERY_REQUIRED", "Previous file is missing/corrupt; current file left untouched.");
            if (current == "") File.Move(PreviousPath, TargetPath); else File.Replace(PreviousPath, TargetPath, null);
        }
        else if (current != pending.PreviousSha256) throw new PatchException("RECOVERY_REQUIRED", "File changed externally; recovery will not overwrite it.");
        FileUtil.WriteJsonAtomic(StatePath, pending.PreviousState);
        if (File.Exists(PreviousPath)) File.Delete(PreviousPath);
        File.Delete(JournalPath);
    }

    private FilePatchState EnsureBaseline(CancellationToken ct)
    {
        if (LoadState() is { } existing) return existing;
        string sha = FileUtil.Sha256OfFile(TargetPath);
        if (!File.Exists(BaselinePath))
        {
            var temporary = BaselinePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try { CopyFlushed(TargetPath, temporary, ct); File.Move(temporary, BaselinePath); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (FileUtil.Sha256OfFile(BaselinePath) != sha || FileUtil.Sha256OfFile(TargetPath) != sha) throw new PatchException("BACKUP", "Source changed during backup or an inconsistent backup exists.");
        var state = new FilePatchState { TargetPath = TargetPath, BaselineSha256 = sha, BaselineSize = new FileInfo(TargetPath).Length, BaselineCreatedUtc = DateTime.UtcNow };
        FileUtil.WriteJsonAtomic(StatePath, state); return state;
    }
    private void Preflight(bool confirmed)
    { if (!confirmed) throw new PatchException("CONFIRM", "Change was not confirmed."); if (_running()) throw new PatchException("GAME_RUNNING", "Close Path of Exile before changing files."); }
    private FileStream Lock()
    {
        Directory.CreateDirectory(BackupDirectory);
        try { return new FileStream(Path.Combine(BackupDirectory, "file-operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new PatchException("BUSY", "Another operation is running for this file.", ex); }
    }
    public static void CopyFlushed(string source, string target, CancellationToken ct)
    {
        using var input = File.OpenRead(source); using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        byte[] buffer = new byte[1 << 20]; int read;
        while ((read = input.Read(buffer)) != 0) { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
        output.Flush(true);
    }
}
