using System.Buffers.Binary;
using System.Text;
using PoeMtxChanger.Core.Formats.Audio;
using PoeMtxChanger.Core.Patching;
using PoeMtxChanger.Core.Profiles;
using PoeMtxChanger.Core.Util;

namespace PoeMtxChanger.Core.Tests;

public class AudioTests
{
    private static byte[] Bank(byte seed,int frames=44100)
    {
        using var fsb=new MemoryStream();using var w=new BinaryWriter(fsb,Encoding.UTF8,true);
        w.Write("FSB5"u8);w.Write(1);w.Write(2);w.Write(16);w.Write(20);w.Write(64);w.Write(15);w.Write(new byte[32]);
        for(int i=0;i<2;i++)w.Write(((ulong)frames<<34)|((ulong)i<<7)|(1UL<<5)|(8UL<<1));
        w.Write(8);w.Write(10);w.Write("a\0b\0"u8);w.Write(new byte[8]);w.Write(Enumerable.Repeat(seed,32).ToArray());w.Write(Enumerable.Repeat((byte)(seed+1),32).ToArray());
        using var bank=new MemoryStream();using var b=new BinaryWriter(bank);
        b.Write("RIFF"u8);b.Write(0);b.Write("FEV FMT "u8);b.Write(8);b.Write(0x92);b.Write(0);
        b.Write("SNDH"u8);b.Write(12);b.Write(0);b.Write(56);b.Write((int)fsb.Length);
        b.Write("SND "u8);b.Write((int)fsb.Length);b.Write(fsb.ToArray());b.Flush();bank.Position=4;b.Write((int)bank.Length-8);return bank.ToArray();
    }
    [Fact]
    public void Rebuild_preserves_events_names_other_samples_and_donor_codec_metadata()
    {
        var original=new FmodBank(Bank(5));var donor=new FmodBank(Bank(9));
        Assert.Equal(original.Raw,original.Rebuild(new Dictionary<int,SampleInfo>()));
        byte[] changed=original.Rebuild(new Dictionary<int,SampleInfo>{{0,donor.Fsb.Samples[1]}});
        original.AssertMetadataPreserved(changed);var result=new FmodBank(changed);
        Assert.Equal(new[]{"a","b"},result.Fsb.Samples.Select(s=>s.Name));
        Assert.Equal(donor.Fsb.Samples[1].Payload,result.Fsb.Samples[0].Payload);
        Assert.Equal(original.Fsb.Samples[1].Payload,result.Fsb.Samples[1].Payload);
    }
    [Fact]
    public void Different_duration_and_unknown_metadata_are_rejected()
    {
        var original=new FmodBank(Bank(5));var donor=new FmodBank(Bank(9,22050));
        Assert.Throws<InvalidDataException>(()=>original.Rebuild(new Dictionary<int,SampleInfo>{{0,donor.Fsb.Samples[0]}}));
        var sample=original.Fsb.Samples[0] with{Metadata=[(127,new byte[]{1})]};
        Assert.False(FsbSoundBank.Compatible(original.Fsb.Samples[0],sample));
    }
    [Theory]
    [InlineData(4)] [InlineData(16)] [InlineData(48)] [InlineData(52)]
    public void Corrupt_container_lengths_are_rejected(int offset)
    {
        byte[] broken=Bank(1);BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(offset),uint.MaxValue);
        Assert.ThrowsAny<Exception>(()=>new FmodBank(broken));
    }
    [Fact]
    public void Plan_is_pinned_to_hashes_and_restore_is_exact()
    {
        using var tmp=new TempDir();string dir=tmp.Combine("game","FMOD","Desktop");Directory.CreateDirectory(dir);
        string path=Path.Combine(dir,"target.bank");byte[] original=Bank(2);File.WriteAllBytes(path,original);File.WriteAllBytes(Path.Combine(dir,"source.bank"),Bank(8));
        var engine=new AudioBankEngine(tmp.Combine("game"),tmp.Combine("backups"),()=>false);
        var plan=engine.Plan("target.bank",0,"source.bank",1);
        Assert.Throws<PatchException>(()=>engine.Apply("target.bank",[plan],false));Assert.Equal(original,File.ReadAllBytes(path));
        engine.Apply("target.bank",[plan],true);Assert.Equal(9,new FmodBank(File.ReadAllBytes(path)).Fsb.Samples[0].Payload[0]);
        engine.Restore("target.bank",true);Assert.Equal(original,File.ReadAllBytes(path));
        File.WriteAllBytes(Path.Combine(dir,"source.bank"),Bank(10));
        Assert.Throws<PatchException>(()=>engine.Apply("target.bank",[plan],true));Assert.Equal(original,File.ReadAllBytes(path));
    }
    [Fact]
    public void Profiles_retain_audio_plans_and_reject_file_system_paths()
    {
        using var tmp=new TempDir();var store=new ProfileManager(tmp.Path);
        var swap=new AudioSampleSwap("target.bank",0,"a","source.bank",1,"b",new string('a',64),new string('b',64));
        var profile=new CosmeticProfile{AudioSwaps=[swap]};store.Save(profile);
        Assert.Equal(swap,Assert.Single(Assert.Single(store.LoadAll()).AudioSwaps));
        profile.AudioSwaps=[swap with{SourceBank="../outside.bank"}];Assert.Throws<ProfileImportException>(()=>store.Save(profile));
        Assert.Throws<ProfileImportException>(()=>ProfileManager.Parse("{\"SchemaVersion\":1,\"AudioSwaps\":[null]}"));
    }
}
