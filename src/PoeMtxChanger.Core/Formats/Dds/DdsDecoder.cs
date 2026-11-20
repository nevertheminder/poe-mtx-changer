using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace PoeMtxChanger.Core.Formats.Dds;

/// <summary>Top mip level as 32-bit BGRA (premultiplied = false).</summary>
public sealed record DecodedImage(int Width, int Height, byte[] Bgra, string Format);

public sealed class DdsNotSupportedException(string message) : NotSupportedException(message);

/// <summary>
/// Minimal DDS reader for previews: BC1/BC2/BC3/BC4/BC5 and uncompressed 32/24-bit, legacy and DX10 headers.
/// Handles PoE's "*path" texture redirects and Brotli-wrapped DDS. BC7 uses BCnEncoder.NET; BC6H is unsupported.
/// </summary>
public static class DdsDecoder
{
    private const uint Magic = 0x20534444; // "DDS "

    /// <param name="resolve">Reads another game resource, used to follow "*path" redirects.</param>
    public static DecodedImage Decode(byte[] data, Func<string, byte[]?>? resolve = null)
    {
        for (int hop = 0; hop < 4; hop++)
        {
            if (data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic) return DecodeDds(data);
            if (data.Length > 1 && data[0] == (byte)'*')
            {
                if (resolve is null) throw new DdsNotSupportedException("Texture is a redirect and no resolver was given.");
                var target = Encoding.UTF8.GetString(data, 1, data.Length - 1).Trim('\0', ' ', '\r', '\n');
                data = resolve(target) ?? throw new DdsNotSupportedException($"Redirect target not found: {target}");
                continue;
            }
            if (TryBrotli(data, out var unpacked)) { data = unpacked; continue; }
            break;
        }
        throw new DdsNotSupportedException("Not a DDS texture (unknown wrapper).");
    }

    private static bool TryBrotli(byte[] data, out byte[] result)
    {
        result = [];
        if (data.Length < 8) return false;
        int size = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (size <= 0 || size > 256 * 1024 * 1024) return false;
        var buf = new byte[size];
        if (!BrotliDecoder.TryDecompress(data.AsSpan(4), buf, out int written) || written != size) return false;
        result = buf;
        return true;
    }

    private static DecodedImage DecodeDds(byte[] d)
    {
        if (d.Length < 128) throw new InvalidDataException("DDS header truncated.");
        int height = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(12));
        int width = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(16));
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) throw new InvalidDataException("Invalid DDS dimensions.");
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(80));
        string fourCC = Encoding.ASCII.GetString(d, 84, 4);
        int bitCount = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(88));
        uint rMask = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(92));
        uint gMask = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(96));
        uint bMask = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(100));
        uint aMask = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(104));
        int offset = 128;

        string format;
        if ((pfFlags & 0x4) != 0) // DDPF_FOURCC
        {
            format = fourCC;
            if (fourCC == "DX10")
            {
                if (d.Length < 148) throw new InvalidDataException("DX10 header truncated.");
                uint dxgi = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(128));
                offset = 148;
                format = dxgi switch
                {
                    70 or 71 or 72 => "DXT1",
                    73 or 74 or 75 => "DXT3",
                    76 or 77 or 78 => "DXT5",
                    79 or 80 or 81 => "BC4",
                    82 or 83 or 84 => "BC5",
                    28 or 29 => "RGBA8",
                    87 or 91 => "BGRA8",
                    88 or 93 => "BGRX8",
                    94 or 95 or 96 => throw new DdsNotSupportedException("BC6H textures are not supported."),
                    97 or 98 or 99 => "BC7",
                    _ => throw new DdsNotSupportedException($"DXGI format {dxgi} is not supported."),
                };
            }
            format = format switch { "DXT2" => "DXT3", "DXT4" => "DXT5", "ATI1" or "BC4U" => "BC4", "ATI2" or "BC5U" => "BC5", _ => format };
        }
        else if ((pfFlags & 0x40) != 0 && bitCount is 32 or 24) format = "MASK" + bitCount;
        else throw new DdsNotSupportedException($"Pixel format (flags 0x{pfFlags:X}) is not supported.");

        var px = new byte[width * height * 4];
        var src = d.AsSpan(offset);
        switch (format)
        {
            case "BC7":
                int blockBytes = checked(((width + 3) / 4) * ((height + 3) / 4) * 16);
                if (src.Length < blockBytes) throw new InvalidDataException("Truncated BC7 top mip level.");
                var rgba = new BcDecoder().DecodeRaw(src[..blockBytes].ToArray(),width,height,CompressionFormat.Bc7);
                for (int i=0;i<rgba.Length;i++) { px[i*4]=rgba[i].b;px[i*4+1]=rgba[i].g;px[i*4+2]=rgba[i].r;px[i*4+3]=rgba[i].a; }
                break;
            case "DXT1": Blocks(src, width, height, 8, px, (b, o) => DecodeColor(b, o, allowAlpha: true)); break;
            case "DXT3": Blocks(src, width, height, 16, px, DecodeBc2); break;
            case "DXT5": Blocks(src, width, height, 16, px, DecodeBc3); break;
            case "BC4": Blocks(src, width, height, 8, px, DecodeBc4); break;
            case "BC5": Blocks(src, width, height, 16, px, DecodeBc5); break;
            case "RGBA8": Linear(src, width, height, 4, px, (s, o) => { o[0] = s[2]; o[1] = s[1]; o[2] = s[0]; o[3] = s[3]; }); break;
            case "BGRA8": Linear(src, width, height, 4, px, (s, o) => s[..4].CopyTo(o)); break;
            case "BGRX8": Linear(src, width, height, 4, px, (s, o) => { s[..3].CopyTo(o); o[3] = 255; }); break;
            case "MASK32":
            case "MASK24":
                int bpp = bitCount / 8;
                Linear(src, width, height, bpp, px, (s, o) =>
                {
                    uint v = bpp == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(s) : (uint)(s[0] | s[1] << 8 | s[2] << 16);
                    o[0] = Channel(v, bMask); o[1] = Channel(v, gMask); o[2] = Channel(v, rMask);
                    o[3] = aMask == 0 ? (byte)255 : Channel(v, aMask);
                });
                break;
            default: throw new DdsNotSupportedException($"Format {format} is not supported.");
        }
        return new DecodedImage(width, height, px, format);
    }

    private static byte Channel(uint v, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return (byte)(((v & mask) >> shift) * 255 / max);
    }

    private delegate void PixelCopy(ReadOnlySpan<byte> src, Span<byte> dst);
    private delegate void BlockDecoder(ReadOnlySpan<byte> block, Span<byte> out64); // 16 BGRA pixels

    private static void Linear(ReadOnlySpan<byte> src, int w, int h, int bpp, byte[] px, PixelCopy copy)
    {
        if (src.Length < (long)w * h * bpp) throw new InvalidDataException("DDS pixel data truncated.");
        for (int i = 0; i < w * h; i++) copy(src.Slice(i * bpp, bpp), px.AsSpan(i * 4, 4));
    }

    private static void Blocks(ReadOnlySpan<byte> src, int w, int h, int blockSize, byte[] px, BlockDecoder decode)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        if (src.Length < (long)bw * bh * blockSize) throw new InvalidDataException("DDS block data truncated.");
        Span<byte> tile = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                decode(src.Slice((by * bw + bx) * blockSize, blockSize), tile);
                for (int y = 0; y < 4; y++)
                {
                    int py = by * 4 + y;
                    if (py >= h) break;
                    for (int x = 0; x < 4; x++)
                    {
                        int pxX = bx * 4 + x;
                        if (pxX >= w) break;
                        tile.Slice((y * 4 + x) * 4, 4).CopyTo(px.AsSpan((py * w + pxX) * 4, 4));
                    }
                }
            }
    }

    private static void Rgb565(ushort c, Span<int> rgb)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        rgb[0] = (r << 3) | (r >> 2);
        rgb[1] = (g << 2) | (g >> 4);
        rgb[2] = (b << 3) | (b >> 2);
    }

    /// <summary>BC1 colour block. With <paramref name="allowAlpha"/>, c0 ≤ c1 selects 3 colours + transparent.</summary>
    internal static void DecodeColor(ReadOnlySpan<byte> b, Span<byte> o, bool allowAlpha)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(b), c1 = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]);
        uint idx = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        Span<int> p0 = stackalloc int[3], p1 = stackalloc int[3];
        Rgb565(c0, p0);
        Rgb565(c1, p1);
        Span<int> pal = stackalloc int[16]; // 4 colours × (r,g,b,a)
        for (int ch = 0; ch < 3; ch++)
        {
            pal[ch] = p0[ch];
            pal[4 + ch] = p1[ch];
            if (c0 > c1 || !allowAlpha)
            {
                pal[8 + ch] = (2 * p0[ch] + p1[ch]) / 3;
                pal[12 + ch] = (p0[ch] + 2 * p1[ch]) / 3;
            }
            else
            {
                pal[8 + ch] = (p0[ch] + p1[ch]) / 2;
                pal[12 + ch] = 0;
            }
        }
        pal[3] = pal[7] = pal[11] = 255;
        pal[15] = c0 > c1 || !allowAlpha ? 255 : 0;
        for (int i = 0; i < 16; i++)
        {
            int k = (int)((idx >> (2 * i)) & 3) * 4;
            o[i * 4 + 0] = (byte)pal[k + 2]; // B
            o[i * 4 + 1] = (byte)pal[k + 1]; // G
            o[i * 4 + 2] = (byte)pal[k + 0]; // R
            o[i * 4 + 3] = (byte)pal[k + 3];
        }
    }

    /// <summary>BC3/BC4 interpolated 8-bit channel block → 16 values.</summary>
    internal static void DecodeAlphaChannel(ReadOnlySpan<byte> b, Span<byte> values)
    {
        int a0 = b[0], a1 = b[1];
        Span<int> pal = stackalloc int[8];
        pal[0] = a0; pal[1] = a1;
        if (a0 > a1) for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else
        {
            for (int i = 1; i < 5; i++) pal[i + 1] = ((5 - i) * a0 + i * a1) / 5;
            pal[6] = 0; pal[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) values[i] = (byte)pal[(int)((bits >> (3 * i)) & 7)];
    }

    private static void DecodeBc2(ReadOnlySpan<byte> b, Span<byte> o)
    {
        DecodeColor(b[8..], o, allowAlpha: false);
        for (int i = 0; i < 16; i++)
        {
            int nibble = (b[i / 2] >> (4 * (i & 1))) & 0xF;
            o[i * 4 + 3] = (byte)(nibble * 17);
        }
    }

    private static void DecodeBc3(ReadOnlySpan<byte> b, Span<byte> o)
    {
        DecodeColor(b[8..], o, allowAlpha: false);
        Span<byte> a = stackalloc byte[16];
        DecodeAlphaChannel(b, a);
        for (int i = 0; i < 16; i++) o[i * 4 + 3] = a[i];
    }

    private static void DecodeBc4(ReadOnlySpan<byte> b, Span<byte> o)
    {
        Span<byte> r = stackalloc byte[16];
        DecodeAlphaChannel(b, r);
        for (int i = 0; i < 16; i++) { o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = r[i]; o[i * 4 + 3] = 255; }
    }

    private static void DecodeBc5(ReadOnlySpan<byte> b, Span<byte> o)
    {
        Span<byte> r = stackalloc byte[16], g = stackalloc byte[16];
        DecodeAlphaChannel(b, r);
        DecodeAlphaChannel(b[8..], g);
        for (int i = 0; i < 16; i++) { o[i * 4] = 0; o[i * 4 + 1] = g[i]; o[i * 4 + 2] = r[i]; o[i * 4 + 3] = 255; }
    }
}
