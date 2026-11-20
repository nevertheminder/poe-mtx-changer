using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Formats.Dat;
using PoeMtxChanger.Core.Installation;
using PoeMtxChanger.Core.Logging;
using PoeMtxChanger.Core.Patching;
using PoeMtxChanger.Core.Profiles;
using PoeMtxChanger.Core.Settings;
using PoeMtxChanger.Core.Storage;
using PoeMtxChanger.Core.Util;

namespace PoeMtxChanger.App.Services;

/// <summary>Composition root shared by all view models.</summary>
public sealed class AppServices
{
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public FileLoggerProvider LogProvider { get; }
    public ILoggerFactory LoggerFactory { get; }
    public GameSession Game { get; }
    public ProfileSession Profiles { get; }
    public IDialogService Dialogs { get; } = new DialogService();
    public ThumbnailService Thumbnails { get; }

    public AppServices()
    {
        SettingsStore = new SettingsStore(AppPaths.SettingsFile);
        Settings = SettingsStore.Load();
        LogProvider = new FileLoggerProvider(AppPaths.LogsDirectory)
        {
            MinimumLevel = () => Settings.DetailedLogging ? LogLevel.Debug : LogLevel.Information,
        };
        LoggerFactory = new SimpleLoggerFactory(LogProvider);
        Game = new GameSession(this);
        Profiles = new ProfileSession(this);
        Thumbnails = new ThumbnailService(this);
    }

    public ILogger Log(string category) => LoggerFactory.CreateLogger(category);
    public void SaveSettings() => SettingsStore.Save(Settings);

    private sealed class SimpleLoggerFactory(ILoggerProvider provider) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);
        public void AddProvider(ILoggerProvider p) { }
        public void Dispose() { }
    }
}

/// <summary>State of the detected installation, the Oodle codec, the opened index and the catalog.</summary>
public sealed partial class GameSession(AppServices app) : ObservableObject
{
    private readonly ILogger _log = app.Log("Game");

    [ObservableProperty] private GameInstallation? _installation;
    [ObservableProperty] private IndexHeaderInfo? _headerInfo;
    [ObservableProperty] private string? _headerError;
    [ObservableProperty] private IOodleCodec _codec = new UnavailableOodleCodec("No Oodle library configured.");
    [ObservableProperty] private GameArchive? _archive;
    [ObservableProperty] private string? _archiveError;
    [ObservableProperty] private GameFileStatus? _fileStatus;
    [ObservableProperty] private PatchState? _patchState;
    [ObservableProperty] private IReadOnlyList<CosmeticItem> _catalog = [];
    [ObservableProperty] private bool _catalogIsMock;
    [ObservableProperty] private IReadOnlyList<TargetSuggestion> _suggestions = [];
    [ObservableProperty] private IReadOnlyList<TableDiagnostic> _tableDiagnostics = [];
    [ObservableProperty] private IReadOnlyList<SkillVisualDefinition> _skillVisuals = [];
    [ObservableProperty] private IReadOnlyList<BuffVisualDefinition> _buffVisuals = [];
    [ObservableProperty] private IReadOnlyList<BuffSkillDefinition> _buffSkills = [];
    [ObservableProperty] private string _catalogSource = "—";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";

    public event EventHandler? CatalogChanged;

    public bool CanWrite => Installation != null && Codec.IsAvailable;

    public async Task RunBusy(string text, Func<Task> work)
    {
        IsBusy = true;
        BusyText = text;
        try { await work(); }
        finally { IsBusy = false; BusyText = ""; }
    }

    public void ReloadCodec()
    {
        Archive?.Dispose();
        Archive = null;
        if (Codec is IDisposable d) d.Dispose();
        Codec = NativeOodleCodec.TryLoad(app.Settings.OodleLibraryPath);
        _log.LogInformation("Oodle: {State}", Codec.IsAvailable ? "loaded " + Codec.Description : Codec.Description);
        OnPropertyChanged(nameof(CanWrite));
    }

    /// <summary>
    /// When no working Oodle library is configured, looks for one already on this computer (next to the program, in
    /// Unreal Engine or Epic games, in Steam games), loads it and saves its path in the settings.
    /// </summary>
    public async Task<bool> FindCodecAsync()
    {
        if (Codec.IsAvailable) return true;
        var found = await Task.Run(() => OodleLocator.FindAndLoad(AppContext.BaseDirectory));
        if (found is not { } f)
        {
            _log.LogInformation("Oodle: no library found automatically");
            return false;
        }
        Archive?.Dispose();
        Archive = null;
        if (Codec is IDisposable d) d.Dispose();
        Codec = f.Codec;
        app.Settings.OodleLibraryPath = f.Path;
        app.SaveSettings();
        _log.LogInformation("Oodle: found automatically ({Source}) at {Path}", f.Source, f.Path);
        OnPropertyChanged(nameof(CanWrite));
        return true;
    }

    /// <summary>Finds the installation (configured path first, then auto-detection) and inspects its index header.</summary>
    public async Task DetectAsync()
    {
        await Task.Run(() =>
        {
            GameInstallation? inst = null;
            if (!string.IsNullOrWhiteSpace(app.Settings.GameRootPath))
                inst = GameInstallationDetector.Probe(app.Settings.GameRootPath!, "Settings");
            if (inst is null)
            {
                inst = GameInstallationDetector.DetectAll().FirstOrDefault();
                if (inst != null)
                {
                    app.Settings.GameRootPath = inst.RootPath;
                    app.SaveSettings();
                }
            }
            Installation = inst;
        });
        _log.LogInformation("Installation: {Root} ({Kind})", Installation?.RootPath ?? "not found", Installation?.Kind);
        OnPropertyChanged(nameof(CanWrite));
        await InspectAsync();
    }

    public async Task InspectAsync()
    {
        HeaderInfo = null;
        HeaderError = null;
        FileStatus = null;
        PatchState = null;
        var inst = Installation;
        if (inst is null) return;
        await Task.Run(() =>
        {
            try
            {
                if (inst.IndexPath != null)
                {
                    HeaderInfo = IndexInspector.Inspect(inst.IndexPath);
                    var engine = CreateEngine();
                    if (engine != null)
                    {
                        FileStatus = engine.GetStatus();
                        PatchState = engine.LoadState();
                    }
                }
                else
                {
                    using var storage = inst.OpenStorage();
                    HeaderInfo = IndexInspector.Inspect(storage.ReadIndexBytes());
                    var engine = CreateEngine()!;
                    FileStatus = engine.GetStatus();
                    PatchState = engine.LoadState();
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                HeaderError = ex.Message;
                _log.LogWarning(ex, "Index inspection failed");
            }
        });
    }

    public IGamePatchEngine? CreateEngine()
    {
        if (Installation is not { } inst) return null;
        if (inst.UsesGgpk) return new StandalonePatchEngine(inst.GgpkPath!, Codec, app.Settings.BackupDirectory);
        return new PatchEngine(new DirectoryBundleStorage(inst.Bundles2Directory), Codec, app.Settings.BackupDirectory, app.Log("Patch"));
    }

    /// <summary>Decompresses the index (needs Oodle) and builds the cosmetic catalog.</summary>
    public async Task LoadArchiveAsync()
    {
        ArchiveError = null;
        if (Installation is null) { ArchiveError = "No installation detected."; return; }
        if (!Codec.IsAvailable) { ArchiveError = Codec.Description; return; }
        var inst = Installation;
        try
        {
            var (archive, items, data, sourceText) = await Task.Run(() =>
            {
                byte[]? cleanIndex = null;
                var engine = CreateEngine();
                if (engine?.GetStatus() == GameFileStatus.PatchedByUs)
                {
                    cleanIndex = engine.ReadCleanIndex();
                }
                // Plans always resolve original resources, even when an earlier profile is active.
                var a = GameArchive.Open(inst.OpenStorage(), Codec, cleanIndex);
                var rules = string.IsNullOrWhiteSpace(app.Settings.CatalogRulesPath) ? CatalogRules.LoadDefault() : CatalogRules.Load(app.Settings.CatalogRulesPath!);
                DataCatalogResult? data = null;
                string src;
                try
                {
                    var schema = string.IsNullOrWhiteSpace(app.Settings.DatSchemaPath) ? DatSchema.LoadDefault() : DatSchema.LoadFile(app.Settings.DatSchemaPath!);
                    data = new DataCatalogBuilder(a, schema, rules).Build();
                    // Table writers (ItemVisualIdentity edits) must use the same layout the plans were built from.
                    DatSchema.Current = schema;
                    src = $"game tables ({schema.Source}, schema from {schema.CreatedAt:yyyy-MM-dd})";
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
                {
                    _log.LogWarning(ex, "Data tables could not be read");
                    src = "path heuristics (schema not loaded: " + ex.Message + ")";
                }
                if (data is { HasMtxNames: true, Items.Count: > 0 })
                    return (a, data.Items, data, src);
                return (a, new CatalogBuilder(rules).Build(a), data, data is null ? src : "path heuristics (MTX tables were not read)");
            });
            Archive?.Dispose();
            Archive = archive;
            Catalog = items;
            CatalogIsMock = false;
            Suggestions = data?.Suggestions ?? [];
            TableDiagnostics = data?.Diagnostics ?? [];
            SkillVisuals = data?.Skills ?? [];
            BuffVisuals = data?.Buffs ?? [];
            BuffSkills = data?.BuffSkills ?? [];
            CatalogSource = sourceText;
            foreach (var d in TableDiagnostics.Where(d => !d.Ok)) _log.LogWarning("Table {Table}: {Message}", d.Table, d.Message);
            _log.LogInformation("Index loaded: {Files} files, {Bundles} bundles, {Unresolved} unresolved paths; {Items} cosmetics discovered",
                archive.Index.Files.Count, archive.Index.Bundles.Count, archive.UnresolvedPathCount, items.Count);
        }
        catch (Exception ex)
        {
            ArchiveError = ex.Message;
            _log.LogError(ex, "Failed to load index");
        }
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    public void LoadMockCatalog()
    {
        Catalog = MockCatalog.Create();
        CatalogIsMock = true;
        CatalogSource = "demo data (MOCK)";
        Suggestions = [new TargetSuggestion("portal", "[MOCK] Default portal", "mock/portal/default.aoc", "mock")];
        SkillVisuals = [];
        BuffVisuals = [];
        BuffSkills = [];
        _log.LogInformation("Mock catalog loaded ({Count} demo items)", Catalog.Count);
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    public IResourceSource? Resources => Archive;
}

/// <summary>Profiles on disk plus the active profile.</summary>
public sealed partial class ProfileSession : ObservableObject
{
    private readonly AppServices _app;
    public ProfileManager Manager { get; }
    public ObservableCollection<CosmeticProfile> All { get; } = [];

    [ObservableProperty] private CosmeticProfile? _active;

    public ProfileSession(AppServices app)
    {
        _app = app;
        Manager = new ProfileManager(AppPaths.ProfilesDirectory);
        Reload();
    }

    public void Reload()
    {
        var id = _app.Settings.ActiveProfileId;
        All.Clear();
        foreach (var p in Manager.LoadAll()) All.Add(p);
        if (All.Count == 0)
        {
            var p = new CosmeticProfile { Name = "Default" };
            Manager.Save(p);
            All.Add(p);
        }
        Active = All.FirstOrDefault(p => p.Id == id) ?? All[0];
    }

    partial void OnActiveChanged(CosmeticProfile? value)
    {
        _app.Settings.ActiveProfileId = value?.Id;
        _app.SaveSettings();
    }

    public void SaveActive()
    {
        if (Active != null)
        {
            Active.GameBuild ??= _app.Game.Installation?.SteamBuildId;
            Manager.Save(Active);
        }
    }
}
