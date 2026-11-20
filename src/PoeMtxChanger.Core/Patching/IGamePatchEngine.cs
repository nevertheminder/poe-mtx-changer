namespace PoeMtxChanger.Core.Patching;

public interface IGamePatchEngine
{
    GameFileStatus GetStatus();
    PatchState? LoadState();
    byte[] ReadCleanIndex();
    Task<PatchResult> ApplyAsync(PatchRequest request, IProgress<string>? progress = null, CancellationToken ct = default);
    Task<RestoreResult> RestoreAsync(bool userConfirmed, CancellationToken ct = default);
    void AdoptCurrentAsBaseline(bool userConfirmed);
}
