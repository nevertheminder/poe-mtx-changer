using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Util;

namespace PoeMtxChanger.Core.Settings;

public sealed class AppSettings
{
    public string? GameRootPath { get; set; }
    public string BackupDirectory { get; set; } = AppPaths.DefaultBackupDirectory;
    /// <summary>Path to a user-supplied oo2core_*_win64.dll. Never bundled with the app.</summary>
    public string? OodleLibraryPath { get; set; }
    public bool DetailedLogging { get; set; }
    /// <summary>Run dependency analysis before applying (recommended).</summary>
    public bool ValidateResources { get; set; } = true;
    public bool CheckCompatibilityOnStartup { get; set; } = true;
    public bool ShowMockData { get; set; }
    public string? ActiveProfileId { get; set; }
    public OodleCompressor Compressor { get; set; } = OodleCompressor.Mermaid;
    public OodleCompressionLevel CompressionLevel { get; set; } = OodleCompressionLevel.Normal;
    /// <summary>Optional path to custom catalog rules JSON.</summary>
    public string? CatalogRulesPath { get; set; }
    /// <summary>Optional newer dat-schema JSON (poe-tool-dev/dat-schema format); the embedded copy is used otherwise.</summary>
    public string? DatSchemaPath { get; set; }
    public HashSet<string> FavoriteCosmeticIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Wardrobe and 3D preview simulate cloth on phys_* bones (capes, ribbons, skirts).</summary>
    public bool ClothPhysics { get; set; } = true;
    /// <summary>The “Oodle library not found” help was shown once at startup (it stays available in Settings).</summary>
    public bool OodleHelpShown { get; set; }
}

public sealed class SettingsStore(string path)
{
    public string Path { get; } = path;

    public AppSettings Load()
    {
        try { return FileUtil.ReadJson<AppSettings>(Path) ?? new AppSettings(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { return new AppSettings(); }
    }

    public void Save(AppSettings settings) => FileUtil.WriteJsonAtomic(Path, settings);
}
