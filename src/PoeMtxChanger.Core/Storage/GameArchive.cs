using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Formats.Bundles;

namespace PoeMtxChanger.Core.Storage;

/// <summary>Read access to game resources by virtual path ("Art/...", "Metadata/...").</summary>
public interface IResourceSource
{
    bool Exists(string path);
    byte[] Read(string path);
    IEnumerable<string> EnumeratePaths();
}

/// <summary>An opened bundle index plus the storage its bundles live in.</summary>
public sealed class GameArchive : IResourceSource, IDisposable
{
    private readonly Dictionary<int, BundleHeader> _headers = [];
    private readonly Lock _lock = new();

    public IBundleStorage Storage { get; }
    public IOodleCodec Codec { get; }
    public BundleIndex Index { get; }
    public BundleHeader IndexHeader { get; }
    public int UnresolvedPathCount { get; }

    private GameArchive(IBundleStorage storage, IOodleCodec codec, BundleIndex index, BundleHeader header, int unresolved)
    {
        Storage = storage;
        Codec = codec;
        Index = index;
        IndexHeader = header;
        UnresolvedPathCount = unresolved;
    }

    /// <summary>Opens the index. <paramref name="indexBytesOverride"/> lets callers open a backed-up copy instead of the live file.</summary>
    public static GameArchive Open(IBundleStorage storage, IOodleCodec codec, byte[]? indexBytesOverride = null, bool resolvePaths = true)
    {
        if (!codec.IsAvailable) throw new OodleUnavailableException(codec.Description);
        var raw = indexBytesOverride ?? storage.ReadIndexBytes();
        var header = BundleHeader.Read(raw);
        var index = BundleIndex.Parse(BundleFile.Decompress(raw, codec));
        int unresolved = 0;
        if (resolvePaths && index.PathTableBundle.Length > 0)
            unresolved = index.ResolvePaths(BundleFile.Decompress(index.PathTableBundle, codec));
        return new GameArchive(storage, codec, index, header, unresolved);
    }

    public bool TryGetRecord(string path, out FileRecord record) => Index.TryGetFile(path, out record);

    public bool Exists(string path) => Index.TryGetFile(path, out _);

    public byte[] Read(string path)
    {
        if (!Index.TryGetFile(path, out var rec)) throw new FileNotFoundException($"Not in index: {path}");
        return Read(rec);
    }

    public byte[] Read(FileRecord record)
    {
        var bundle = Index.Bundles[record.BundleIndex];
        using var stream = Storage.OpenBundle(bundle.Name);
        BundleHeader header;
        lock (_lock)
        {
            if (!_headers.TryGetValue(record.BundleIndex, out header!))
            {
                header = BundleHeader.Read(stream);
                _headers[record.BundleIndex] = header;
            }
        }
        return BundleFile.ReadRange(stream, header, record.Offset, record.Size, Codec);
    }

    public IEnumerable<string> EnumeratePaths()
    {
        foreach (var f in Index.Files)
            if (f.Path is not null) yield return f.Path;
    }

    public void Dispose() => Storage.Dispose();
}

/// <summary>In-memory resource source for tests, previews and mock data.</summary>
public sealed class InMemoryResourceSource : IResourceSource
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryResourceSource Add(string path, byte[] content) { _files[BundleIndex.NormalizePath(path)] = content; return this; }
    public InMemoryResourceSource Add(string path, string utf16Text)
        => Add(path, [0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes(utf16Text)]);

    public bool Exists(string path) => _files.ContainsKey(BundleIndex.NormalizePath(path));
    public byte[] Read(string path) => _files.TryGetValue(BundleIndex.NormalizePath(path), out var d) ? d : throw new FileNotFoundException(path);
    public IEnumerable<string> EnumeratePaths() => _files.Keys;
}
