using System.Buffers.Binary;
using System.Security.Cryptography;
using PoeMtxChanger.Core.Formats.Ggpk;
using PoeMtxChanger.Core.Installation;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Patching;
using PoeMtxChanger.Core.Storage;
using PoeMtxChanger.Core.Util;

namespace PoeMtxChanger.Core.Tests;

public class GgpkWriteTests
{
    [Theory]
    [InlineData(2u)] [InlineData(3u)] [InlineData(4u)]
    public void Writer_preserves_payloads_sorted_child_hashes_and_empty_directories(uint version)
    {
        using var stream = new MemoryStream();
        GgpkRebuilder.Write(stream, version, [GgpkInputFile.FromBytes("Bundles2/_.index.bin", [7,8,9]), GgpkInputFile.FromBytes("Sounds/a.bank", [1,2])], directories: ["Empty/Nested"]);
        using var reader = new GgpkArchive(stream, true);
        Assert.Contains("Empty/Nested", reader.EnumerateDirectories());
        Assert.Equal(new byte[] {7,8,9}, reader.ReadFile(reader.EnumerateFiles().Single(e=>e.Path.EndsWith("index.bin"))));
        VerifyNode(reader.RootDirectoryOffset);
        byte[] VerifyNode(long offset)
        {
            stream.Position = offset; using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
            uint length = r.ReadUInt32(), tag = r.ReadUInt32(); int nameLength = r.ReadInt32();
            int count = tag == GgpkArchive.TagPdir ? r.ReadInt32() : 0;
            byte[] stored = r.ReadBytes(32); r.ReadBytes(nameLength*(version==4?4:2));
            if (tag == GgpkArchive.TagFile) Assert.Equal(stored, SHA256.HashData(r.ReadBytes(checked((int)(offset+length-stream.Position)))));
            else
            {
                var entries = Enumerable.Range(0,count).Select(_=>(Hash:r.ReadUInt32(),Offset:r.ReadInt64())).ToArray();
                Assert.Equal(entries.Select(e=>e.Hash).Order(), entries.Select(e=>e.Hash));
                using var childHashes = new MemoryStream();
                foreach(var entry in entries) childHashes.Write(VerifyNode(entry.Offset));
                Assert.Equal(stored, SHA256.HashData(childHashes.ToArray()));
            }
            return stored;
        }
    }

    [Fact]
    public async Task Standalone_apply_readback_second_profile_and_exact_restore()
    {
        using var tmp = new TempDir(); var files = SyntheticGame.SampleFiles();
        using var disk = SyntheticGame.Create(tmp.Combine("steam"), files);
        string ggpk = tmp.Combine("Content.ggpk");
        var inputs = Directory.EnumerateFiles(tmp.Combine("steam"), "*", SearchOption.AllDirectories)
            .Select(f=>GgpkInputFile.FromDisk(Path.GetRelativePath(tmp.Combine("steam"),f).Replace('\\','/'),f)).ToList();
        inputs.Add(GgpkInputFile.FromBytes("unrelated.txt", [1,9,2]));
        using(var output = File.Create(ggpk)) GgpkRebuilder.Write(output,3,inputs, directories:["Empty"]);
        string sha = FileUtil.Sha256OfFile(ggpk);
        var engine = new StandalonePatchEngine(ggpk,SyntheticGame.Codec,tmp.Combine("backups"),()=>false);
        const string target = "Metadata/Effects/Misc/portal/townportal.aoc";
        const string source = "Metadata/Effects/Microtransactions/portals/emberportal/emberportal.aoc";
        engine.Apply(new PatchRequest{ UserConfirmed=true, ProfileName="first", Mappings=[new ReplacementMapping{TargetPath=target,SourcePath=source}]});
        using(var a=GameArchive.Open(new GgpkBundleStorage(ggpk),SyntheticGame.Codec)) Assert.Equal(files[source],a.Read(target));
        using(var a=GgpkArchive.OpenRead(ggpk)) { Assert.Contains("Empty",a.EnumerateDirectories()); Assert.Equal(new byte[]{1,9,2},a.ReadFile(a.EnumerateFiles().Single(f=>f.Path=="unrelated.txt"))); }
        engine.Apply(new PatchRequest{ UserConfirmed=true, ProfileName="second", Mappings=[new ReplacementMapping{TargetPath=source,SourcePath=target}]});
        using(var a=GameArchive.Open(new GgpkBundleStorage(ggpk),SyntheticGame.Codec)) {Assert.Equal(files[target],a.Read(target));Assert.Equal(files[target],a.Read(source));}
        Assert.True((await engine.RestoreAsync(true)).IndexRestored);
        Assert.Equal(sha,FileUtil.Sha256OfFile(ggpk));
        Assert.Equal(GameFileStatus.Clean,engine.GetStatus());
    }

    [Fact]
    public void Atomic_failure_restores_previous_profile_and_rejects_external_change()
    {
        using var tmp=new TempDir(); string file=tmp.Combine("bank.bin"); File.WriteAllBytes(file,[1]);
        var patch=new AtomicFilePatch(file,tmp.Combine("backups"),()=>false);
        void Apply(byte b,IProgress<string>? progress=null)=>patch.Apply(true,"test",(_,stage,_)=>File.WriteAllBytes(stage,[b]),(_,_)=>{},progress);
        Apply(2);
        Assert.Throws<PatchException>(()=>Apply(3,new SynchronousProgress(s=>{if(s=="Saving the file state…")throw new IOException("Injected save failure");})));
        Assert.Equal(new byte[]{2},File.ReadAllBytes(file)); Assert.Equal(GameFileStatus.PatchedByUs,patch.GetStatus());
        File.WriteAllBytes(file,[4]); Assert.Throws<PatchException>(()=>Apply(5)); Assert.Equal(new byte[]{4},File.ReadAllBytes(file));
    }
    private sealed class SynchronousProgress(Action<string> callback):IProgress<string>{public void Report(string s)=>callback(s);}

    [Fact]
    public void Interrupted_commit_recovers_previous_state_before_restore()
    {
        using var tmp=new TempDir();string path=tmp.Combine("file.bin");File.WriteAllBytes(path,[1]);bool running=false;
        var patch=new AtomicFilePatch(path,tmp.Combine("backup"),()=>running);
        patch.Apply(true,"first",(_,s,_)=>File.WriteAllBytes(s,[2]),(_,_)=>{});
        var error=Assert.Throws<PatchException>(()=>patch.Apply(true,"second",(_,s,_)=>File.WriteAllBytes(s,[3]),(_,_)=>{},
            new SynchronousProgress(message=>{if(message=="Saving the file state…"){running=true;throw new IOException("Simulated crash");}})));
        Assert.Equal("RECOVERY_REQUIRED",error.Code);Assert.Equal(GameFileStatus.RecoveryRequired,patch.GetStatus());
        running=false;Assert.True(patch.Restore(true));Assert.Equal(new byte[]{1},File.ReadAllBytes(path));Assert.False(File.Exists(patch.JournalPath));
    }
    [Fact]
    public void Cancellation_during_staging_and_adopt_leave_recoverable_baselines()
    {
        using var tmp=new TempDir();string path=tmp.Combine("file.bin");File.WriteAllBytes(path,[1]);
        var patch=new AtomicFilePatch(path,tmp.Combine("backup"),()=>false);using var cancel=new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(()=>patch.Apply(true,"cancel",(_,s,_)=>{File.WriteAllBytes(s,[2]);cancel.Cancel();},(_,_)=>{},ct:cancel.Token));
        Assert.Equal(new byte[]{1},File.ReadAllBytes(path));Assert.Equal(GameFileStatus.Clean,patch.GetStatus());
        File.WriteAllBytes(path,[7]);patch.Adopt(true);Assert.Equal(GameFileStatus.Clean,patch.GetStatus());Assert.Equal(new byte[]{7},File.ReadAllBytes(patch.BaselinePath));
    }
}
