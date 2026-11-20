using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Installation;

public enum InstallationKind { Steam, Standalone, Epic, Unknown }

public sealed record GameInstallation
{
    public required string RootPath { get; init; }
    public required InstallationKind Kind { get; init; }
    /// <summary>Bundles2/_.index.bin on disk (Steam/Epic), or null for a GGPK install.</summary>
    public string? IndexPath { get; init; }
    public string? GgpkPath { get; init; }
    public string? SteamBuildId { get; init; }
    /// <summary>FileVersion of the game executable, if it has one.</summary>
    public string? ExecutableVersion { get; init; }
    public required string DetectedBy { get; init; }

    public bool UsesGgpk => GgpkPath is not null;
    public string Bundles2Directory => Path.Combine(RootPath, "Bundles2");

    public IBundleStorage OpenStorage() => UsesGgpk ? new GgpkBundleStorage(GgpkPath!) : new DirectoryBundleStorage(Bundles2Directory);
}

/// <summary>Finds Path of Exile 1 installs. Registry entries are validated because stale ones are common.</summary>
public static partial class GameInstallationDetector
{
    public const string SteamAppId = "238960";
    private static readonly string[] ExeNames = ["PathOfExileSteam.exe", "PathOfExile_x64Steam.exe", "PathOfExile.exe", "PathOfExile_x64.exe", "PathOfExileEGS.exe", "PathOfExile_x64EGS.exe"];

    /// <summary>Inspects one folder. Returns null when it is not a valid install.</summary>
    public static GameInstallation? Probe(string root, string detectedBy, string? steamBuildId = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        root = Path.GetFullPath(root.TrimEnd('\\', '/'));
        var ggpk = Path.Combine(root, "Content.ggpk");
        var index = Path.Combine(root, "Bundles2", DirectoryBundleStorage.IndexFileName);
        bool hasGgpk = File.Exists(ggpk), hasIndex = File.Exists(index);
        if (!hasGgpk && !hasIndex) return null;

        // A Steam install lives in <library>/steamapps/common/<dir>; its manifest sits two levels up.
        if (steamBuildId is null)
        {
            var manifest = Path.GetFullPath(Path.Combine(root, "..", "..", $"appmanifest_{SteamAppId}.acf"));
            if (File.Exists(manifest))
            {
                try { ParseVdfFlat(File.ReadAllText(manifest)).TryGetValue("buildid", out steamBuildId); }
                catch (IOException) { }
            }
        }

        var exe = ExeNames.Select(n => Path.Combine(root, n)).FirstOrDefault(File.Exists);
        InstallationKind kind = hasGgpk ? InstallationKind.Standalone
            : exe?.Contains("Steam", StringComparison.OrdinalIgnoreCase) == true || steamBuildId != null ? InstallationKind.Steam
            : exe?.Contains("EGS", StringComparison.OrdinalIgnoreCase) == true ? InstallationKind.Epic
            : InstallationKind.Unknown;

        string? version = null;
        if (exe != null)
        {
            try { version = FileVersionInfo.GetVersionInfo(exe).FileVersion; } catch (IOException) { }
            if (string.IsNullOrWhiteSpace(version)) version = null;
        }

        return new GameInstallation
        {
            RootPath = root,
            Kind = kind,
            IndexPath = hasGgpk ? null : index,
            GgpkPath = hasGgpk ? ggpk : null,
            SteamBuildId = steamBuildId,
            ExecutableVersion = version,
            DetectedBy = detectedBy,
        };
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<GameInstallation> DetectAll()
    {
        var found = new List<GameInstallation>();
        void TryAdd(string? root, string by, string? build = null)
        {
            if (root is null) return;
            var inst = Probe(root, by, build);
            if (inst != null && !found.Any(f => string.Equals(f.RootPath, inst.RootPath, StringComparison.OrdinalIgnoreCase)))
                found.Add(inst);
        }

        foreach (var (lib, _) in SteamLibraries())
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{SteamAppId}.acf");
            string? build = null, installDir = "Path of Exile";
            if (File.Exists(manifest))
            {
                var kv = ParseVdfFlat(File.ReadAllText(manifest));
                kv.TryGetValue("buildid", out build);
                if (kv.TryGetValue("installdir", out var d)) installDir = d;
            }
            TryAdd(Path.Combine(lib, "steamapps", "common", installDir), "Steam library", build);
        }

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            foreach (var key in new[] { @"Software\GrindingGearGames\Path of Exile", @"SOFTWARE\WOW6432Node\GrindingGearGames\Path of Exile" })
            {
                using var k = hive.OpenSubKey(key);
                TryAdd(k?.GetValue("InstallLocation") as string, "Registry");
            }

        foreach (var root in EpicInstallLocations()) TryAdd(root, "Epic manifest");

        TryAdd(@"C:\Program Files (x86)\Grinding Gear Games\Path of Exile", "Default location");
        return found;
    }

    /// <summary>Steam library roots on this computer (the Steam folder and the libraryfolders.vdf entries).</summary>
    [SupportedOSPlatform("windows")]
    public static IEnumerable<string> SteamLibraryFolders() => SteamLibraries().Select(l => l.Path).Distinct(StringComparer.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    private static IEnumerable<(string Path, string Source)> SteamLibraries()
    {
        string? steam = null;
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) steam = k?.GetValue("SteamPath") as string;
        steam ??= @"C:\Program Files (x86)\Steam";
        if (!Directory.Exists(steam)) yield break;
        yield return (Path.GetFullPath(steam), "Steam");
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (var lib in ParseLibraryFolders(File.ReadAllText(vdf)))
            if (Directory.Exists(lib)) yield return (Path.GetFullPath(lib), "libraryfolders.vdf");
    }

    private static IEnumerable<string> EpicInstallLocations()
    {
        var dir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            string? loc = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                if (r.TryGetProperty("DisplayName", out var n) && n.GetString()?.Contains("Path of Exile", StringComparison.OrdinalIgnoreCase) == true
                    && r.TryGetProperty("InstallLocation", out var l))
                    loc = l.GetString();
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
            if (loc != null) yield return loc;
        }
    }

    [GeneratedRegex("\"(?<k>[^\"]+)\"\\s+\"(?<v>[^\"]*)\"")]
    private static partial Regex VdfPair();

    /// <summary>Flat key/value view of a Valve KeyValues text file (first occurrence wins).</summary>
    public static Dictionary<string, string> ParseVdfFlat(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in VdfPair().Matches(text))
            d.TryAdd(m.Groups["k"].Value, m.Groups["v"].Value.Replace(@"\\", @"\"));
        return d;
    }

    public static IReadOnlyList<string> ParseLibraryFolders(string text)
        => VdfPair().Matches(text).Where(m => m.Groups["k"].Value.Equals("path", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups["v"].Value.Replace(@"\\", @"\")).ToList();
}

/// <summary>Detects whether the game client is running, so nothing is modified underneath it.</summary>
public static class GameProcessGuard
{
    private static readonly string[] ProcessNames =
        ["PathOfExile", "PathOfExile_x64", "PathOfExileSteam", "PathOfExile_x64Steam", "PathOfExileEGS", "PathOfExile_x64EGS", "PathOfExile_KG", "PathOfExile_x64_KG"];

    public static IReadOnlyList<string> RunningClients()
    {
        var list = new List<string>();
        foreach (var n in ProcessNames)
        {
            var procs = Process.GetProcessesByName(n);
            if (procs.Length > 0) list.Add(n);
            foreach (var p in procs) p.Dispose();
        }
        return list;
    }

    public static bool IsGameRunning => RunningClients().Count > 0;
}
