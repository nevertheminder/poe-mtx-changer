using System.Buffers.Binary;
using System.Text;

namespace PoeMtxChanger.Core.Formats.Bundles;

public enum PathHashAlgorithm
{
    /// <summary>MurmurHash64A, seed 0x1337B33F, lower-cased UTF-8 (since game patch 3.21.2).</summary>
    Murmur64A,
    /// <summary>FNV-1a 64 with "++" suffix (before 3.21.2).</summary>
    Fnv1a64Legacy,
}

/// <summary>Path hashing used by the bundle index to identify files and directories.</summary>
public static class PathHash
{
    public const ulong MurmurSeed = 0x1337B33FUL;

    /// <summary>Hash of the root directory (empty path) under <see cref="PathHashAlgorithm.Murmur64A"/>.</summary>
    public const ulong MurmurRootHash = 0xF42A94E69CFF42FEUL;

    /// <summary>Hash of the root directory under <see cref="PathHashAlgorithm.Fnv1a64Legacy"/>.</summary>
    public const ulong FnvRootHash = 0x07E47507B4A92E53UL;

    public static PathHashAlgorithm? DetectFromRootHash(ulong rootHash) => rootHash switch
    {
        MurmurRootHash => PathHashAlgorithm.Murmur64A,
        FnvRootHash => PathHashAlgorithm.Fnv1a64Legacy,
        _ => null,
    };

    public static ulong Hash(string path, PathHashAlgorithm algorithm)
    {
        if (algorithm == PathHashAlgorithm.Murmur64A)
        {
            var bytes = Encoding.UTF8.GetBytes(path.ToLowerInvariant());
            return Murmur64A(TrimTrailingSlash(bytes));
        }
        return Fnv1a64Legacy(Encoding.UTF8.GetBytes(path));
    }

    private static ReadOnlySpan<byte> TrimTrailingSlash(ReadOnlySpan<byte> s)
        => s.Length > 0 && s[^1] == (byte)'/' ? s[..^1] : s;

    /// <summary>Standard MurmurHash64A (Austin Appleby), little-endian block reads.</summary>
    public static ulong Murmur64A(ReadOnlySpan<byte> data, ulong seed = MurmurSeed)
    {
        const ulong m = 0xC6A4A7935BD1E995UL;
        const int r = 47;
        unchecked
        {
            ulong h = seed ^ ((ulong)data.Length * m);
            int blocks = data.Length / 8;
            for (int i = 0; i < blocks; i++)
            {
                ulong k = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i * 8, 8));
                k *= m;
                k ^= k >> r;
                k *= m;
                h ^= k;
                h *= m;
            }

            var tail = data[(blocks * 8)..];
            switch (tail.Length)
            {
                case 7: h ^= (ulong)tail[6] << 48; goto case 6;
                case 6: h ^= (ulong)tail[5] << 40; goto case 5;
                case 5: h ^= (ulong)tail[4] << 32; goto case 4;
                case 4: h ^= (ulong)tail[3] << 24; goto case 3;
                case 3: h ^= (ulong)tail[2] << 16; goto case 2;
                case 2: h ^= (ulong)tail[1] << 8; goto case 1;
                case 1: h ^= tail[0]; h *= m; break;
            }

            h ^= h >> r;
            h *= m;
            h ^= h >> r;
            return h;
        }
    }

    /// <summary>
    /// Legacy FNV-1a 64 path hash: files are lower-cased ASCII-wise, directories (trailing '/') keep case
    /// and lose the slash; both get a "++" suffix.
    /// </summary>
    public static ulong Fnv1a64Legacy(ReadOnlySpan<byte> utf8)
    {
        const ulong prime = 0x100000001B3UL;
        ulong hash = 0xCBF29CE484222325UL;
        unchecked
        {
            if (utf8.Length > 0 && utf8[^1] == (byte)'/')
            {
                foreach (var b in utf8[..^1])
                    hash = (hash ^ b) * prime;
            }
            else
            {
                foreach (var b in utf8)
                {
                    ulong ch = b is >= (byte)'A' and <= (byte)'Z' ? (ulong)(b + 32) : b;
                    hash = (hash ^ ch) * prime;
                }
            }
            hash = (hash ^ '+') * prime;
            hash = (hash ^ '+') * prime;
            return hash;
        }
    }
}
