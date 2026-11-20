using System.Buffers.Binary;
using System.Text;
using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Formats.Bundles;
using PoeMtxChanger.Core.Installation;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Tests;

public class PathHashTests
{
    [Fact]
    public void Murmur_of_empty_path_is_the_documented_root_hash()
        => Assert.Equal(PathHash.MurmurRootHash, PathHash.Hash("", PathHashAlgorithm.Murmur64A));

    [Fact]
    public void Murmur_is_case_insensitive_and_ignores_trailing_slash()
    {
        Assert.Equal(PathHash.Hash("art/models/a.dds", PathHashAlgorithm.Murmur64A), PathHash.Hash("Art/Models/A.DDS", PathHashAlgorithm.Murmur64A));
        Assert.Equal(PathHash.Hash("Art/Models", PathHashAlgorithm.Murmur64A), PathHash.Hash("Art/Models/", PathHashAlgorithm.Murmur64A));
        Assert.NotEqual(PathHash.Hash("Art/Models/a.dds", PathHashAlgorithm.Murmur64A), PathHash.Hash("Art/Models/b.dds", PathHashAlgorithm.Murmur64A));
    }

    [Theory]
    [InlineData(1)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(16)] [InlineData(23)]
    public void Murmur_matches_reference_for_every_tail_length(int length)
    {
        var data = Enumerable.Range(0, length).Select(i => (byte)(i * 31 + 7)).ToArray();
        Assert.Equal(ReferenceMurmur64A(data, PathHash.MurmurSeed), PathHash.Murmur64A(data));
    }

    // Straight transcription of the published C reference (byte-wise tail via switch fall-through).
    private static ulong ReferenceMurmur64A(byte[] key, ulong seed)
    {
        const ulong m = 0xc6a4a7935bd1e995UL; const int r = 47;
        unchecked
        {
            ulong h = seed ^ ((ulong)key.Length * m);
            int nblocks = key.Length / 8;
            for (int i = 0; i < nblocks; i++)
            {
                ulong k = 0;
                for (int b = 7; b >= 0; b--) k = (k << 8) | key[i * 8 + b];
                k *= m; k ^= k >> r; k *= m; h ^= k; h *= m;
            }
            int t = nblocks * 8, rem = key.Length & 7;
            for (int b = rem - 1; b >= 0; b--) h ^= (ulong)key[t + b] << (8 * b);
            if (rem > 0) h *= m;
            h ^= h >> r; h *= m; h ^= h >> r;
            return h;
        }
    }

    [Fact]
    public void Algorithm_detection_from_root_hash()
    {
        Assert.Equal(PathHashAlgorithm.Murmur64A, PathHash.DetectFromRootHash(PathHash.MurmurRootHash));
        Assert.Equal(PathHashAlgorithm.Fnv1a64Legacy, PathHash.DetectFromRootHash(PathHash.FnvRootHash));
        Assert.Null(PathHash.DetectFromRootHash(42));
    }
}

public class BundleFileTests
{
    private static readonly PassthroughTestCodec Codec = new();

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(100)] [InlineData(1000)] [InlineData(1001)]
    public void Compress_then_decompress_roundtrips(int size)
    {
        var payload = Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();
        var bundle = BundleFile.Compress(payload, Codec, OodleCompressor.Leviathan, OodleCompressionLevel.Normal, chunkSize: 100);
        var h = BundleHeader.Read(bundle);
        Assert.Equal(size, h.UncompressedSize);
        Assert.Equal((size + 99) / 100, h.ChunkCount);
        Assert.Equal(48 + 4 * h.ChunkCount, h.HeadSize);
        Assert.Equal(OodleCompressor.Leviathan, h.Compressor);
        Assert.Equal(payload, BundleFile.Decompress(bundle, Codec));
    }

    [Theory]
    [InlineData(0, 10)] [InlineData(95, 10)] [InlineData(150, 300)] [InlineData(0, 1000)] [InlineData(999, 1)]
    public void ReadRange_decompresses_only_the_needed_chunks(int offset, int length)
    {
        var payload = Enumerable.Range(0, 1000).Select(i => (byte)(i * 13)).ToArray();
        var bundle = BundleFile.Compress(payload, Codec, OodleCompressor.Mermaid, OodleCompressionLevel.Normal, chunkSize: 100);
        using var ms = new MemoryStream(bundle);
        var h = BundleHeader.Read(ms);
        Assert.Equal(payload.AsSpan(offset, length).ToArray(), BundleFile.ReadRange(ms, h, offset, length, Codec));
    }

    [Fact]
    public void Header_with_inconsistent_head_size_is_rejected()
    {
        var bundle = BundleFile.Compress(new byte[500], Codec, OodleCompressor.Mermaid, OodleCompressionLevel.Normal, 100);
        BinaryPrimitives.WriteInt32LittleEndian(bundle.AsSpan(8), 999);
        Assert.Throws<InvalidDataException>(() => BundleHeader.Read(bundle));
    }

    [Fact]
    public void Truncated_bundle_is_rejected()
    {
        var bundle = BundleFile.Compress(new byte[500], Codec, OodleCompressor.Mermaid, OodleCompressionLevel.Normal, 100);
        Assert.Throws<InvalidDataException>(() => BundleFile.Decompress(bundle.AsSpan(0, bundle.Length - 10), Codec));
    }

    [Fact]
    public void Unavailable_codec_throws_a_clear_error()
    {
        var codec = new UnavailableOodleCodec("no dll");
        var ex = Assert.Throws<OodleUnavailableException>(() => codec.Decompress([1], new byte[1]));
        Assert.Equal("no dll", ex.Message);
        Assert.False(NativeOodleCodec.TryLoad(null).IsAvailable);
        Assert.False(NativeOodleCodec.TryLoad(@"C:\definitely\missing\oo2core.dll").IsAvailable);
    }

    /// <summary>Read-only check against a real installation, if one exists (no Oodle needed for the header).</summary>
    [Fact]
    public void Real_index_header_parses_when_installation_present()
    {
        var path = Environment.GetEnvironmentVariable("POE_INDEX_PATH")
                   ?? (OperatingSystem.IsWindows() ? GameInstallationDetector.DetectAll().FirstOrDefault()?.IndexPath : null);
        if (path is null) return; // no local install on this machine
        if (!File.Exists(path)) return; // no local install on this machine
        var info = IndexInspector.Inspect(path, computeHash: false);
        Assert.Equal(48 + 4 * info.Header.ChunkCount, info.Header.HeadSize);
        Assert.True(Enum.IsDefined(info.Header.Compressor));
        Assert.Equal(info.Header.UncompressedSize, info.Header.UncompressedSizeLong);
    }
}

public class BundleIndexTests
{
    [Fact]
    public void Path_table_handles_base_mode_prefixes()
    {
        using var ms = new MemoryStream();
        void Int(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); ms.Write(b); }
        void Str(string s) { ms.Write(Encoding.UTF8.GetBytes(s)); ms.WriteByte(0); }
        Int(0);            // base mode on
        Int(1); Str("Art/");          // temp[0] = "Art/"
        Int(1); Str("Models/");       // temp[1] = temp[0] + "Models/"
        Int(0);            // base mode off
        Int(1); Str("a.dds");         // temp[0] + a.dds
        Int(2); Str("b.sm");          // temp[1] + b.sm
        Int(9); Str("Metadata/x.ot"); // out of range → literal
        var table = ms.ToArray();
        var paths = PathTable.Enumerate(table, [new DirectoryRecord(0, 0, table.Length, table.Length)]).ToList();
        Assert.Equal(["Art/a.dds", "Art/Models/b.sm", "Metadata/x.ot"], paths);
    }

    [Fact]
    public void Synthetic_index_roundtrips_and_resolves_paths()
    {
        using var tmp = new TempDir();
        var files = SyntheticGame.SampleFiles();
        using var storage = SyntheticGame.Create(tmp.Path, files);
        using var archive = GameArchive.Open(storage, SyntheticGame.Codec);

        Assert.Equal(files.Count, archive.Index.Files.Count);
        Assert.Equal(0, archive.UnresolvedPathCount);
        Assert.All(archive.Index.Files, f => Assert.NotNull(f.Path));
        foreach (var (path, content) in files)
            Assert.Equal(content, archive.Read(path));

        var reparsed = BundleIndex.Parse(archive.Index.Serialize());
        Assert.Equal(archive.Index.Serialize(), reparsed.Serialize());
        Assert.Equal(PathHashAlgorithm.Murmur64A, reparsed.HashAlgorithm);
    }

    [Fact]
    public void File_record_pointing_to_missing_bundle_is_rejected()
    {
        var idx = new BundleIndex();
        idx.AddBundle("a", 0);
        idx.Files.Add(new FileRecord(1, 5, 0, 0));
        Assert.Throws<InvalidDataException>(() => BundleIndex.Parse(idx.Serialize()));
    }

    [Fact]
    public void Bundle_names_cannot_escape_Bundles2()
    {
        using var tmp = new TempDir();
        var storage = new DirectoryBundleStorage(tmp.Path);
        Assert.Throws<InvalidDataException>(() => storage.GetBundlePath("../../evil"));
    }
}

public class InstallationTests
{
    [Fact]
    public void Steam_library_folders_are_parsed()
    {
        var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"E:\\\\Games\\\\Steam\"\n\t}\n}";
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"E:\Games\Steam"], GameInstallationDetector.ParseLibraryFolders(vdf));
        var acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"238960\"\n\t\"buildid\"\t\t\"26694821\"\n}";
        Assert.Equal("26694821", GameInstallationDetector.ParseVdfFlat(acf)["buildid"]);
    }

    [Fact]
    public void Probe_rejects_folders_without_game_data()
    {
        using var tmp = new TempDir();
        Assert.Null(GameInstallationDetector.Probe(tmp.Path, "test"));
        Assert.Null(GameInstallationDetector.Probe(tmp.Combine("missing"), "test"));
        SyntheticGame.Create(tmp.Path, SyntheticGame.SampleFiles()).Dispose();
        var inst = GameInstallationDetector.Probe(tmp.Path, "test", "123");
        Assert.NotNull(inst);
        Assert.Equal(InstallationKind.Steam, inst!.Kind);
        Assert.False(inst.UsesGgpk);
    }
}
