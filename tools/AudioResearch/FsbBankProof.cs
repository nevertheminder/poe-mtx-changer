using System.Buffers.Binary;
using System.Text;

namespace AudioResearch;

// Independent, deliberately bounded research implementation. No writes occur here.
public sealed record SampleInfo(int Index, string Name, int Frequency, int Channels, int Frames,
    int DataOffset, byte[] Header, byte[] Payload, IReadOnlyList<(int Type, byte[] Bytes)> Metadata)
{
    public bool Loops => Metadata.Any(m => m.Type == 3);
    public uint? SetupCrc => Metadata.FirstOrDefault(m => m.Type == 11).Bytes is { Length: >= 4 } b ? BinaryPrimitives.ReadUInt32LittleEndian(b) : null;
}

public sealed class FsbProof
{
    public byte[] Raw { get; }
    public byte[] NameTable { get; }
    public IReadOnlyList<SampleInfo> Samples { get; }
    public int Codec { get; }
    public int HeaderBytes { get; }
    public int NameBytes { get; }
    public int DataBytes { get; }
    private static readonly int[] Frequencies = [4000,8000,11000,11025,16000,22050,24000,32000,44100,48000,96000];
    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(at,4));
    private static int I32(ReadOnlySpan<byte> b, int at) => checked((int)U32(b,at));

    public FsbProof(byte[] bytes)
    {
        Raw = bytes;
        if (bytes.Length < 60 || !bytes.AsSpan(0,4).SequenceEqual("FSB5"u8) || U32(bytes,4)!=1)
            throw new InvalidDataException("Only unencrypted FSB5 v1 is supported by this proof.");
        int count=I32(bytes,8); HeaderBytes=I32(bytes,12); NameBytes=I32(bytes,16); DataBytes=I32(bytes,20); Codec=I32(bytes,24);
        if (count<=0 || count>100000 || checked(60L+HeaderBytes+NameBytes+DataBytes)!=bytes.Length)
            throw new InvalidDataException("FSB bounds mismatch.");
        int headerEnd=checked(60+HeaderBytes), nameEnd=checked(headerEnd+NameBytes);
        if (headerEnd>bytes.Length || nameEnd>bytes.Length) throw new InvalidDataException("FSB regions out of bounds.");
        NameTable=bytes.AsSpan(headerEnd,NameBytes).ToArray();
        var parsed=new List<SampleInfo>(); int p=60;
        for(int i=0;i<count;i++)
        {
            if(p+8>headerEnd)throw new InvalidDataException("Truncated sample header.");
            int begin=p; ulong bits=BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(p,8));p+=8;
            int rate=(int)((bits>>1)&15);int channels=new[]{1,2,6,8}[(bits>>5)&3];
            int frames=checked((int)(bits>>34));int offset=checked((int)(((bits>>7)&0x7ffffff)*32));
            var metadata=new List<(int,byte[])>();bool more=(bits&1)!=0;
            while(more)
            {
                if(p+4>headerEnd)throw new InvalidDataException("Truncated metadata header.");
                uint chunk=U32(bytes,p);p+=4;more=(chunk&1)!=0;int size=(int)((chunk>>1)&0xffffff);int type=(int)(chunk>>25);
                if(size<0 || p+size>headerEnd)throw new InvalidDataException("Truncated metadata payload.");
                var data=bytes.AsSpan(p,size).ToArray();p+=size;metadata.Add((type,data));
                if(type==1 && size==1)channels=data[0];
            }
            var frequencyMetadata=metadata.FirstOrDefault(m=>m.Item1==2).Item2;
            int frequency=frequencyMetadata is {Length:4} ? I32(frequencyMetadata,0) : rate<Frequencies.Length ? Frequencies[rate] : throw new InvalidDataException("Unknown sample rate code.");
            string name="#"+i;
            if(NameBytes!=0)
            {
                if(NameBytes<count*4)throw new InvalidDataException("Name offset table truncated.");
                int start=I32(NameTable,i*4);if(start<count*4||start>=NameBytes)throw new InvalidDataException("Name offset out of bounds.");
                int end=Array.IndexOf(NameTable,(byte)0,start);if(end<0)throw new InvalidDataException("Unterminated sample name.");
                name=Encoding.UTF8.GetString(NameTable,start,end-start);
            }
            parsed.Add(new(i,name,frequency,channels,frames,offset,bytes.AsSpan(begin,p-begin).ToArray(),[],metadata));
        }
        if(p!=headerEnd)throw new InvalidDataException("Unexpected sample-header padding/unknown bytes.");
        if(parsed[0].DataOffset!=0)throw new InvalidDataException("Nonzero first sample offset is unsupported.");
        for(int i=0;i<parsed.Count;i++)
        {
            int end=i+1<parsed.Count?parsed[i+1].DataOffset:DataBytes;int start=parsed[i].DataOffset;
            if(end<start||end>DataBytes)throw new InvalidDataException("Sample offsets are not monotonic/in bounds.");
            parsed[i]=parsed[i] with{Payload=bytes.AsSpan(nameEnd+start,end-start).ToArray()};
        }
        Samples=parsed;
    }

    public byte[] Rebuild(IReadOnlyDictionary<int,SampleInfo> substitutions)
    {
        if(substitutions.Keys.Any(i=>i<0||i>=Samples.Count))throw new ArgumentOutOfRangeException(nameof(substitutions));
        using var headers=new MemoryStream();using var payload=new MemoryStream();
        foreach(var original in Samples)
        {
            var sample=substitutions.GetValueOrDefault(original.Index)??original;
            if(sample.Header.Length<8||sample.Payload.Length==0)throw new InvalidDataException("Empty sample replacement.");
            if(sample.Metadata.Any(m=>m.Type is not (1 or 2 or 3 or 11 or 13)))throw new InvalidDataException("Unresearched metadata chunk.");
            while(payload.Position%32!=0)payload.WriteByte(0);
            ulong units=checked((ulong)payload.Position/32);if(units>0x7ffffff)throw new InvalidDataException("FSB offset overflow.");
            var header=(byte[])sample.Header.Clone();ulong packed=BinaryPrimitives.ReadUInt64LittleEndian(header);
            packed=(packed&~(0x7ffffffUL<<7))|(units<<7);BinaryPrimitives.WriteUInt64LittleEndian(header,packed);
            headers.Write(header);payload.Write(sample.Payload);
        }
        while(payload.Position%32!=0)payload.WriteByte(0);
        int namePadding=(32-(60+checked((int)headers.Length)+NameTable.Length)%32)%32;
        using var output=new MemoryStream();var baseHeader=Raw.AsSpan(0,60).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(baseHeader.AsSpan(12),checked((uint)headers.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(baseHeader.AsSpan(16),checked((uint)(NameTable.Length+namePadding)));
        BinaryPrimitives.WriteUInt32LittleEndian(baseHeader.AsSpan(20),checked((uint)payload.Length));
        output.Write(baseHeader);output.Write(headers.ToArray());output.Write(NameTable);output.Write(new byte[namePadding]);output.Write(payload.ToArray());
        return output.ToArray();
    }
}

public sealed class BankProof
{
    public byte[] Raw {get;}
    public FsbProof Fsb {get;}
    public int FsbOffset {get;}
    public int SndHeader {get;}
    public int SndhData {get;}

    public BankProof(byte[] bytes)
    {
        Raw=bytes;
        if(bytes.Length<28||!bytes.AsSpan(0,4).SequenceEqual("RIFF"u8)||!bytes.AsSpan(8,4).SequenceEqual("FEV "u8))throw new InvalidDataException("Not RIFF/FEV.");
        uint Read32(int p)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p,4));
        if(Read32(4)!=bytes.Length-8)throw new InvalidDataException("RIFF length mismatch.");
        if(!bytes.AsSpan(12,4).SequenceEqual("FMT "u8)||Read32(16)!=8||Read32(20)!=0x92)throw new InvalidDataException("Proof gates FEV format 0x92.");
        var snd=new List<(int Header,int Size)>();var sndh=new List<(int Data,int Size)>();
        Walk(12,bytes.Length,0);
        if(snd.Count!=1||sndh.Count!=1||sndh[0].Size!=12)throw new InvalidDataException("Proof requires exactly one SND and one 12-byte SNDH.");
        SndHeader=snd[0].Header;SndhData=sndh[0].Data;FsbOffset=checked((int)Read32(SndhData+4));int fsbSize=checked((int)Read32(SndhData+8));
        if(FsbOffset<SndHeader+8||FsbOffset+fsbSize!=bytes.Length||SndHeader+8+snd[0].Size!=bytes.Length)throw new InvalidDataException("Proof requires FSB at the end of the final SND chunk.");
        Fsb=new(bytes.AsSpan(FsbOffset,fsbSize).ToArray());
        void Walk(int begin,int end,int depth)
        {
            if(depth>32)throw new InvalidDataException("Nested RIFF limit.");
            int p=begin;
            while(p<end)
            {
                if(p+8>end)throw new InvalidDataException("Truncated RIFF chunk.");
                int size=checked((int)Read32(p+4));int next=checked(p+8+size);if(next>end)throw new InvalidDataException("RIFF chunk beyond container.");
                var tag=Encoding.ASCII.GetString(bytes,p,4);
                if(tag=="LIST"){if(size<4)throw new InvalidDataException("LIST without type.");Walk(p+12,next,depth+1);}
                if(tag=="SND ")snd.Add((p,size));if(tag=="SNDH")sndh.Add((p+8,size));
                p=checked(next+(size&1));
            }
            if(p!=end)throw new InvalidDataException("Odd RIFF alignment mismatch.");
        }
    }

    public byte[] Rebuild(IReadOnlyDictionary<int,SampleInfo> substitutions)
    {
        var fsb=Fsb.Rebuild(substitutions);var output=new byte[checked(FsbOffset+fsb.Length)];Raw.AsSpan(0,FsbOffset).CopyTo(output);fsb.CopyTo(output,FsbOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4),checked((uint)output.Length-8));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(SndHeader+4),checked((uint)(output.Length-SndHeader-8)));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(SndhData+8),checked((uint)fsb.Length));
        return output;
    }

    public void AssertMetadataPreserved(byte[] changed)
    {
        for(int i=0;i<FsbOffset;i++)
            if(!(i>=4&&i<8)&&!(i>=SndHeader+4&&i<SndHeader+8)&&!(i>=SndhData+8&&i<SndhData+12)&&Raw[i]!=changed[i])
                throw new InvalidDataException($"Event/GUID metadata changed at {i:X}.");
    }
}
