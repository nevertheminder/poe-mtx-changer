using System.Buffers.Binary;
using PoeMtxChanger.Core.Compression;

namespace PoeMtxChanger.Core.Formats.Bundles;

/// <summary>60-byte bundle header followed by the chunk size table (verified against a real _.index.bin).</summary>
public sealed class BundleHeader
{
    public const int FixedSize = 60;
    public const int DefaultChunkSize = 256 * 1024;

    public int UncompressedSize { get; set; }
    public int CompressedSize { get; set; }
    public int HeadSize { get; set; }
    public OodleCompressor Compressor { get; set; } = OodleCompressor.Mermaid;
    public int Unknown { get; set; } = 1;
    public long UncompressedSizeLong { get; set; }
    public long CompressedSizeLong { get; set; }
    public int ChunkCount { get; set; }
    public int ChunkSize { get; set; } = DefaultChunkSize;
    public int[] ChunkCompressedSizes { get; set; } = [];

    /// <summary>Offset of the first chunk from the start of the bundle (12 + HeadSize).</summary>
    public int DataOffset => 12 + HeadSize;

    public int GetChunkUncompressedSize(int chunk)
        => chunk == ChunkCount - 1 ? UncompressedSize - ChunkSize * (ChunkCount - 1) : ChunkSize;

    public static BundleHeader Read(Stream stream)
    {
        Span<byte> fixedPart = stackalloc byte[FixedSize];
        stream.ReadExactly(fixedPart);
        var h = Parse(fixedPart);
        var table = new byte[checked(h.ChunkCount * 4)];
        stream.ReadExactly(table);
        h.ChunkCompressedSizes = new int[h.ChunkCount];
        for (int i = 0; i < h.ChunkCount; i++)
            h.ChunkCompressedSizes[i] = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i * 4));
        h.Validate();
        return h;
    }

    public static BundleHeader Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < FixedSize)
            throw new InvalidDataException("Bundle is shorter than its header.");
        var h = Parse(data[..FixedSize]);
        if (data.Length < FixedSize + h.ChunkCount * 4L)
            throw new InvalidDataException("Bundle chunk table is truncated.");
        h.ChunkCompressedSizes = new int[h.ChunkCount];
        for (int i = 0; i < h.ChunkCount; i++)
            h.ChunkCompressedSizes[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(FixedSize + i * 4)..]);
        h.Validate();
        return h;
    }

    private static BundleHeader Parse(ReadOnlySpan<byte> b) => new()
    {
        UncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(b[0..]),
        CompressedSize = BinaryPrimitives.ReadInt32LittleEndian(b[4..]),
        HeadSize = BinaryPrimitives.ReadInt32LittleEndian(b[8..]),
        Compressor = (OodleCompressor)BinaryPrimitives.ReadInt32LittleEndian(b[12..]),
        Unknown = BinaryPrimitives.ReadInt32LittleEndian(b[16..]),
        UncompressedSizeLong = BinaryPrimitives.ReadInt64LittleEndian(b[20..]),
        CompressedSizeLong = BinaryPrimitives.ReadInt64LittleEndian(b[28..]),
        ChunkCount = BinaryPrimitives.ReadInt32LittleEndian(b[36..]),
        ChunkSize = BinaryPrimitives.ReadInt32LittleEndian(b[40..]),
    };

    private void Validate()
    {
        if (ChunkCount < 0 || ChunkSize <= 0 || UncompressedSize < 0 || CompressedSize < 0)
            throw new InvalidDataException("Bundle header contains negative sizes.");
        if (HeadSize != 48 + ChunkCount * 4)
            throw new InvalidDataException($"Unexpected bundle head size {HeadSize} for {ChunkCount} chunks.");
        if ((long)ChunkSize * ChunkCount < UncompressedSize || (ChunkCount > 0 && (long)ChunkSize * (ChunkCount - 1) >= UncompressedSize))
            throw new InvalidDataException("Bundle chunk count does not match its uncompressed size.");
        long sum = 0;
        foreach (var s in ChunkCompressedSizes)
        {
            if (s < 0) throw new InvalidDataException("Negative chunk size.");
            sum += s;
        }
        if (sum != CompressedSize)
            throw new InvalidDataException("Chunk sizes do not add up to the compressed size.");
    }

    public void Write(Stream stream)
    {
        Span<byte> b = stackalloc byte[FixedSize];
        b.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(b[0..], UncompressedSize);
        BinaryPrimitives.WriteInt32LittleEndian(b[4..], CompressedSize);
        BinaryPrimitives.WriteInt32LittleEndian(b[8..], HeadSize);
        BinaryPrimitives.WriteInt32LittleEndian(b[12..], (int)Compressor);
        BinaryPrimitives.WriteInt32LittleEndian(b[16..], Unknown);
        BinaryPrimitives.WriteInt64LittleEndian(b[20..], UncompressedSizeLong);
        BinaryPrimitives.WriteInt64LittleEndian(b[28..], CompressedSizeLong);
        BinaryPrimitives.WriteInt32LittleEndian(b[36..], ChunkCount);
        BinaryPrimitives.WriteInt32LittleEndian(b[40..], ChunkSize);
        stream.Write(b);
        Span<byte> n = stackalloc byte[4];
        foreach (var s in ChunkCompressedSizes)
        {
            BinaryPrimitives.WriteInt32LittleEndian(n, s);
            stream.Write(n);
        }
    }
}

/// <summary>Reads and writes complete bundle files through an <see cref="IOodleCodec"/>.</summary>
public static class BundleFile
{
    /// <summary>Decompresses a whole bundle held in memory.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> bundle, IOodleCodec codec)
    {
        var h = BundleHeader.Read(bundle);
        var output = GC.AllocateUninitializedArray<byte>(h.UncompressedSize);
        long src = h.DataOffset;
        for (int i = 0; i < h.ChunkCount; i++)
        {
            int cs = h.ChunkCompressedSizes[i];
            if (src + cs > bundle.Length) throw new InvalidDataException("Bundle data is truncated.");
            codec.Decompress(bundle.Slice((int)src, cs), output.AsSpan(i * h.ChunkSize, h.GetChunkUncompressedSize(i)));
            src += cs;
        }
        return output;
    }

    /// <summary>
    /// Reads <paramref name="length"/> bytes at <paramref name="offset"/> of the uncompressed payload,
    /// decompressing only the chunks that cover the range.
    /// </summary>
    public static byte[] ReadRange(Stream bundle, BundleHeader header, int offset, int length, IOodleCodec codec)
    {
        if (offset < 0 || length < 0 || (long)offset + length > header.UncompressedSize)
            throw new ArgumentOutOfRangeException(nameof(offset), "Range is outside the bundle.");
        var result = GC.AllocateUninitializedArray<byte>(length);
        if (length == 0) return result;

        int first = offset / header.ChunkSize;
        int last = (offset + length - 1) / header.ChunkSize;
        long pos = header.DataOffset;
        for (int i = 0; i < first; i++) pos += header.ChunkCompressedSizes[i];
        bundle.Position = pos;

        var chunk = new byte[header.ChunkSize];
        int written = 0;
        for (int i = first; i <= last; i++)
        {
            var compressed = new byte[header.ChunkCompressedSizes[i]];
            bundle.ReadExactly(compressed);
            int rawSize = header.GetChunkUncompressedSize(i);
            codec.Decompress(compressed, chunk.AsSpan(0, rawSize));
            int chunkStart = i * header.ChunkSize;
            int from = Math.Max(offset, chunkStart) - chunkStart;
            int to = Math.Min(offset + length, chunkStart + rawSize) - chunkStart;
            chunk.AsSpan(from, to - from).CopyTo(result.AsSpan(written));
            written += to - from;
        }
        return result;
    }

    /// <summary>Builds a bundle (header + chunks) for <paramref name="payload"/>.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> payload, IOodleCodec codec, OodleCompressor compressor,
        OodleCompressionLevel level, int chunkSize = BundleHeader.DefaultChunkSize)
    {
        int chunkCount = (payload.Length + chunkSize - 1) / chunkSize;
        var sizes = new int[chunkCount];
        using var data = new MemoryStream();
        var buffer = new byte[codec.GetCompressedBufferSize(chunkSize)];
        for (int i = 0; i < chunkCount; i++)
        {
            int start = i * chunkSize;
            int len = Math.Min(chunkSize, payload.Length - start);
            int n = codec.Compress(compressor, level, payload.Slice(start, len), buffer);
            sizes[i] = n;
            data.Write(buffer, 0, n);
        }

        var header = new BundleHeader
        {
            UncompressedSize = payload.Length,
            CompressedSize = checked((int)data.Length),
            HeadSize = 48 + chunkCount * 4,
            Compressor = compressor,
            Unknown = 1,
            UncompressedSizeLong = payload.Length,
            CompressedSizeLong = data.Length,
            ChunkCount = chunkCount,
            ChunkSize = chunkSize,
            ChunkCompressedSizes = sizes,
        };
        using var output = new MemoryStream(checked((int)(BundleHeader.FixedSize + chunkCount * 4 + data.Length)));
        header.Write(output);
        data.Position = 0;
        data.CopyTo(output);
        return output.ToArray();
    }
}
