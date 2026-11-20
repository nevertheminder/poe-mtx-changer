using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoeMtxChanger.Core.Util;

public static class FileUtil
{
    public static string Sha256OfFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Writes to a temp file in the same folder, flushes it to disk, then renames it over the target.</summary>
    public static void WriteAllBytesAtomic(string path, ReadOnlySpan<byte> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? ReadJson<T>(string path) where T : class
        => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;

    public static void WriteJsonAtomic<T>(string path, T value)
        => WriteAllBytesAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    /// <summary>Turns a display name into a safe file name.</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(s) ? "unnamed" : s.Length > 80 ? s[..80] : s;
    }
}

public static class AppPaths
{
    public static string DataRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PoeMtxChanger");

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string ProfilesDirectory => Path.Combine(DataRoot, "profiles");
    public static string LogsDirectory => Path.Combine(DataRoot, "logs");
    public static string DefaultBackupDirectory => Path.Combine(DataRoot, "backups");
}
