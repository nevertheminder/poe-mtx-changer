// Read-only format experiment. JSON reports and optional asset exports go to the OS temp directory.
// Independently implemented from the binary layout and measured against local PoE 1 assets.
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Formats.Bundles;
using PoeMtxChanger.Core.Formats.Dat;
using PoeMtxChanger.Core.Installation;
using PoeMtxChanger.Core.Resources;
using PoeMtxChanger.Core.Storage;

// Game folder and Oodle: first argument / second argument, otherwise detected on this computer.
var installation = (args.Length > 0 ? GameInstallationDetector.Probe(args[0], "argument") : GameInstallationDetector.DetectAll().FirstOrDefault())
    ?? throw new InvalidOperationException("Path of Exile not found; pass its folder as the first argument.");
using var codec = (NativeOodleCodec)(args.Length > 1 ? NativeOodleCodec.TryLoad(args[1])
    : OodleLocator.FindAndLoad(AppContext.BaseDirectory)?.Codec ?? throw new InvalidOperationException("Oodle not found; pass oo2core_*_win64.dll as the second argument."));
using var source = GameArchive.Open(installation.OpenStorage(), codec);
var reportDirectory = Path.Combine(Path.GetTempPath(), "PoeMtxChanger-GeometryResearch");
Directory.CreateDirectory(reportDirectory);

object InspectMesh(string path)
{
    var bytes = source.Read(path);
    var mesh = MeshData.Read(bytes);
    return new { path, bytes = bytes.Length, mesh.Version, mesh.Flags, mesh.TriangleCount, vertexCount = mesh.Vertices.Count,
        mesh.Shapes, mesh.HeaderBounds, positionMin = V(mesh.Vertices.Select(x => x.Position).Aggregate(Vector3.Min)),
        positionMax = V(mesh.Vertices.Select(x => x.Position).Aggregate(Vector3.Max)),
        mesh.VertexStart, mesh.VertexStride, mesh.GeometryEnd, tailBytes = bytes.Length - mesh.GeometryEnd,
        maxIndex = mesh.Indices.Max(), validIndices = mesh.Indices.All(x => x < mesh.Vertices.Count),
        finiteVertices = mesh.Vertices.All(x => float.IsFinite(x.Position.X) && float.IsFinite(x.Position.Y) && float.IsFinite(x.Position.Z)),
        weightSums = mesh.Vertices.GroupBy(v => v.Weights.Sum(x => x)).Select(g => new { sum = g.Key, count = g.Count() }),
        weightedBoneRange = mesh.Vertices.SelectMany(v => v.Bones.Where((_, i) => v.Weights[i] > 0)).DefaultIfEmpty().Max(),
        sampleVertices = mesh.Vertices.Take(3).Select(v => new { position = V(v.Position), normal = V(v.Normal), uv = new[] {v.Uv.X, v.Uv.Y}, bones=v.Bones, weights=v.Weights }) };
}
object InspectSkeleton(string path)
{
    var bytes = source.Read(path);
    var skeleton = SkeletonData.Read(bytes, codec);
    return new { path, bytes=bytes.Length, skeleton.Version, skeleton.MetadataEnd, skeleton.BundleBytes, skeleton.DecompressedBytes,
        boneCount=skeleton.Bones.Count, bones=skeleton.Bones.Select((b,i)=>new { index=i,b.Name,b.Parent,b.Sibling,b.Child,local=Mat(b.Local), world=Mat(skeleton.BindWorld[i]) }),
        totalAnimationCount=skeleton.Animations.Count,
        animations=skeleton.Animations.Where(a=>a.Tracks.Count>0).Select(a=>new {a.Name,a.FrameRate,a.TrackCount,a.Offset,a.Length,a.ParsedBytes,a.MaxFrame,parsedTrackCount=a.Tracks.Count,
            sampleTracks=a.Tracks.Take(3).Select(t=>new {t.Bone, counts=t.KeyCounts,scales=t.Keys[0].Take(2),rotations=t.Keys[1].Take(2),positions=t.Keys[2].Take(2),extra=t.Keys.Skip(3).Select(k=>k.Take(4))}),
            poses=new[]{0f,a.MaxFrame/2,a.MaxFrame}.Select(f=>new {frame=f, transforms=skeleton.Pose(a,f).Take(6).Select(Mat)}) }) };
}
static float[] V(Vector3 v) => [v.X,v.Y,v.Z];
static float[] Mat(Matrix4x4 m) => [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];

var mode=args.Length>0?args[0]:"mesh";
if(mode=="paths") foreach(var p in source.EnumeratePaths().Where(p=>args.Skip(1).All(x=>p.Contains(x,StringComparison.OrdinalIgnoreCase))).Take(100))Console.WriteLine(p);
else if(mode=="text")foreach(var path in args.Skip(1)){Console.WriteLine("== "+path+" exists="+source.Exists(path));if(source.Exists(path)){var text=ReferenceExtractor.DecodeText(source.Read(path))??"";Console.WriteLine(text[..Math.Min(4500,text.Length)]);}}
else if(mode=="ao")foreach(var original in args.Skip(1))
{
    string path=original;
    for(int depth=0;depth<6&&source.Exists(path);depth++)
    {
        var text=ReferenceExtractor.DecodeText(source.Read(path))??"";
        Console.WriteLine("== "+path);foreach(var line in text.Split('\n').Where(l=>Regex.IsMatch(l,@"^\s*(metadata|skeleton|skin|attached_object|extends)\s")))Console.WriteLine(line.Trim());
        var parent=Regex.Match(text,"extends\\s+\"([^\"]+)\"");if(!parent.Success)break;path=parent.Groups[1].Value+".ao";
    }
}
else if(mode=="characters")
{
    var ts=DatSchema.LoadDefault().Tables["Characters"];var t=DatTable.Parse(source.Read(ts.GamePath),ts);
    for(int r=0;r<Math.Min(t.RowCount,20);r++)Console.WriteLine($"#{r} Id={t.GetString(r,"Id")} Name={t.GetString(r,"Name")} AO={t.GetString(r,"AOFile")} ACT={t.GetString(r,"ACTFile")}");
}
else
{
    var paths=args.Skip(1).ToArray();if(paths.Length==0)paths=[mode=="skeleton"?"Art/Models/Effects/townportal_3/rig.ast":"Art/Models/Effects/townportal_3/rig_b30f1235.smd"];
    foreach(var path in paths)
    {
        try
        {
            var report=mode=="skeleton"?InspectSkeleton(path):InspectMesh(path);
            string json=JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true});
            var output=Path.Combine(reportDirectory,Path.GetFileName(path)+".json");File.WriteAllText(output,json);
            Console.WriteLine("PASS "+path+" → "+output);Console.WriteLine(json.Length>3800?json[..3800]+"...":json);
        }
        catch(Exception ex){Console.WriteLine("FAIL "+path+": "+ex);Environment.ExitCode=1;}
    }
}

internal sealed record Vertex(Vector3 Position,Vector3 Normal,Vector2 Uv,byte[] Bones,byte[] Weights);
internal sealed record Shape(string Name,uint TriangleStart);
internal sealed class MeshData
{
    public byte Version,Flags;
    public uint TriangleCount;
    public List<Vertex> Vertices=[];
    public uint[] Indices=[];
    public List<Shape> Shapes=[];
    public float[] HeaderBounds=[];
    public long VertexStart,GeometryEnd;
    public int VertexStride;
    public static MeshData Read(byte[] bytes)
    {
        using var r=new BinaryReader(new MemoryStream(bytes));
        var m=new MeshData {Version=r.ReadByte()};
        if(m.Version is not (1 or 2))throw new NotSupportedException("Proof reader covers old v1/v2 only; got "+m.Version);
        m.TriangleCount=r.ReadUInt32();var vertexCount=r.ReadUInt32();m.Flags=r.ReadByte();var shapeCount=r.ReadUInt16();var nameBytes=r.ReadUInt32();
        m.HeaderBounds=Enumerable.Range(0,6).Select(_=>r.ReadSingle()).ToArray();if(m.Version==2)r.ReadUInt32();
        var shapeHeaders=Enumerable.Range(0,shapeCount).Select(_=>(Length:r.ReadUInt32(),Start:r.ReadUInt32())).ToArray();
        foreach(var h in shapeHeaders)m.Shapes.Add(new Shape(Encoding.Unicode.GetString(r.ReadBytes(checked((int)h.Length))),h.Start));
        m.Indices=Enumerable.Range(0,checked((int)m.TriangleCount*3)).Select(_=>vertexCount<65536?r.ReadUInt16():r.ReadUInt32()).ToArray();
        m.VertexStart=r.BaseStream.Position;m.VertexStride=32+((m.Flags&2)!=0?4:0)+((m.Flags&1)!=0?4:0);
        for(int i=0;i<vertexCount;i++)
        {
            var p=new Vector3(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());var normal=r.ReadBytes(4);r.ReadBytes(4);
            var uv=new Vector2((float)BitConverter.UInt16BitsToHalf(r.ReadUInt16()),(float)BitConverter.UInt16BitsToHalf(r.ReadUInt16()));
            var bones=r.ReadBytes(4);var weights=r.ReadBytes(4);if((m.Flags&2)!=0)r.ReadBytes(4);if((m.Flags&1)!=0)r.ReadBytes(4);
            m.Vertices.Add(new Vertex(p,new Vector3((sbyte)normal[0]/127f,(sbyte)normal[1]/127f,(sbyte)normal[2]/127f),uv,bones,weights));
        }
        m.GeometryEnd=r.BaseStream.Position;return m;
    }
}
internal sealed record Bone(string Name,int Sibling,int Child,Matrix4x4 Local){public int Parent=-1;}
internal sealed record Track(int Bone,uint[] KeyCounts,List<float[]>[] Keys);
internal sealed class AnimationData
{
    public string Name="";public int FrameRate,TrackCount;public uint Offset,Length;public long ParsedBytes;public List<Track> Tracks=[];
    public float MaxFrame=>Tracks.SelectMany(t=>t.Keys.Take(3)).SelectMany(k=>k).Select(k=>k[0]).DefaultIfEmpty().Max();
}
internal sealed class SkeletonData
{
    public byte Version;public long MetadataEnd;public int BundleBytes,DecompressedBytes;public List<Bone> Bones=[];public List<AnimationData> Animations=[];public Matrix4x4[] BindWorld=[];
    static Matrix4x4 Matrix(BinaryReader r)=>new(r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle());
    public static SkeletonData Read(byte[] bytes,IOodleCodec codec)
    {
        using var r=new BinaryReader(new MemoryStream(bytes));var s=new SkeletonData{Version=r.ReadByte()};int nb=r.ReadByte();r.ReadByte();int na=r.ReadUInt16();r.ReadByte();r.ReadByte();int nl=r.ReadByte();
        for(int i=0;i<nb;i++){int sibling=r.ReadByte(),child=r.ReadByte();var mat=Matrix(r);int len=r.ReadByte();if(s.Version>=8)r.ReadByte();var name=Encoding.UTF8.GetString(r.ReadBytes(len));s.Bones.Add(new Bone(name,sibling==255?-1:sibling,child==255?-1:child,mat));}
        for(int i=0;i<nl;i++){int len=r.ReadByte();r.ReadBytes(51);if(s.Version>=7)r.ReadBytes(4);if(s.Version>=9)r.ReadBytes(4);r.ReadBytes(len);}
        if(s.Version<8)throw new NotSupportedException("Proof reader currently expects nested animation bundle v8+");
        for(int i=0;i<na;i++)
        {
            var a=new AnimationData{TrackCount=r.ReadByte()};r.ReadByte();a.FrameRate=r.ReadByte();r.ReadByte();if(s.Version>=10)r.ReadByte();int len=r.ReadByte();int parentLen=s.Version>=11?r.ReadByte():0;
            a.Offset=r.ReadUInt32();a.Length=r.ReadUInt32();a.Name=Encoding.UTF8.GetString(r.ReadBytes(len));if(parentLen>0)r.ReadBytes(parentLen);s.Animations.Add(a);
        }
        s.MetadataEnd=r.BaseStream.Position;var bundle=bytes.AsSpan(checked((int)s.MetadataEnd)).ToArray();s.BundleBytes=bundle.Length;var tracks=BundleFile.Decompress(bundle,codec);s.DecompressedBytes=tracks.Length;
        var selected=s.Animations.Where(a=>a.Name.Contains("idle_unarmed",StringComparison.OrdinalIgnoreCase)).Take(1).Concat(s.Animations.Take(3)).Distinct().ToList();
        foreach(var a in selected)
        {
            using var tr=new BinaryReader(new MemoryStream(tracks));tr.BaseStream.Position=a.Offset;
            for(int i=0;i<a.TrackCount;i++)
            {
                tr.ReadByte();int index=checked((int)tr.ReadUInt32());var counts=Enumerable.Range(0,6).Select(_=>tr.ReadUInt32()).ToArray();if(s.Version>=10)tr.ReadUInt32();
                int[] widths=[4,5,4,4,5,4];var keys=new List<float[]>[6];for(int k=0;k<6;k++){keys[k]=[];for(int j=0;j<counts[k];j++)keys[k].Add(Enumerable.Range(0,widths[k]).Select(_=>tr.ReadSingle()).ToArray());}
                a.Tracks.Add(new Track(index,counts,keys));
            }
            a.ParsedBytes=tr.BaseStream.Position-a.Offset;if(a.ParsedBytes!=a.Length)throw new InvalidDataException($"Animation {a.Name} length mismatch parsed {a.ParsedBytes} expected {a.Length}");
        }
        // Child/sibling lists encode a tree. Detect malformed loops rather than recursing forever.
        for(int p=0;p<s.Bones.Count;p++){var seen=new HashSet<int>();for(int c=s.Bones[p].Child;c>=0;c=s.Bones[c].Sibling){if(c>=s.Bones.Count||!seen.Add(c))throw new InvalidDataException("Bad bone hierarchy");s.Bones[c].Parent=p;}}
        s.BindWorld=s.Compose(s.Bones.Select(b=>b.Local).ToArray());return s;
    }
    Matrix4x4[] Compose(Matrix4x4[] local)
    {
        var done=new bool[Bones.Count];var active=new bool[Bones.Count];var world=new Matrix4x4[Bones.Count];
        Matrix4x4 Visit(int i){if(done[i])return world[i];if(active[i])throw new InvalidDataException("Cyclic bone parent graph");active[i]=true;world[i]=Bones[i].Parent>=0?local[i]*Visit(Bones[i].Parent):local[i];active[i]=false;done[i]=true;return world[i];}
        for(int i=0;i<world.Length;i++)Visit(i);return world;
    }
    public Matrix4x4[] Pose(AnimationData a,float frame)
    {
        var local=Bones.Select(b=>b.Local).ToArray();
        foreach(var t in a.Tracks)
        {
            if((uint)t.Bone>=(uint)Bones.Count)throw new InvalidDataException("Track outside skeleton");
            Matrix4x4.Decompose(Bones[t.Bone].Local,out var bs,out var br,out var bp);
            Vector3 Vec(List<float[]> k,Vector3 fallback){if(k.Count==0)return fallback;var (l,r,f)=Bracket(k,frame);return Vector3.Lerp(new(l[1],l[2],l[3]),new(r[1],r[2],r[3]),f);}
            Quaternion Quat(List<float[]> k,Quaternion fallback){if(k.Count==0)return fallback;var (l,r,f)=Bracket(k,frame);return Quaternion.Normalize(Quaternion.Slerp(new(l[1],l[2],l[3],l[4]),new(r[1],r[2],r[3],r[4]),f));}
            local[t.Bone]=Matrix4x4.CreateScale(Vec(t.Keys[0],bs))*Matrix4x4.CreateFromQuaternion(Quat(t.Keys[1],br))*Matrix4x4.CreateTranslation(Vec(t.Keys[2],bp));
        }
        return Compose(local);
    }
    static (float[] Left,float[] Right,float Fraction) Bracket(List<float[]> keys,float time)
    {
        if(time<=keys[0][0])return(keys[0],keys[0],0);for(int i=1;i<keys.Count;i++)if(time<=keys[i][0])return(keys[i-1],keys[i],Math.Clamp((time-keys[i-1][0])/(keys[i][0]-keys[i-1][0]),0,1));return(keys[^1],keys[^1],0);
    }
}
