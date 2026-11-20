using PoeMtxChanger.Core.Formats.Bundles;
using PoeMtxChanger.Core.Util;

namespace PoeMtxChanger.Core.Installation;

/// <summary>Facts about the index that can be read without Oodle (header only) — used by the dashboard.</summary>
public sealed record IndexHeaderInfo(long FileSize, DateTime LastWriteUtc, BundleHeader Header, string Sha256);

public static class IndexInspector
{
    public static IndexHeaderInfo Inspect(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        var header = BundleHeader.Read(stream);
        if (header.DataOffset + (long)header.CompressedSize != bytes.LongLength)
            throw new InvalidDataException("Index size does not match its header.");
        return new(bytes.LongLength, DateTime.MinValue, header, FileUtil.Sha256(bytes));
    }
    public static IndexHeaderInfo Inspect(string indexPath, bool computeHash = true)
    {
        var fi = new FileInfo(indexPath);
        using var fs = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = BundleHeader.Read(fs);
        long expected = header.DataOffset + (long)header.CompressedSize;
        if (expected != fi.Length)
            throw new InvalidDataException($"Index size {fi.Length} does not match header ({expected}).");
        var sha = computeHash ? FileUtil.Sha256OfFile(indexPath) : "";
        return new IndexHeaderInfo(fi.Length, fi.LastWriteTimeUtc, header, sha);
    }
}
