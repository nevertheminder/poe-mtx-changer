using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.Core.Installation;

namespace PoeMtxChanger.App.ViewModels;

public enum MainTab { Catalog, MySets, Wardrobe, Profiles, Settings, About }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _app;
    private readonly DispatcherTimer _processTimer;

    public GameSession Game => _app.Game;
    internal AppServices Services => _app;
    public ProfileSession Profiles => _app.Profiles;
    public CatalogViewModel Catalog { get; }
    public MySetsViewModel MySets { get; }
    public ProfilesViewModel ProfilesPage { get; }
    public SettingsViewModel SettingsPage { get; }
    public AboutViewModel About { get; }

    public string VersionText => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)} (PoE 1)";

    [ObservableProperty] private MainTab _selectedTab = MainTab.Catalog;
    [ObservableProperty] private bool _isGameRunning;

    public string GameStatusText => IsGameRunning ? "The game is running, file changes are blocked" : "Game not running";

    public MainViewModel(AppServices app)
    {
        _app = app;
        var actions = new ProfileActions(app);
        Catalog = new CatalogViewModel(app, actions);
        MySets = new MySetsViewModel(app, actions);
        ProfilesPage = new ProfilesViewModel(app, actions);
        SettingsPage = new SettingsViewModel(app);
        About = new AboutViewModel(app, actions);

        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _processTimer.Tick += (_, _) => IsGameRunning = GameProcessGuard.IsGameRunning;
        _processTimer.Start();
        IsGameRunning = GameProcessGuard.IsGameRunning;
    }

    partial void OnIsGameRunningChanged(bool value) => OnPropertyChanged(nameof(GameStatusText));

    partial void OnSelectedTabChanged(MainTab value)
    {
        if (value == MainTab.MySets) MySets.Refresh();
        if (value == MainTab.About) About.Refresh();
    }

    /// <summary>Normal interactive start: may show the one-time Oodle help (off for the screenshot/render modes).</summary>
    public bool ShowOodleHelp { get; set; }

    public async Task InitializeAsync()
    {
        await Game.RunBusy("Looking for the installation…", async () =>
        {
            Game.ReloadCodec();
            if (!Game.Codec.IsAvailable)
            {
                Game.BusyText = "Looking for the Oodle library…";
                if (!await Game.FindCodecAsync() && ShowOodleHelp && !_app.Settings.OodleHelpShown)
                {
                    _app.Settings.OodleHelpShown = true;
                    _app.SaveSettings();
                    if (OodleHelp.Show()) SelectedTab = MainTab.Settings;
                }
            }
            await Game.DetectAsync();
            if (Game.Codec.IsAvailable && Game.Installation != null)
            {
                Game.BusyText = "Reading the resource index…";
                await Game.LoadArchiveAsync();
            }
            else if (_app.Settings.ShowMockData)
                Game.LoadMockCatalog();
        });
        About.Refresh();
    }

    [RelayCommand]
    private void LaunchGame()
    {
        var inst = Game.Installation;
        if (inst is null) { _app.Dialogs.Info("Launch", "Game installation not found. Set the path in the settings."); return; }
        try
        {
            if (inst.Kind == InstallationKind.Steam)
                Process.Start(new ProcessStartInfo($"steam://rungameid/{GameInstallationDetector.SteamAppId}") { UseShellExecute = true });
            else
            {
                var exe = new[] { "PathOfExile_x64.exe", "PathOfExile.exe", "PathOfExile_x64EGS.exe", "PathOfExileEGS.exe" }
                    .Select(n => Path.Combine(inst.RootPath, n)).FirstOrDefault(File.Exists);
                if (exe is null) { _app.Dialogs.Info("Launch", "The game executable was not found."); return; }
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = inst.RootPath });
            }
            _app.Log("App").LogInformation("Game launch requested by user");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _app.Dialogs.Error("Launch", ex.Message);
        }
    }
}
