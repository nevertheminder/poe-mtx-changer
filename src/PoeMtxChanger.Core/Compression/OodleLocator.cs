using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using PoeMtxChanger.Core.Installation;

namespace PoeMtxChanger.Core.Compression;

/// <summary>
/// Finds an Oodle library (oo2core_*_win64.dll) that is already on this computer, so the user does not have to pick it:
/// next to the program, in Unreal Engine installs and Epic games (LauncherInstalled.dat), and in Steam games. Nothing is
/// downloaded or copied; the library is loaded from where it was found. Newer versions are tried first.
/// </summary>
public static partial class OodleLocator
{
    [GeneratedRegex(@"^oo2core_(\d+)_win64\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex FileName();

    private static readonly EnumerationOptions Shallow = new()
    {
        RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
    };

    /// <summary>Candidate files in search order (program folder, Unreal/Epic, Steam), newest version first within each.</summary>
    [SupportedOSPlatform("windows")]
    public static IEnumerable<(string Path, string Source)> Candidates(string? programDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<(string, string)> Newest(IEnumerable<string> files, string source) => files
            .Select(f => (Path: f, Match: FileName().Match(System.IO.Path.GetFileName(f))))
            .Where(x => x.Match.Success && seen.Add(System.IO.Path.GetFullPath(x.Path)))
            .OrderByDescending(x => int.Parse(x.Match.Groups[1].Value))
            .Select(x => (x.Path, source));

        if (programDirectory != null && Directory.Exists(programDirectory))
            foreach (var c in Newest(Directory.EnumerateFiles(programDirectory, "oo2core_*_win64.dll"), "program folder")) yield return c;

        foreach (var (root, isEngine) in EpicInstalls())
        {
            // Unreal Engine keeps it with the automation tools; games keep it next to their executable.
            var places = isEngine
                ? new[] { System.IO.Path.Combine(root, "Engine", "Binaries", "DotNET", "AutomationTool"), System.IO.Path.Combine(root, "Engine", "Binaries", "Win64") }
                : [root];
            foreach (var place in places.Where(Directory.Exists))
                foreach (var c in Newest(Search(place, isEngine ? 1 : 3), isEngine ? "Unreal Engine" : "Epic game")) yield return c;
        }

        foreach (var library in GameInstallationDetector.SteamLibraryFolders())
        {
            var common = System.IO.Path.Combine(library, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            foreach (var game in SafeDirectories(common))
                foreach (var c in Newest(Search(game, 3), "Steam game")) yield return c;
        }
    }

    /// <summary>Loads the first candidate that is a working Oodle library, or returns null.</summary>
    [SupportedOSPlatform("windows")]
    public static (IOodleCodec Codec, string Path, string Source)? FindAndLoad(string? programDirectory, CancellationToken ct = default)
    {
        foreach (var (path, source) in Candidates(programDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var codec = NativeOodleCodec.TryLoad(path);
            if (codec.IsAvailable) return (codec, path, source);
            if (codec is IDisposable d) d.Dispose();
        }
        return null;
    }

    private static IEnumerable<string> Search(string root, int depth)
    {
        try { return Directory.EnumerateFiles(root, "oo2core_*_win64.dll", new EnumerationOptions
        {
            RecurseSubdirectories = depth > 0, MaxRecursionDepth = depth, IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = Shallow.AttributesToSkip,
        }).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Install locations from the Epic launcher's LauncherInstalled.dat; engine installs are named UE_*.</summary>
    private static IEnumerable<(string Root, bool IsEngine)> EpicInstalls()
    {
        var file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
        if (!File.Exists(file)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("InstallationList", out var list) || list.ValueKind != JsonValueKind.Array) return [];
            return list.EnumerateArray()
                .Select(e => (Root: e.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null,
                    App: e.TryGetProperty("AppName", out var a) ? a.GetString() : null))
                .Where(e => !string.IsNullOrWhiteSpace(e.Root) && Directory.Exists(e.Root))
                .Select(e => (e.Root!, e.App?.StartsWith("UE_", StringComparison.OrdinalIgnoreCase) == true
                    || File.Exists(System.IO.Path.Combine(e.Root!, "Engine", "Build", "Build.version"))))
                .DistinctBy(e => e.Item1, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(e => e.Item2) // engines first
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }
}
