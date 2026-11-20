using System.Text;
using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Formats.Bundles;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Tests;

/// <summary>
/// TEST-ONLY codec: "compressed" chunk == raw chunk. Lets the bundle/index/patch pipeline be tested
/// without the proprietary Oodle library. Files produced with it are NOT readable by the game.
/// </summary>
public sealed class PassthroughTestCodec : IOodleCodec
{
    public bool IsAvailable => true;
    public string Description => "test passthrough codec";
    public int CompressCalls { get; private set; }

    public void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length != destination.Length) throw new InvalidDataException("passthrough size mismatch");
        source.CopyTo(destination);
    }

    public int GetCompressedBufferSize(int rawSize) => rawSize + 16;

    public int Compress(OodleCompressor compressor, OodleCompressionLevel level, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        CompressCalls++;
        source.CopyTo(destination);
        return source.Length;
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pmc-tests-" + Guid.NewGuid().ToString("N")[..10]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Builds a fake Steam-style Bundles2 folder (index + bundles) from a set of virtual files.</summary>
public static class SyntheticGame
{
    public static readonly PassthroughTestCodec Codec = new();

    public static byte[] Utf16(string text) => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)];

    /// <param name="files">virtual path → content</param>
    /// <param name="chunkSize">small chunk size forces multi-chunk bundles</param>
    public static DirectoryBundleStorage Create(string gameRoot, IReadOnlyDictionary<string, byte[]> files, int chunkSize = 64)
    {
        var bundles2 = System.IO.Path.Combine(gameRoot, "Bundles2");
        Directory.CreateDirectory(bundles2);
        var index = new BundleIndex();

        // Split into two bundles to exercise bundle indices; one lives in a sub-folder.
        var groups = files.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select((k, i) => (k, i)).GroupBy(t => t.i % 2 == 0 ? "Folder/Bundle_A" : "Bundle_B");
        foreach (var g in groups)
        {
            using var payload = new MemoryStream();
            int bi = index.AddBundle(g.Key, 0);
            foreach (var (path, _) in g)
            {
                var data = files[path];
                index.Files.Add(new FileRecord(PathHash.Hash(path, PathHashAlgorithm.Murmur64A), bi, (int)payload.Position, data.Length));
                payload.Write(data);
            }
            index.Bundles[bi].UncompressedSize = (int)payload.Length;
            var bytes = BundleFile.Compress(payload.ToArray(), Codec, OodleCompressor.Mermaid, OodleCompressionLevel.Normal, chunkSize);
            var dst = System.IO.Path.Combine(bundles2, g.Key.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".bundle.bin");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
            File.WriteAllBytes(dst, bytes);
        }

        var table = PathTable.BuildSimpleBlock(files.Keys);
        index.Directories.Add(new DirectoryRecord(PathHash.MurmurRootHash, 0, table.Length, table.Length));
        index.PathTableBundle = BundleFile.Compress(table, Codec, OodleCompressor.Mermaid, OodleCompressionLevel.Normal, chunkSize);
        var indexBytes = BundleFile.Compress(index.Serialize(), Codec, OodleCompressor.Mermaid, OodleCompressionLevel.Normal, chunkSize);
        File.WriteAllBytes(System.IO.Path.Combine(bundles2, DirectoryBundleStorage.IndexFileName), indexBytes);
        return new DirectoryBundleStorage(bundles2);
    }

    public static Dictionary<string, byte[]> SampleFiles() => new()
    {
        ["Metadata/Effects/Misc/portal/townportal.aoc"] = Utf16("version 2\r\nClientAnimationController\r\n{\r\n\tskeleton = \"Art/Models/Effects/portal/portal.ast\"\r\n}\r\n"),
        ["Metadata/Effects/Microtransactions/portals/emberportal/emberportal.aoc"] = Utf16("version 2\r\nClientAnimationController\r\n{\r\n\tskeleton = \"Art/Microtransactions/portals/emberportal/ember.ast\"\r\n\teffect = \"Metadata/Effects/Microtransactions/portals/emberportal/ember.epk\"\r\n}\r\n"),
        ["Metadata/Effects/Microtransactions/portals/emberportal/ember.epk"] = Utf16("1\r\n\"Art/Microtransactions/portals/emberportal/ember.pet\"\r\n"),
        ["Art/Microtransactions/portals/emberportal/ember.pet"] = Utf16("version 3\r\ntexture \"Art/Microtransactions/portals/emberportal/ember.dds\"\r\n"),
        ["Art/Microtransactions/portals/emberportal/ember.dds"] = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(),
        ["Art/Microtransactions/portals/emberportal/ember.ast"] = Enumerable.Range(0, 130).Select(i => (byte)(i * 7)).ToArray(),
        ["Art/Models/Effects/portal/portal.ast"] = Enumerable.Range(0, 90).Select(i => (byte)(255 - i)).ToArray(),
        ["Metadata/Pet/Wisp/wisp.aoc"] = Utf16("version 2\r\nskeleton = \"Art/Models/Pets/wisp/missing.ast\"\r\n"),
        ["Data/BaseItemTypes.dat64"] = [1, 2, 3, 4],
    };
}
