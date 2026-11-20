using System.Buffers.Binary;
using System.Text;

namespace PoeMtxChanger.Core.Formats.Ggpk;

public sealed record GgpkFileEntry(string Path, long RecordOffset, long DataOffset, int DataLength);

/// <summary>
/// Bounded Content.ggpk reader. GgpkRebuilder provides streaming out-of-place writing.
/// </summary>
public sealed class GgpkArchive : IDisposable
{
    public const uint TagGgpk = 0x4B504747; // "GGPK"
    public const uint TagPdir = 0x52494450; // "PDIR"
    public const uint TagFile = 0x454C4946; // "FILE"
    public const uint TagFree = 0x45455246; // "FREE"

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private Dictionary<string, GgpkFileEntry>? _bundleFiles;

    public uint Version { get; }
    public long RootDirectoryOffset { get; }
    public long FirstFreeOffset { get; }
    public string RootName => ReadDirectory(RootDirectoryOffset).Name;

    public GgpkArchive(Stream stream, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _stream.Position = 0;
        var (len, tag) = ReadRecordHeader(0);
        if (tag != TagGgpk || len < 28) throw new InvalidDataException("Not a GGPK file.");
        Span<byte> b = stackalloc byte[20];
        _stream.ReadExactly(b);
        Version = BinaryPrimitives.ReadUInt32LittleEndian(b);
        RootDirectoryOffset = BinaryPrimitives.ReadInt64LittleEndian(b[4..]);
        FirstFreeOffset = BinaryPrimitives.ReadInt64LittleEndian(b[12..]);
        if (Version is not (2 or 3 or 4)) throw new InvalidDataException($"Unsupported GGPK version {Version}.");
    }

    public static GgpkArchive OpenRead(string path)
        => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess));

    private (uint Length, uint Tag) ReadRecordHeader(long offset)
    {
        if (offset < 0 || offset > _stream.Length - 8) throw new InvalidDataException("GGPK record offset out of bounds.");
        _stream.Position = offset;
        Span<byte> h = stackalloc byte[8];
        _stream.ReadExactly(h);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(h);
        if (length < 8 || length > _stream.Length - offset) throw new InvalidDataException("GGPK record length out of bounds.");
        return (length, BinaryPrimitives.ReadUInt32LittleEndian(h[4..]));
    }

    private string ReadName(int lengthWithNul)
    {
        int chars = lengthWithNul - 1;
        if (chars < 0 || chars > 4096) throw new InvalidDataException("Invalid record name length.");
        int width = Version == 4 ? 4 : 2;
        var buf = new byte[(chars + 1) * width];
        _stream.ReadExactly(buf);
        if (buf.AsSpan(chars * width, width).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("GGPK name is not terminated.");
        return Version == 4
            ? Encoding.UTF32.GetString(buf, 0, chars * 4)
            : Encoding.Unicode.GetString(buf, 0, chars * 2);
    }

    private (string Name, List<long> Children) ReadDirectory(long offset)
    {
        var (recordLength, tag) = ReadRecordHeader(offset);
        if (tag != TagPdir || recordLength < 48) throw new InvalidDataException($"Expected PDIR at {offset}.");
        Span<byte> b = stackalloc byte[8];
        _stream.ReadExactly(b);
        int nameLen = BinaryPrimitives.ReadInt32LittleEndian(b);
        int count = BinaryPrimitives.ReadInt32LittleEndian(b[4..]);
        if (count < 0 || count > (recordLength - 48L) / 12) throw new InvalidDataException("Invalid directory entry count.");
        _stream.Seek(32, SeekOrigin.Current); // SHA-256
        var name = ReadName(nameLen);
        if (_stream.Position + count * 12L != offset + recordLength) throw new InvalidDataException("Directory record size mismatch.");
        var entries = new byte[count * 12];
        _stream.ReadExactly(entries);
        var children = new List<long>(count);
        for (int i = 0; i < count; i++)
            children.Add(BinaryPrimitives.ReadInt64LittleEndian(entries.AsSpan(i * 12 + 4)));
        return (name, children);
    }

    /// <summary>Enumerates every FILE record below the directory at <paramref name="prefix"/> ("" = root).</summary>
    public IEnumerable<GgpkFileEntry> EnumerateFiles(string prefix = "")
    {
        long start = RootDirectoryOffset;
        string basePath = "";
        foreach (var part in prefix.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var (_, children) = ReadDirectory(start);
            long? next = null;
            foreach (var c in children)
            {
                var (_, tag) = ReadRecordHeader(c);
                if (tag != TagPdir) continue;
                if (string.Equals(ReadDirectory(c).Name, part, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
            }
            if (next is null) yield break;
            start = next.Value;
            basePath += part + "/";
        }

        var stack = new Stack<(long Offset, string Path)>();
        var visited = new HashSet<long>();
        stack.Push((start, basePath));
        while (stack.Count > 0)
        {
            var (off, path) = stack.Pop();
            if (!visited.Add(off)) throw new InvalidDataException("Cyclic or duplicated GGPK directory.");
            var (_, children) = ReadDirectory(off);
            foreach (var c in children)
            {
                var (len, tag) = ReadRecordHeader(c);
                if (tag == TagPdir)
                {
                    stack.Push((c, path + ReadDirectory(c).Name + "/"));
                }
                else if (tag == TagFile)
                {
                    if (len < 44) throw new InvalidDataException("Truncated GGPK FILE record.");
                    Span<byte> nb = stackalloc byte[4];
                    _stream.ReadExactly(nb);
                    int nameLen = BinaryPrimitives.ReadInt32LittleEndian(nb);
                    if (nameLen < 1 || 44L + nameLen * (Version == 4 ? 4L : 2L) > len)
                        throw new InvalidDataException("GGPK FILE name exceeds its record.");
                    _stream.Seek(32, SeekOrigin.Current);
                    var name = ReadName(nameLen);
                    long dataOffset = _stream.Position;
                    yield return new GgpkFileEntry(path + name, c, dataOffset, checked((int)(c + len - dataOffset)));
                }
            }
        }
    }

    public IEnumerable<string> EnumerateDirectories()
    {
        var stack = new Stack<(long Offset, string Path)>();
        var seen = new HashSet<long>();
        stack.Push((RootDirectoryOffset, ""));
        while (stack.TryPop(out var item))
        {
            if (!seen.Add(item.Offset)) throw new InvalidDataException("Cyclic GGPK directory.");
            yield return item.Path;
            foreach (var offset in ReadDirectory(item.Offset).Children)
                if (ReadRecordHeader(offset).Tag == TagPdir)
                {
                    string name = ReadDirectory(offset).Name;
                    stack.Push((offset, item.Path.Length == 0 ? name : item.Path + "/" + name));
                }
        }
    }

    public byte[] ReadFile(GgpkFileEntry entry)
    {
        var data = new byte[entry.DataLength];
        _stream.Position = entry.DataOffset;
        _stream.ReadExactly(data);
        return data;
    }

    public Stream OpenFile(GgpkFileEntry entry) => new SubStream(_stream, entry.DataOffset, entry.DataLength);

    /// <summary>Lookup table of everything under Bundles2/ (built once).</summary>
    public IReadOnlyDictionary<string, GgpkFileEntry> BundleFiles
        => _bundleFiles ??= EnumerateFiles("Bundles2").ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (!_leaveOpen) _stream.Dispose();
    }
}

/// <summary>Read-only window over part of a seekable stream. Not thread-safe (shares the parent position).</summary>
internal sealed class SubStream(Stream parent, long start, long length) : Stream
{
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => _position = Math.Clamp(value, 0, length); }
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = (int)Math.Min(buffer.Length, length - _position);
        if (n <= 0) return 0;
        parent.Position = start + _position;
        int read = parent.Read(buffer[..n]);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => length + offset,
    };

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
