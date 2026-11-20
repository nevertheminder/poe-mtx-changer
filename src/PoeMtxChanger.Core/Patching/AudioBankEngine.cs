using PoeMtxChanger.Core.Formats.Audio;
using PoeMtxChanger.Core.Util;

namespace PoeMtxChanger.Core.Patching;

public sealed record AudioSampleSwap(string TargetBank, int TargetIndex, string TargetName,
    string SourceBank, int SourceIndex, string SourceName, string TargetBankSha256, string SourceBankSha256);

/// <summary>Individual encoded sample swaps. Event/GUID records, slot names and event timing are retained.</summary>
public sealed class AudioBankEngine(string gameRoot,string backupRoot,Func<bool>? isGameRunning=null)
{
    private string BankPath(string name)
    {
        if(name!=Path.GetFileName(name)||!name.EndsWith(".bank",StringComparison.OrdinalIgnoreCase)||name.IndexOfAny(['/', '\\', ':'])>=0)
            throw new InvalidDataException("Expected a bank file name inside FMOD/Desktop.");
        return Path.Combine(gameRoot,"FMOD","Desktop",name);
    }
    private AtomicFilePatch FilePatch(string name)=>new(BankPath(name),backupRoot,isGameRunning);
    public GameFileStatus GetStatus(string name)=>FilePatch(name).GetStatus();
    public byte[] ReadClean(string name)
    {
        var file=FilePatch(name);var status=file.GetStatus();
        if(status is GameFileStatus.ChangedExternally or GameFileStatus.BaselineDamaged or GameFileStatus.RecoveryRequired)
            throw new PatchException("AUDIO_STATE","The sound was changed externally, the backup is missing or a restore is needed: "+name);
        if(status==GameFileStatus.PatchedByUs)
        {
            byte[] bytes=System.IO.File.ReadAllBytes(file.BaselinePath);
            if(FileUtil.Sha256(bytes)!=file.LoadState()!.BaselineSha256)throw new PatchException("BASELINE","The original audio bank is damaged.");
            return bytes;
        }
        return System.IO.File.ReadAllBytes(BankPath(name));
    }
    public AudioSampleSwap Plan(string target,int targetIndex,string source,int sourceIndex)
    {
        byte[] t=ReadClean(target),s=ReadClean(source);var tb=new FmodBank(t);var sb=new FmodBank(s);
        var ts=Sample(tb,targetIndex);var ss=Sample(sb,sourceIndex);
        if(!FsbSoundBank.Compatible(ts,ss))throw new InvalidDataException("A replacement needs the same Vorbis codec, rate, channels, length and loop mode.");
        return new(target,targetIndex,ts.Name,source,sourceIndex,ss.Name,FileUtil.Sha256(t),FileUtil.Sha256(s));
    }
    private static SampleInfo Sample(FmodBank bank,int index)
        =>(uint)index<(uint)bank.Fsb.Samples.Count?bank.Fsb.Samples[index]:throw new InvalidDataException("Sample number outside the bank.");

    public void Apply(string target,IReadOnlyList<AudioSampleSwap> swaps,bool confirmed,CancellationToken ct=default)
    {
        if(swaps.Count==0||swaps.Any(s=>s.TargetBank!=target)||swaps.Select(s=>s.TargetIndex).Distinct().Count()!=swaps.Count)
            throw new InvalidDataException("Expected a nonempty unique sample plan for one target bank.");
        var patch=FilePatch(target);
        patch.Apply(confirmed,"Sounds: "+target,(baseline,stage,token)=>
        {
            byte[] original=System.IO.File.ReadAllBytes(baseline);var bank=new FmodBank(original);
            var replacements=new Dictionary<int,SampleInfo>();
            foreach(var swap in swaps)
            {
                token.ThrowIfCancellationRequested();
                byte[] donor=ReadClean(swap.SourceBank);var source=new FmodBank(donor);
                if(FileUtil.Sha256(original)!=swap.TargetBankSha256||FileUtil.Sha256(donor)!=swap.SourceBankSha256)
                    throw new PatchException("AUDIO_CHANGED","The bank changed after the plan was made. Read the sound list again.");
                var targetSample=Sample(bank,swap.TargetIndex);var sourceSample=Sample(source,swap.SourceIndex);
                if(targetSample.Name!=swap.TargetName||sourceSample.Name!=swap.SourceName)throw new PatchException("AUDIO_CHANGED","The sample names changed.");
                replacements.Add(swap.TargetIndex,sourceSample);
            }
            byte[] changed=bank.Rebuild(replacements);bank.AssertMetadataPreserved(changed);
            var parsed=new FmodBank(changed);
            for(int i=0;i<bank.Fsb.Samples.Count;i++)
            {
                var expected=replacements.GetValueOrDefault(i)??bank.Fsb.Samples[i];var actual=parsed.Fsb.Samples[i];
                if(actual.Name!=bank.Fsb.Samples[i].Name||!actual.Payload.AsSpan().StartsWith(expected.Payload)
                    ||actual.Frames!=expected.Frames||actual.Frequency!=expected.Frequency||actual.Channels!=expected.Channels
                    ||actual.Metadata.Count!=expected.Metadata.Count||actual.Metadata.Where((m,j)=>m.Type!=expected.Metadata[j].Type||!m.Bytes.SequenceEqual(expected.Metadata[j].Bytes)).Any())
                    throw new PatchException("AUDIO_VERIFY","The rebuild changed another slot's content.");
            }
            using var output=new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None);output.Write(changed);output.Flush(true);
        },(stage,token)=>{token.ThrowIfCancellationRequested();_=new FmodBank(System.IO.File.ReadAllBytes(stage));},ct:ct);
    }
    public bool Restore(string name,bool confirmed)=>FilePatch(name).Restore(confirmed);
}
