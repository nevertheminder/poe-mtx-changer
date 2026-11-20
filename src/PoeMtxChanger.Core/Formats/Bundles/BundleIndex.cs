using System.Buffers.Binary;
using System.Text;

namespace PoeMtxChanger.Core.Formats.Bundles;

public sealed class BundleRecord(string name, int uncompressedSize)
{
    /// <summary>Bundle name relative to Bundles2, without ".bundle.bin".</summary>
    public string Name { get; set; } = name;
    public int UncompressedSize { get; set; } = uncompressedSize;
    public string FileName => Name + ".bundle.bin";
}

public sealed class FileRecord(ulong pathHash, int bundleIndex, int offset, int size)
{
    public const int RecordLength = 20;
    public ulong PathHash { get; } = pathHash;
    public int BundleIndex { get; set; } = bundleIndex;
    public int Offset { get; set; } = offset;
    public int Size { get; set; } = size;
    /// <summary>Resolved path (null until the path table has been parsed, or if it could not be resolved).</summary>
    public string? Path { get; set; }

    public FileRecord Clone() => new(PathHash, BundleIndex, Offset, Size) { Path = Path };
}

public readonly record struct DirectoryRecord(ulong PathHash, int Offset, int Size, int RecursiveSize)
{
    public const int RecordLength = 20;
}

/// <summary>
/// Decompressed payload of _.index.bin: bundle list, file list, directory list and the
/// (still compressed) path-table bundle. Reimplemented from the documented format.
/// </summary>
public sealed class BundleIndex
{
    public List<BundleRecord> Bundles { get; } = [];
    public List<FileRecord> Files { get; } = [];
    public List<DirectoryRecord> Directories { get; } = [];

    /// <summary>Path table as stored: a nested, compressed bundle. Kept byte-for-byte on save.</summary>
    public byte[] PathTableBundle { get; set; } = [];

    public PathHashAlgorithm HashAlgorithm { get; set; } = PathHashAlgorithm.Murmur64A;

    private Dictionary<ulong, FileRecord>? _byHash;

    public IReadOnlyDictionary<ulong, FileRecord> FilesByHash => _byHash ??= BuildHashMap();

    private Dictionary<ulong, FileRecord> BuildHashMap()
    {
        var d = new Dictionary<ulong, FileRecord>(Files.Count);
        foreach (var f in Files) d[f.PathHash] = f;
        return d;
    }

    public bool TryGetFile(string path, out FileRecord record)
        => FilesByHash.TryGetValue(PathHash.Hash(NormalizePath(path), HashAlgorithm), out record!);

    public static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    public int AddBundle(string name, int uncompressedSize)
    {
        Bundles.Add(new BundleRecord(name, uncompressedSize));
        return Bundles.Count - 1;
    }

    public static BundleIndex Parse(ReadOnlySpan<byte> payload)
    {
        var idx = new BundleIndex();
        int p = 0;

        int bundleCount = ReadInt(payload, ref p);
        if (bundleCount < 0) throw new InvalidDataException("Negative bundle count.");
        for (int i = 0; i < bundleCount; i++)
        {
            int nameLen = ReadInt(payload, ref p);
            Need(payload, p, nameLen);
            var name = Encoding.UTF8.GetString(payload.Slice(p, nameLen));
            p += nameLen;
            idx.Bundles.Add(new BundleRecord(name, ReadInt(payload, ref p)));
        }

        int fileCount = ReadInt(payload, ref p);
        if (fileCount < 0) throw new InvalidDataException("Negative file count.");
        Need(payload, p, (long)fileCount * FileRecord.RecordLength);
        idx.Files.Capacity = fileCount;
        for (int i = 0; i < fileCount; i++)
        {
            ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(payload[p..]);
            int bundle = BinaryPrimitives.ReadInt32LittleEndian(payload[(p + 8)..]);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(payload[(p + 12)..]);
            int size = BinaryPrimitives.ReadInt32LittleEndian(payload[(p + 16)..]);
            p += FileRecord.RecordLength;
            if ((uint)bundle >= (uint)bundleCount)
                throw new InvalidDataException($"File record {i} points to bundle {bundle} of {bundleCount}.");
            idx.Files.Add(new FileRecord(hash, bundle, offset, size));
        }

        int dirCount = ReadInt(payload, ref p);
        if (dirCount < 0) throw new InvalidDataException("Negative directory count.");
        Need(payload, p, (long)dirCount * DirectoryRecord.RecordLength);
        for (int i = 0; i < dirCount; i++)
        {
            idx.Directories.Add(new DirectoryRecord(
                BinaryPrimitives.ReadUInt64LittleEndian(payload[p..]),
                BinaryPrimitives.ReadInt32LittleEndian(payload[(p + 8)..]),
                BinaryPrimitives.ReadInt32LittleEndian(payload[(p + 12)..]),
                BinaryPrimitives.ReadInt32LittleEndian(payload[(p + 16)..])));
            p += DirectoryRecord.RecordLength;
        }

        idx.PathTableBundle = payload[p..].ToArray();
        if (idx.Directories.Count > 0)
            idx.HashAlgorithm = PathHash.DetectFromRootHash(idx.Directories[0].PathHash) ?? PathHashAlgorithm.Murmur64A;
        return idx;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        var b = new byte[8];

        void WriteInt(int v) { BinaryPrimitives.WriteInt32LittleEndian(b, v); ms.Write(b, 0, 4); }
        void WriteULong(ulong v) { BinaryPrimitives.WriteUInt64LittleEndian(b, v); ms.Write(b, 0, 8); }

        WriteInt(Bundles.Count);
        foreach (var br in Bundles)
        {
            var name = Encoding.UTF8.GetBytes(br.Name);
            WriteInt(name.Length);
            ms.Write(name);
            WriteInt(br.UncompressedSize);
        }
        WriteInt(Files.Count);
        foreach (var f in Files)
        {
            WriteULong(f.PathHash);
            WriteInt(f.BundleIndex);
            WriteInt(f.Offset);
            WriteInt(f.Size);
        }
        WriteInt(Directories.Count);
        foreach (var d in Directories)
        {
            WriteULong(d.PathHash);
            WriteInt(d.Offset);
            WriteInt(d.Size);
            WriteInt(d.RecursiveSize);
        }
        ms.Write(PathTableBundle);
        return ms.ToArray();
    }

    /// <summary>
    /// Resolves <see cref="FileRecord.Path"/> from the decompressed path table.
    /// Returns the number of generated paths that did not match any file record.
    /// </summary>
    public int ResolvePaths(ReadOnlySpan<byte> pathTable)
    {
        int unmatched = 0;
        foreach (var path in PathTable.Enumerate(pathTable, Directories))
        {
            if (FilesByHash.TryGetValue(PathHash.Hash(path, HashAlgorithm), out var f))
                f.Path = path;
            else
                unmatched++;
        }
        return unmatched;
    }

    public BundleIndex DeepClone()
    {
        var c = new BundleIndex { PathTableBundle = PathTableBundle, HashAlgorithm = HashAlgorithm };
        foreach (var b in Bundles) c.Bundles.Add(new BundleRecord(b.Name, b.UncompressedSize));
        foreach (var f in Files) c.Files.Add(f.Clone());
        c.Directories.AddRange(Directories);
        return c;
    }

    private static int ReadInt(ReadOnlySpan<byte> s, ref int p)
    {
        Need(s, p, 4);
        int v = BinaryPrimitives.ReadInt32LittleEndian(s[p..]);
        p += 4;
        return v;
    }

    private static void Need(ReadOnlySpan<byte> s, int p, long n)
    {
        if (n < 0 || p + n > s.Length) throw new InvalidDataException("Index payload is truncated.");
    }
}

/// <summary>Encoding of file paths per directory record (see docs/RESEARCH.md §3.3).</summary>
public static class PathTable
{
    public static IEnumerable<string> Enumerate(ReadOnlySpan<byte> table, IReadOnlyList<DirectoryRecord> directories)
    {
        var results = new List<string>();
        var temp = new List<string>();
        foreach (var d in directories)
        {
            if (d.Offset < 0 || d.Size < 0 || (long)d.Offset + d.Size > table.Length)
                throw new InvalidDataException("Directory record points outside the path table.");
            var block = table.Slice(d.Offset, d.Size);
            temp.Clear();
            bool baseMode = false;
            int p = 0;
            while (p <= block.Length - 4)
            {
                int index = BinaryPrimitives.ReadInt32LittleEndian(block[p..]);
                p += 4;
                if (index == 0)
                {
                    baseMode = !baseMode;
                    if (baseMode) temp.Clear();
                    continue;
                }
                int end = block[p..].IndexOf((byte)0);
                if (end < 0) throw new InvalidDataException("Unterminated string in path table.");
                var s = Encoding.UTF8.GetString(block.Slice(p, end));
                p += end + 1;
                index -= 1;
                if (index < temp.Count) s = temp[index] + s;
                if (baseMode) temp.Add(s);
                else results.Add(s);
            }
        }
        return results;
    }

    /// <summary>
    /// Builds a minimal valid path-table block for one directory (no shared prefixes).
    /// Used for synthetic test fixtures; the game's own table is never rewritten.
    /// </summary>
    public static byte[] BuildSimpleBlock(IEnumerable<string> paths)
    {
        using var ms = new MemoryStream();
        Span<byte> b = stackalloc byte[4];
        foreach (var path in paths)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b, 1); // index 1 => temp[0] if present; temp is empty, so used as-is
            ms.Write(b);
            ms.Write(Encoding.UTF8.GetBytes(path));
            ms.WriteByte(0);
        }
        return ms.ToArray();
    }
}
