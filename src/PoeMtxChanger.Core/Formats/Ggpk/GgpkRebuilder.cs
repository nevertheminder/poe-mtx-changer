using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PoeMtxChanger.Core.Formats.Ggpk;

public sealed record GgpkInputFile(string Path, long Length, Func<Stream> Open)
{
    public static GgpkInputFile FromBytes(string path, byte[] bytes) => new(path, bytes.LongLength, () => new MemoryStream(bytes, false));
    public static GgpkInputFile FromDisk(string path, string diskPath) => new(path, new FileInfo(diskPath).Length, () => File.OpenRead(diskPath));
}

/// <summary>Independent out-of-place GGPK builder. Streams payloads; never allocates/free-lists inside the original archive.</summary>
public static class GgpkRebuilder
{
    private sealed class Node(string name)
    {
        public string Name = name;
        public Dictionary<string, Node> Children = new(StringComparer.OrdinalIgnoreCase);
        public GgpkInputFile? File;
        public bool IsDirectory;
        public long Offset;
        public byte[] Hash = [];
    }

    public static uint NameHash(string name)
    {
        var bytes = Encoding.Unicode.GetBytes(name.ToLowerInvariant());
        const uint multiply = 0x5BD1E995;
        uint value = (uint)bytes.Length;
        unchecked
        {
            int p = 0;
            while (p + 4 <= bytes.Length)
            {
                uint block = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p));
                block *= multiply; block ^= block >> 24; block *= multiply;
                value = value * multiply ^ block;
                p += 4;
            }
            uint tail = 0;
            for (int i = 0; p + i < bytes.Length; i++) tail |= (uint)bytes[p + i] << (8 * i);
            if (p != bytes.Length) value = (value ^ tail) * multiply;
            value ^= value >> 13; value *= multiply; return value ^ value >> 15;
        }
    }

    public static void Write(Stream destination, uint version, IEnumerable<GgpkInputFile> files,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default, string rootName = "", IEnumerable<string>? directories = null)
    {
        if (!destination.CanWrite || !destination.CanSeek || destination.Length != 0) throw new ArgumentException("Destination must be an empty seekable output.");
        if (version is not (2 or 3 or 4)) throw new NotSupportedException("Unsupported GGPK version.");
        var root = new Node(rootName) { IsDirectory = true };
        foreach (var directory in directories ?? [])
        {
            if (directory.Length == 0) continue;
            var parent = root;
            foreach (var piece in directory.Replace('\\', '/').Split('/'))
            {
                if (string.IsNullOrWhiteSpace(piece) || piece is "." or ".." || piece.IndexOfAny(['\0', ':']) >= 0)
                    throw new InvalidDataException("Invalid GGPK directory path.");
                if (!parent.Children.TryGetValue(piece, out var child)) parent.Children.Add(piece, child = new Node(piece));
                parent = child;
                parent.IsDirectory = true;
            }
        }
        foreach (var file in files)
        {
            var path = file.Path.Replace('\\', '/');
            var pieces = path.Split('/');
            if (pieces.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.IndexOfAny(['\0', ':']) >= 0) || path.Length > 4096)
                throw new InvalidDataException("Invalid GGPK virtual path.");
            var parent = root;
            for (int i = 0; i < pieces.Length; i++)
            {
                if (parent.File != null) throw new InvalidDataException("FILE/PDIR name collision.");
                if (!parent.Children.TryGetValue(pieces[i], out var child)) parent.Children.Add(pieces[i], child = new Node(pieces[i]));
                parent = child;
                if (i < pieces.Length - 1) parent.IsDirectory = true;
            }
            if (parent.File != null || parent.IsDirectory || parent.Children.Count != 0) throw new InvalidDataException("Duplicate GGPK file path.");
            if (file.Length < 0 || file.Length > uint.MaxValue - 8300L) throw new InvalidDataException("GGPK FILE payload too large.");
            parent.File = file;
        }
        destination.Write(new byte[28]);
        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        var buffer = new byte[1 << 20]; int writtenFiles = 0;
        WriteNode(root);
        destination.Position = 0;
        writer.Write(28u); writer.Write(GgpkArchive.TagGgpk); writer.Write(version); writer.Write(root.Offset); writer.Write(0L);
        destination.Flush();

        void WriteNode(Node node)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] encoded = (version == 4 ? Encoding.UTF32 : Encoding.Unicode).GetBytes(node.Name + "\0");
            int characters = encoded.Length / (version == 4 ? 4 : 2);
            if (node.File is { } file)
            {
                node.Offset = destination.Position;
                writer.Write(checked((uint)(44L + encoded.Length + file.Length)));
                writer.Write(GgpkArchive.TagFile); writer.Write(characters);
                long hashAt = destination.Position; writer.Write(new byte[32]); writer.Write(encoded);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var input = file.Open())
                {
                    long remaining = file.Length;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read == 0) throw new EndOfStreamException("GGPK input file changed or was truncated.");
                        destination.Write(buffer, 0, read); hash.AppendData(buffer, 0, read); remaining -= read;
                    }
                }
                node.Hash = hash.GetHashAndReset();
                long end = destination.Position; destination.Position = hashAt; writer.Write(node.Hash); destination.Position = end;
                if (++writtenFiles % 128 == 0) progress?.Report($"GGPK: files copied {writtenFiles:N0}");
                return;
            }
            var children = node.Children.Values.OrderBy(child => NameHash(child.Name)).ToList();
            if (children.Select(child => NameHash(child.Name)).Distinct().Count() != children.Count) throw new InvalidDataException("GGPK name-hash collision.");
            foreach (var child in children) WriteNode(child);
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                foreach (var child in children) hash.AppendData(child.Hash);
                node.Hash = hash.GetHashAndReset();
            }
            node.Offset = destination.Position;
            writer.Write(checked((uint)(48L + encoded.Length + children.Count * 12L)));
            writer.Write(GgpkArchive.TagPdir); writer.Write(characters); writer.Write(children.Count); writer.Write(node.Hash); writer.Write(encoded);
            foreach (var child in children) { writer.Write(NameHash(child.Name)); writer.Write(child.Offset); }
        }
    }

    public static void Rebuild(string inputPath, string outputPath, IReadOnlyDictionary<string, string> replacementFiles,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Path.GetFullPath(inputPath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("GGPK rebuild requires a different output file.");
        using var input = GgpkArchive.OpenRead(inputPath);
        var replacements = new Dictionary<string, string>(replacementFiles, StringComparer.OrdinalIgnoreCase);
        var sources = new List<GgpkInputFile>();
        foreach (var entry in input.EnumerateFiles())
        {
            if (replacements.Remove(entry.Path, out var replacement)) sources.Add(GgpkInputFile.FromDisk(entry.Path, replacement));
            else sources.Add(new(entry.Path, entry.DataLength, () => input.OpenFile(entry)));
        }
        foreach (var (virtualPath, diskPath) in replacements) sources.Add(GgpkInputFile.FromDisk(virtualPath, diskPath));
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        Write(output, input.Version, sources, progress, cancellationToken, input.RootName, input.EnumerateDirectories().ToArray());
        output.Flush(true);
    }
}
