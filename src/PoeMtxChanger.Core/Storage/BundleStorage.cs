using PoeMtxChanger.Core.Formats.Ggpk;

namespace PoeMtxChanger.Core.Storage;

/// <summary>Where the index and bundle files physically live.</summary>
public interface IBundleStorage : IDisposable
{
    string Description { get; }
    bool SupportsWriting { get; }
    byte[] ReadIndexBytes();
    Stream OpenBundle(string bundleName);
    bool BundleExists(string bundleName);
}

/// <summary>Steam/Epic layout: loose files under &lt;game&gt;/Bundles2.</summary>
public sealed class DirectoryBundleStorage(string bundles2Directory) : IBundleStorage
{
    public const string IndexFileName = "_.index.bin";

    public string Bundles2Directory { get; } = Path.GetFullPath(bundles2Directory);
    public string IndexPath => Path.Combine(Bundles2Directory, IndexFileName);
    public string Description => Bundles2Directory;
    public bool SupportsWriting => true;

    public byte[] ReadIndexBytes()
    {
        using var fs = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var data = new byte[fs.Length];
        fs.ReadExactly(data);
        return data;
    }

    public string GetBundlePath(string bundleName)
    {
        var full = Path.GetFullPath(Path.Combine(Bundles2Directory, bundleName.Replace('/', Path.DirectorySeparatorChar) + ".bundle.bin"));
        if (!full.StartsWith(Bundles2Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Bundle name escapes the Bundles2 directory: {bundleName}");
        return full;
    }

    public Stream OpenBundle(string bundleName)
        => new FileStream(GetBundlePath(bundleName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);

    public bool BundleExists(string bundleName) => File.Exists(GetBundlePath(bundleName));

    public void Dispose() { }
}

/// <summary>Standalone layout: bundles stored inside Content.ggpk. Read-only.</summary>
public sealed class GgpkBundleStorage(string ggpkPath) : IBundleStorage
{
    private readonly GgpkArchive _ggpk = GgpkArchive.OpenRead(ggpkPath);
    private readonly Lock _lock = new();

    public string Description => ggpkPath;
    public bool SupportsWriting => false;

    private GgpkFileEntry Find(string relative)
    {
        lock (_lock)
        {
            if (_ggpk.BundleFiles.TryGetValue("Bundles2/" + relative, out var e)) return e;
        }
        throw new FileNotFoundException($"Bundles2/{relative} not found in Content.ggpk.");
    }

    public byte[] ReadIndexBytes()
    {
        lock (_lock) return _ggpk.ReadFile(Find(DirectoryBundleStorage.IndexFileName));
    }

    public Stream OpenBundle(string bundleName)
    {
        // Copy to memory: the GGPK stream position is shared, so independent readers need their own buffer.
        lock (_lock) return new MemoryStream(_ggpk.ReadFile(Find(bundleName + ".bundle.bin")), writable: false);
    }

    public bool BundleExists(string bundleName)
    {
        lock (_lock) return _ggpk.BundleFiles.ContainsKey("Bundles2/" + bundleName + ".bundle.bin");
    }

    public void Dispose() => _ggpk.Dispose();
}
