using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Logging;
using PoeMtxChanger.Core.Profiles;
using PoeMtxChanger.Core.Util;

namespace PoeMtxChanger.App.ViewModels;

public sealed partial class ProfilesViewModel(AppServices app, ProfileActions actions) : ObservableObject
{
    public ProfileSession Profiles => app.Profiles;

    [ObservableProperty] private CosmeticProfile? _selected = app.Profiles.Active;
    [ObservableProperty] private string _editName = app.Profiles.Active?.Name ?? "";
    [ObservableProperty] private string _editDescription = app.Profiles.Active?.Description ?? "";
    [ObservableProperty] private string? _message;

    public IEnumerable<string> SelectedSummary => Selected?.Entries.Select(e =>
        $"{(e.Enabled ? "●" : "○")} {e.Kind switch { ProfileEntryKind.EquipmentSlot => "Slot", ProfileEntryKind.SkillEffect => "Skill", _ => "Other" }} {e.Key}: {e.CosmeticName ?? "—"} ({e.Mappings.Count} replacements)") ?? [];

    public bool IsSelectedEmpty => Selected is null || Selected.Entries.Count == 0;

    partial void OnSelectedChanged(CosmeticProfile? value)
    {
        EditName = value?.Name ?? "";
        EditDescription = value?.Description ?? "";
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(IsSelectedEmpty));
    }

    [RelayCommand]
    private void New()
    {
        var p = new CosmeticProfile { Name = "New profile", GameBuild = app.Game.Installation?.SteamBuildId };
        Profiles.Manager.Save(p);
        Profiles.All.Add(p);
        Selected = p;
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is null) return;
        var json = System.Text.Json.JsonSerializer.Serialize(Selected, FileUtil.Json);
        var copy = ProfileManager.Parse(json);
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = Selected.Name + " (copy)";
        Profiles.Manager.Save(copy);
        Profiles.All.Add(copy);
        Selected = copy;
    }

    [RelayCommand]
    private void SaveEdits()
    {
        if (Selected is null) return;
        Selected.Name = string.IsNullOrWhiteSpace(EditName) ? "Untitled" : EditName.Trim();
        Selected.Description = EditDescription;
        Profiles.Manager.Save(Selected);
        var keep = Selected;
        Profiles.Reload();
        Selected = Profiles.All.FirstOrDefault(p => p.Id == keep.Id);
        Message = "Saved.";
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is null) return;
        if (!app.Dialogs.Confirm("Delete the profile?", $"Profile “{Selected.Name}” will be deleted. The game files are not affected.")) return;
        Profiles.Manager.Delete(Selected.Id);
        Profiles.Reload();
        Selected = Profiles.Active;
    }

    [RelayCommand]
    private void MakeActive()
    {
        if (Selected != null) Profiles.Active = Selected;
    }

    [RelayCommand]
    private void Export()
    {
        if (Selected is null) return;
        var path = app.Dialogs.SaveFile("Export profile", "Profile JSON|*.json", FileUtil.SafeFileName(Selected.Name) + ".json");
        if (path is null) return;
        Profiles.Manager.Export(Selected, path);
        Message = "Exported: " + path;
    }

    [RelayCommand]
    private void Import()
    {
        var path = app.Dialogs.PickFile("Import profile", "Profile JSON|*.json");
        if (path is null) return;
        try
        {
            var p = Profiles.Manager.Import(path);
            Profiles.Reload();
            Selected = Profiles.All.FirstOrDefault(x => x.Id == p.Id);
            Message = $"Imported “{p.Name}”. Paths were checked; replacements are checked again before applying.";
        }
        catch (ProfileImportException ex) { app.Dialogs.Error("Import failed", ex.Message); }
    }

    [RelayCommand]
    private async Task Apply()
    {
        MakeActive();
        await actions.ApplyActiveAsync();
    }

    [RelayCommand] private Task Restore() => actions.RestoreAsync();
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _app;

    public ObservableCollection<LogEntry> Log { get; } = [];
    public IReadOnlyList<OodleCompressor> Compressors { get; } = [OodleCompressor.Mermaid, OodleCompressor.Kraken, OodleCompressor.Leviathan];

    [ObservableProperty] private string _gamePath;
    [ObservableProperty] private string _backupDirectory;
    [ObservableProperty] private string _oodlePath;
    [ObservableProperty] private string _schemaPath;
    [ObservableProperty] private bool _detailedLogging;
    [ObservableProperty] private bool _validateResources;
    [ObservableProperty] private bool _checkCompatibilityOnStartup;
    [ObservableProperty] private bool _showMockData;
    [ObservableProperty] private OodleCompressor _compressor;
    [ObservableProperty] private string? _message;

    public GameSession Game => _app.Game;

    public SettingsViewModel(AppServices app)
    {
        _app = app;
        var s = app.Settings;
        _gamePath = s.GameRootPath ?? "";
        _backupDirectory = s.BackupDirectory;
        _oodlePath = s.OodleLibraryPath ?? "";
        // The library may be found automatically after this page was created: show the path it was saved under.
        app.Game.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GameSession.Codec) && app.Game.Codec.IsAvailable && !string.IsNullOrEmpty(app.Settings.OodleLibraryPath))
                OodlePath = app.Settings.OodleLibraryPath;
        };
        _schemaPath = s.DatSchemaPath ?? "";
        _detailedLogging = s.DetailedLogging;
        _validateResources = s.ValidateResources;
        _checkCompatibilityOnStartup = s.CheckCompatibilityOnStartup;
        _showMockData = s.ShowMockData;
        _compressor = s.Compressor;
        foreach (var e in app.LogProvider.Recent.TakeLast(200)) Log.Add(e);
        app.LogProvider.EntryWritten += e => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Log.Add(e);
            while (Log.Count > 300) Log.RemoveAt(0);
        });
    }

    [RelayCommand]
    private void BrowseGame()
    {
        var p = _app.Dialogs.PickFolder("Path of Exile folder", GamePath);
        if (p != null) GamePath = p;
    }

    [RelayCommand]
    private void BrowseBackup()
    {
        var p = _app.Dialogs.PickFolder("Backup folder", BackupDirectory);
        if (p != null) BackupDirectory = p;
    }

    /// <summary>Looks for an Oodle library already on this computer and fills in its path (saved with “Save”).</summary>
    [RelayCommand]
    private async Task FindOodle()
    {
        Message = "Looking for the Oodle library…";
        var found = await Task.Run(() => OodleLocator.FindAndLoad(AppContext.BaseDirectory));
        if (found is { } f)
        {
            if (f.Codec is IDisposable d) d.Dispose();
            OodlePath = f.Path;
            Message = $"Found ({f.Source}). Click “Save” to use it.";
        }
        else Message = "No Oodle library was found on this computer. Choose oo2core_*_win64.dll with “Browse…”.";
    }

    [RelayCommand]
    private void OodleHelp() => Services.OodleHelp.Show();

    [RelayCommand]
    private void BrowseOodle()
    {
        var p = _app.Dialogs.PickFile("Oodle library", "Oodle core|oo2core*.dll|DLL|*.dll", OodlePath);
        if (p != null) OodlePath = p;
    }

    [RelayCommand]
    private void BrowseSchema()
    {
        var p = _app.Dialogs.PickFile("Table schema (dat-schema)", "dat-schema JSON|*.json", SchemaPath);
        if (p != null) SchemaPath = p;
    }

    [RelayCommand]
    private async Task Save()
    {
        var s = _app.Settings;
        bool oodleChanged = (s.OodleLibraryPath ?? "") != OodlePath;
        bool gameChanged = (s.GameRootPath ?? "") != GamePath || (s.DatSchemaPath ?? "") != SchemaPath;
        if (!string.IsNullOrWhiteSpace(SchemaPath))
        {
            try { Core.Formats.Dat.DatSchema.LoadFile(SchemaPath); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                _app.Dialogs.Error("The schema does not fit", ex.Message);
                return;
            }
        }
        s.DatSchemaPath = string.IsNullOrWhiteSpace(SchemaPath) ? null : SchemaPath;
        if (oodleChanged && !string.IsNullOrWhiteSpace(OodlePath) &&
            !_app.Dialogs.Confirm("Load the native library?",
                "Loading a DLL runs its code in this program. Only choose an oo2core_*_win64.dll from a trusted source that you are licensed to use. Continue?"))
            return;

        s.GameRootPath = string.IsNullOrWhiteSpace(GamePath) ? null : GamePath;
        s.BackupDirectory = string.IsNullOrWhiteSpace(BackupDirectory) ? AppPaths.DefaultBackupDirectory : BackupDirectory;
        s.OodleLibraryPath = string.IsNullOrWhiteSpace(OodlePath) ? null : OodlePath;
        s.DetailedLogging = DetailedLogging;
        s.ValidateResources = ValidateResources;
        s.CheckCompatibilityOnStartup = CheckCompatibilityOnStartup;
        s.ShowMockData = ShowMockData;
        s.Compressor = Compressor;
        _app.SaveSettings();
        _app.Log("Settings").LogInformation("Settings saved");
        Message = "Settings saved.";

        await _app.Game.RunBusy("Applying the settings…", async () =>
        {
            if (oodleChanged) _app.Game.ReloadCodec();
            if (gameChanged || oodleChanged) await _app.Game.DetectAsync();
            if ((gameChanged || oodleChanged) && _app.Game.Codec.IsAvailable) await _app.Game.LoadArchiveAsync();
            else if (ShowMockData && _app.Game.Catalog.Count == 0) _app.Game.LoadMockCatalog();
        });
        if (oodleChanged && !_app.Game.Codec.IsAvailable) Message = "Oodle not loaded: " + _app.Game.Codec.Description;
    }

    [RelayCommand]
    private async Task Detect()
    {
        GamePath = "";
        _app.Settings.GameRootPath = null;
        await _app.Game.RunBusy("Looking for the installation…", _app.Game.DetectAsync);
        GamePath = _app.Settings.GameRootPath ?? "";
        Message = _app.Game.Installation is null ? "Installation not found." : "Found: " + _app.Game.Installation.RootPath;
    }

    [RelayCommand]
    private void OpenLogs()
    {
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        Process.Start(new ProcessStartInfo(AppPaths.LogsDirectory) { UseShellExecute = true });
    }
}

/// <summary>"About": installation dashboard, compatibility and restore.</summary>
public sealed partial class AboutViewModel(AppServices app, ProfileActions actions) : ObservableObject
{
    public GameSession Game => app.Game;
    public ProfileSession Profiles => app.Profiles;

    public string InstallPath => Game.Installation?.RootPath ?? "Not found";
    public string InstallKind => Game.Installation is null ? "—" : $"{Game.Installation.Kind} · {(Game.Installation.UsesGgpk ? "Content.ggpk (full backup and rebuild)" : "Bundles2")} · found by: {Game.Installation.DetectedBy}";
    public string GameVersion => Game.Installation is null ? "—"
        : string.Join(" · ", new[] { Game.Installation.ExecutableVersion is { } v ? "exe " + v : null, Game.Installation.SteamBuildId is { } b ? "Steam build " + b : null }.OfType<string>()) is { Length: > 0 } s ? s : "Version cannot be determined";
    public string IndexInfo => Game.HeaderInfo is { } h
        ? $"{h.FileSize / 1048576.0:F1} MB · {h.Header.ChunkCount} chunks · compressor {h.Header.Compressor} · modified {h.LastWriteUtc.ToLocalTime():g}"
        : Game.HeaderError ?? "—";
    public string IndexHash => Game.HeaderInfo?.Sha256 is { Length: > 0 } s ? s[..16] + "…" : "—";
    public string FileStatusText => Ui.FileStatus(Game.FileStatus);
    public string OodleText => Game.Codec.IsAvailable ? "Loaded: " + Game.Codec.Description : "Not loaded: " + Game.Codec.Description;
    public string ArchiveText => Game.Archive is { } a
        ? $"Index read: {a.Index.Files.Count:N0} files, {a.Index.Bundles.Count:N0} bundles, unmatched paths: {a.UnresolvedPathCount}"
        : Game.CatalogIsMock ? "Demo data (MOCK) is shown" : Game.ArchiveError ?? "Index not loaded";
    public string AppliedText => Game.PatchState is { AppliedProfileName: { } n, AppliedUtc: { } t } ? $"Profile “{n}” applied {t.ToLocalTime():g}" : "Nothing applied";
    public bool CanAdopt => Game.FileStatus == Core.Patching.GameFileStatus.ChangedExternally;
    public string CatalogSourceText => "Catalog source: " + Game.CatalogSource;
    public IEnumerable<string> TableLines => Game.TableDiagnostics.Count == 0
        ? ["Tables were not read (a loaded index is required)."]
        : Game.TableDiagnostics.Select(d => $"{(d.Ok ? "✓" : "✗")} {d.Table}: {d.Message}");

    public void Refresh()
    {
        foreach (var p in new[] { nameof(InstallPath), nameof(InstallKind), nameof(GameVersion), nameof(IndexInfo), nameof(IndexHash),
                     nameof(FileStatusText), nameof(OodleText), nameof(ArchiveText), nameof(AppliedText), nameof(CanAdopt),
                     nameof(CatalogSourceText), nameof(TableLines) })
            OnPropertyChanged(p);
    }

    [RelayCommand]
    private async Task Recheck()
    {
        await app.Game.RunBusy("Checking the installation…", app.Game.DetectAsync);
        Refresh();
    }

    [RelayCommand] private async Task Restore() { await actions.RestoreAsync(); Refresh(); }
    [RelayCommand] private async Task Adopt() { await actions.AdoptBaselineAsync(); Refresh(); }
}
