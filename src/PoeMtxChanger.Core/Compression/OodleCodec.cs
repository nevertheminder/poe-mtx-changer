using System.Runtime.InteropServices;

namespace PoeMtxChanger.Core.Compression;

/// <summary>Oodle compressor identifiers as stored in the bundle header.</summary>
public enum OodleCompressor
{
    None = 3,
    Kraken = 8,
    Mermaid = 9,
    Selkie = 11,
    Hydra = 12,
    Leviathan = 13,
}

public enum OodleCompressionLevel
{
    None = 0,
    SuperFast = 1,
    VeryFast = 2,
    Fast = 3,
    Normal = 4,
    Optimal1 = 5,
    Optimal2 = 6,
    Optimal3 = 7,
    Optimal4 = 8,
    Optimal5 = 9,
}

/// <summary>
/// Chunk codec used by bundle files. The real implementation needs a user-supplied
/// Oodle library (proprietary, never redistributed with this application).
/// </summary>
public interface IOodleCodec
{
    bool IsAvailable { get; }

    /// <summary>Human readable description (library path, or why it is unavailable).</summary>
    string Description { get; }

    /// <summary>Decompresses <paramref name="source"/> into exactly <paramref name="destination"/>.Length bytes.</summary>
    void Decompress(ReadOnlySpan<byte> source, Span<byte> destination);

    /// <summary>Upper bound for the compressed size of <paramref name="rawSize"/> bytes.</summary>
    int GetCompressedBufferSize(int rawSize);

    /// <summary>Compresses into <paramref name="destination"/> and returns the number of bytes written.</summary>
    int Compress(OodleCompressor compressor, OodleCompressionLevel level, ReadOnlySpan<byte> source, Span<byte> destination);
}

public sealed class OodleUnavailableException(string message) : InvalidOperationException(message);

/// <summary>Placeholder used when no Oodle library has been configured.</summary>
public sealed class UnavailableOodleCodec(string reason) : IOodleCodec
{
    public bool IsAvailable => false;
    public string Description => reason;
    public void Decompress(ReadOnlySpan<byte> source, Span<byte> destination) => throw new OodleUnavailableException(reason);
    public int GetCompressedBufferSize(int rawSize) => throw new OodleUnavailableException(reason);
    public int Compress(OodleCompressor compressor, OodleCompressionLevel level, ReadOnlySpan<byte> source, Span<byte> destination)
        => throw new OodleUnavailableException(reason);
}

/// <summary>
/// Binds to an Oodle 2.x core library (oo2core_*_win64.dll) chosen by the user.
/// Loading a native library runs its code, so only point this at a DLL you trust and are licensed to use.
/// </summary>
public sealed unsafe class NativeOodleCodec : IOodleCodec, IDisposable
{
    private readonly nint _handle;
    private readonly delegate* unmanaged<byte*, nint, byte*, nint, int, int, int, byte*, nint, void*, void*, void*, nint, int, nint> _decompress;
    private readonly delegate* unmanaged<int, byte*, nint, byte*, int, void*, void*, void*, void*, nint, nint> _compress;

    private NativeOodleCodec(string path, nint handle)
    {
        _handle = handle;
        Description = path;
        _decompress = (delegate* unmanaged<byte*, nint, byte*, nint, int, int, int, byte*, nint, void*, void*, void*, nint, int, nint>)
            NativeLibrary.GetExport(handle, "OodleLZ_Decompress");
        _compress = NativeLibrary.TryGetExport(handle, "OodleLZ_Compress", out var c)
            ? (delegate* unmanaged<int, byte*, nint, byte*, int, void*, void*, void*, void*, nint, nint>)c
            : null;
    }

    public bool IsAvailable => true;
    public string Description { get; }
    public bool CanCompress => _compress != null;

    /// <summary>Loads the library. Returns an <see cref="UnavailableOodleCodec"/> describing the failure instead of throwing.</summary>
    public static IOodleCodec TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new UnavailableOodleCodec("No Oodle library configured (Settings → Oodle library).");
        if (!File.Exists(path))
            return new UnavailableOodleCodec($"Oodle library not found: {path}");
        if (!Environment.Is64BitProcess)
            return new UnavailableOodleCodec("A 64-bit process is required for oo2core_*_win64.dll.");
        try
        {
            var handle = NativeLibrary.Load(path);
            if (!NativeLibrary.TryGetExport(handle, "OodleLZ_Decompress", out _))
            {
                NativeLibrary.Free(handle);
                return new UnavailableOodleCodec($"{Path.GetFileName(path)} does not export OodleLZ_Decompress.");
            }
            return new NativeOodleCodec(path, handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return new UnavailableOodleCodec($"Failed to load {path}: {ex.Message}");
        }
    }

    public void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        nint written;
        fixed (byte* s = source)
        fixed (byte* d = destination)
        {
            // fuzzSafe=1, checkCRC=0, verbosity=0, no dictionary/callback/decoder memory, threadPhase=3 (all)
            written = _decompress(s, source.Length, d, destination.Length, 1, 0, 0, null, 0, null, null, null, 0, 3);
        }
        if (written != destination.Length)
            throw new InvalidDataException($"Oodle decompression produced {written} bytes, expected {destination.Length}.");
    }

    public int GetCompressedBufferSize(int rawSize)
    {
        // Oodle's documented bound is roughly raw + 274 bytes per 256 KiB block; add generous slack
        // instead of calling OodleLZ_GetCompressedBufferSizeNeeded, whose signature changed between versions.
        long blocks = (rawSize + 0x3FFFFL) / 0x40000L;
        return checked((int)(rawSize + 274L * Math.Max(1, blocks) + 0x10000L));
    }

    public int Compress(OodleCompressor compressor, OodleCompressionLevel level, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (_compress == null)
            throw new OodleUnavailableException($"{Path.GetFileName(Description)} does not export OodleLZ_Compress.");
        if (destination.Length < GetCompressedBufferSize(source.Length))
            throw new ArgumentException("Destination buffer too small.", nameof(destination));
        nint written;
        fixed (byte* s = source)
        fixed (byte* d = destination)
        {
            written = _compress((int)compressor, s, source.Length, d, (int)level, null, null, null, null, 0);
        }
        if (written <= 0)
            throw new InvalidDataException("Oodle compression failed.");
        return (int)written;
    }

    public void Dispose() => NativeLibrary.Free(_handle);
}
