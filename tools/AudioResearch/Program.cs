using System.Security.Cryptography;
using AudioResearch;

string root=args.FirstOrDefault(a=>!a.StartsWith("--"))
    ??throw new InvalidOperationException("Pass the Path of Exile folder as the first argument.");
string dir=Path.Combine(root,"FMOD","Desktop");
var targetPath=Path.Combine(dir,"AuspiciousCycloneMTX.bank");
var donorPath=Path.Combine(dir,"CelestialCycloneMTX.bank");
var targetBytes=File.ReadAllBytes(targetPath);var donorBytes=File.ReadAllBytes(donorPath);
var targetHash=SHA256.HashData(targetBytes);var donorHash=SHA256.HashData(donorBytes);
var target=new BankProof(targetBytes);var donor=new BankProof(donorBytes);
Console.WriteLine($"Target: {Path.GetFileName(targetPath)}; FSB@{target.FsbOffset}, {target.Fsb.Samples.Count} samples, codec {target.Fsb.Codec}");
Console.WriteLine($"Donor: {Path.GetFileName(donorPath)}; FSB@{donor.FsbOffset}, {donor.Fsb.Samples.Count} samples, codec {donor.Fsb.Codec}");
foreach(var sample in target.Fsb.Samples.Take(6))Console.WriteLine($"  #{sample.Index} {sample.Name} {sample.Frequency}Hz {sample.Channels}ch frames={sample.Frames}, bytes={sample.Payload.Length}, crc={sample.SetupCrc:X8}");
var noOp=target.Rebuild(new Dictionary<int,SampleInfo>());
if(!targetBytes.SequenceEqual(noOp))throw new Exception("Actual no-op reconstruction is not byte-identical.");
Console.WriteLine("PASS: rebuilding all samples without substitution is byte-identical.");
if(target.Fsb.Codec!=donor.Fsb.Codec)throw new Exception("Codec mismatch.");
var pair=(from t in target.Fsb.Samples from d in donor.Fsb.Samples
    where t.Frequency==d.Frequency&&t.Channels==d.Channels&&t.Frames==d.Frames&&t.Loops==d.Loops
        &&!SHA256.HashData(t.Payload).SequenceEqual(SHA256.HashData(d.Payload))
    select (Target:t,Donor:d)).First();
Console.WriteLine($"Swap proof: target#{pair.Target.Index} '{pair.Target.Name}' <- donor#{pair.Donor.Index} '{pair.Donor.Name}' ({pair.Target.Frames} frames)");
var changed=target.Rebuild(new Dictionary<int,SampleInfo>{{pair.Target.Index,pair.Donor}});
target.AssertMetadataPreserved(changed);
var parsed=new BankProof(changed);
for(int i=0;i<target.Fsb.Samples.Count;i++)
{
    var expected=i==pair.Target.Index?pair.Donor:target.Fsb.Samples[i];var actual=parsed.Fsb.Samples[i];
    if(actual.Name!=target.Fsb.Samples[i].Name)throw new Exception("Target slot name changed.");
    if(actual.Frames!=expected.Frames||actual.Channels!=expected.Channels||actual.Frequency!=expected.Frequency)throw new Exception("Sample metadata mismatch.");
    if(!actual.Payload.AsSpan(0,expected.Payload.Length).SequenceEqual(expected.Payload))throw new Exception("Encoded payload mismatch.");
    if(actual.Metadata.Count!=expected.Metadata.Count||actual.Metadata.Where((m,j)=>m.Type!=expected.Metadata[j].Type||!m.Bytes.SequenceEqual(expected.Metadata[j].Bytes)).Any())throw new Exception("Sample metadata chunks changed.");
}
Console.WriteLine($"PASS: donor payload/metadata preserved; all other samples preserved; all non-SND event/GUID metadata preserved. Bank {targetBytes.Length}->{changed.Length} bytes.");
var temp=Path.Combine(Path.GetTempPath(),"PoeMtxChanger-AudioProof-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
File.WriteAllBytes(Path.Combine(temp,"original.bank"),targetBytes);File.WriteAllBytes(Path.Combine(temp,"rebuilt.bank"),changed);
File.WriteAllBytes(Path.Combine(temp,"donor.fsb"),donor.Fsb.Raw);File.WriteAllBytes(Path.Combine(temp,"rebuilt.fsb"),parsed.Fsb.Raw);
if(args.Contains("--oracle"))
{
    using var oracle=new FmodOracle(Path.Combine(root,"fmod.dll"));
    foreach(uint seek in new uint[]{0,(uint)(pair.Target.Frames/2)})
    {
        var source=oracle.DecodeHash(Path.Combine(temp,"donor.fsb"),pair.Donor.Index,seek);
        var output=oracle.DecodeHash(Path.Combine(temp,"rebuilt.fsb"),pair.Target.Index,seek);
        if(source!=output)throw new Exception($"Independent FMOD PCM hash mismatch at seek {seek}: {source} vs {output}");
        Console.WriteLine($"PASS: installed FMOD decodes donor and rebuilt slot identically at PCM seek {seek}: {source}");
    }
}
if(!SHA256.HashData(File.ReadAllBytes(targetPath)).SequenceEqual(targetHash)||!SHA256.HashData(File.ReadAllBytes(donorPath)).SequenceEqual(donorHash))throw new Exception("Live input changed externally.");
Console.WriteLine("Live input hashes unchanged. Temp copies: "+temp);
