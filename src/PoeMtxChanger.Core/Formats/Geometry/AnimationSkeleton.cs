using System.Numerics;
using System.Text;
using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Formats.Bundles;

namespace PoeMtxChanger.Core.Formats.Geometry;

public sealed record SkeletonBone(string Name, int Parent, Matrix4x4 Local);
/// <summary>
/// A light authored in the skeleton (effect glows, portal fires): colour with its intensity, position in the
/// model's space and the cut-off radius. Layout of the 55-byte record (v8; v9+ adds 4 bytes): type byte, RGB,
/// position, direction, radius, spot parameters, flags.
/// </summary>
public sealed record SkeletonLight(string Name, Vector3 Colour, Vector3 Position, float Radius);
public sealed record AnimationClip(string Name, int FrameRate, int TrackCount, uint Offset, uint Length);
internal sealed record AnimationTrack(int Bone, float[][][] Keys);

/// <summary>AST skeletons v8–v12 and their nested Oodle animation bundle. Tracks are decoded on demand.</summary>
public sealed class AnimationSkeleton
{
    public byte Version { get; private init; }
    public required SkeletonBone[] Bones { get; init; }
    public required AnimationClip[] Clips { get; init; }
    public SkeletonLight[] Lights { get; private init; } = [];
    public Matrix4x4[] BindWorld { get; private set; } = [];
    private Matrix4x4[] _inverseBind = [];
    private byte[] _tracks = [];
    private bool _allowSupplementalTracks;
    private readonly Dictionary<AnimationClip, AnimationTrack[]> _decoded = [];

    public static AnimationSkeleton Read(byte[] bytes, IOodleCodec codec, bool allowSupplementalTracks = false)
    {
        using var r = new BinaryReader(new MemoryStream(bytes, false));
        byte version = r.ReadByte();
        if (version is < 8 or > 12) throw new NotSupportedException($"AST v{version}: animations in this format are not supported yet.");
        int boneCount = r.ReadByte(); r.ReadByte(); int animationCount = r.ReadUInt16(); r.ReadByte(); r.ReadByte(); int lights = r.ReadByte();
        var names = new string[boneCount]; var locals = new Matrix4x4[boneCount]; var children = new int[boneCount]; var siblings = new int[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            siblings[i] = r.ReadByte(); children[i] = r.ReadByte();
            float[] v = Enumerable.Range(0, 16).Select(_ => r.ReadSingle()).ToArray();
            if (v.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Invalid AST transform.");
            locals[i] = new(v[0],v[1],v[2],v[3],v[4],v[5],v[6],v[7],v[8],v[9],v[10],v[11],v[12],v[13],v[14],v[15]);
            int length = r.ReadByte(); r.ReadByte(); names[i] = ReadName(r, length);
        }
        var lightRecords = new List<SkeletonLight>();
        for (int i = 0; i < lights; i++)
        {
            int length = r.ReadByte();
            byte[] record = r.ReadBytes(55 + (version >= 9 ? 4 : 0));
            if (record.Length < 55) throw new EndOfStreamException();
            string lightName = ReadName(r, length);
            float F(int offset) => BitConverter.ToSingle(record, offset);
            var colour = new Vector3(F(1), F(5), F(9));
            var position = new Vector3(F(13), F(17), F(21));
            float radius = F(37);
            if (record[0] == 0 && float.IsFinite(colour.X + colour.Y + colour.Z + position.X + position.Y + position.Z + radius) && radius > 0)
                lightRecords.Add(new SkeletonLight(lightName, colour, position, radius));
        }
        var clips = new AnimationClip[animationCount];
        for (int i = 0; i < clips.Length; i++)
        {
            int tracks = r.ReadByte(); r.ReadByte(); int fps = r.ReadByte(); r.ReadByte(); if (version >= 10) r.ReadByte();
            int length = r.ReadByte(), parentLength = version >= 11 ? r.ReadByte() : 0;
            uint offset = r.ReadUInt32(), size = r.ReadUInt32();
            clips[i] = new(ReadName(r, length), fps, tracks, offset, size); Skip(r, parentLength);
        }
        var compressed = bytes.AsSpan(checked((int)r.BaseStream.Position)).ToArray();
        if (BundleHeader.Read(compressed).UncompressedSize > 256 * 1024 * 1024) throw new InvalidDataException("AST animation bundle exceeds supported size.");
        var data = BundleFile.Decompress(compressed, codec);
        if (data.Length > 256 * 1024 * 1024) throw new InvalidDataException("AST animation bundle exceeds supported size.");
        foreach (var clip in clips)
            if ((ulong)clip.Offset + clip.Length > (ulong)data.Length || clip.FrameRate == 0) throw new InvalidDataException("AST animation range/frequency is invalid.");
        var parents = Enumerable.Repeat(-1, boneCount).ToArray();
        for (int p = 0; p < boneCount; p++)
        {
            var seen = new HashSet<int>();
            for (int c = children[p]; c != 255; c = siblings[c])
            {
                if (c >= boneCount || c == p || !seen.Add(c) || parents[c] >= 0) throw new InvalidDataException("Invalid AST bone hierarchy.");
                parents[c] = p;
            }
        }
        var result = new AnimationSkeleton { Version = version, Bones = Enumerable.Range(0, boneCount).Select(i => new SkeletonBone(names[i], parents[i], locals[i])).ToArray(), Clips = clips, Lights = lightRecords.ToArray(), _tracks = data, _allowSupplementalTracks = allowSupplementalTracks };
        result.BindWorld = result.Compose(locals);
        result._inverseBind = result.BindWorld.Select(m => Matrix4x4.Invert(m, out var inverse) ? inverse : throw new InvalidDataException("Singular AST bind matrix.")).ToArray();
        return result;
    }
    /// <summary>A skeleton without animations from bones in parent-first order (tests).</summary>
    internal static AnimationSkeleton FromBones(SkeletonBone[] bones)
    {
        var result = new AnimationSkeleton { Bones = bones, Clips = [] };
        result.BindWorld = result.Compose(bones.Select(b => b.Local).ToArray());
        result._inverseBind = result.BindWorld.Select(m => Matrix4x4.Invert(m, out var inverse) ? inverse : Matrix4x4.Identity).ToArray();
        return result;
    }
    private static string ReadName(BinaryReader r, int n) { byte[] b = r.ReadBytes(n); if (b.Length != n) throw new EndOfStreamException(); return Encoding.UTF8.GetString(b); }
    private static void Skip(BinaryReader r, int n) { if (r.BaseStream.Position + n > r.BaseStream.Length) throw new EndOfStreamException(); r.BaseStream.Position += n; }

    private AnimationTrack[] Tracks(AnimationClip clip)
    {
        if (!Clips.Contains(clip)) throw new ArgumentException("Clip does not belong to this skeleton.");
        if (_decoded.TryGetValue(clip, out var cached)) return cached;
        using var r = new BinaryReader(new MemoryStream(_tracks, checked((int)clip.Offset), checked((int)clip.Length), false));
        var tracks = new List<AnimationTrack>(clip.TrackCount);
        int[] widths = [4,5,4,4,5,4]; var usedBones = new HashSet<int>();
        for (int i = 0; i < clip.TrackCount; i++)
        {
            byte type = r.ReadByte(); uint bone = r.ReadUInt32();
            var counts = Enumerable.Range(0,6).Select(_=>r.ReadUInt32()).ToArray();
            if (Version >= 10) r.ReadUInt32();
            if (bone >= Bones.Length) throw new InvalidDataException("Invalid AST animation bone.");
            bool supplemental = !usedBones.Add((int)bone);
            // Legacy ASTs can also store a secondary type-1 track for a bone that already has a transform.
            if (supplemental && (!_allowSupplementalTracks || type != 1))
                throw new InvalidDataException($"Duplicate AST v{Version} animation bone {Bones[bone].Name} in {clip.Name} (type {type}, keys {string.Join(",", counts)}).");
            var keys = new float[6][][];
            for (int k = 0; k < 6; k++)
            {
                if (counts[k] > (r.BaseStream.Length - r.BaseStream.Position) / (widths[k] * 4L)) throw new InvalidDataException("Truncated AST keyframes.");
                keys[k] = new float[counts[k]][];
                for (int j = 0; j < keys[k].Length; j++)
                {
                    var key = Enumerable.Range(0,widths[k]).Select(_=>r.ReadSingle()).ToArray();
                    if (key.Any(x=>!float.IsFinite(x)) || (j > 0 && key[0] <= keys[k][j-1][0])) throw new InvalidDataException("Invalid AST animation key.");
                    keys[k][j] = key;
                }
            }
            if (!_allowSupplementalTracks && keys.Skip(3).Any(k=>k.Length>0))
                throw new NotSupportedException("This animation's additional AST tracks have not been researched yet.");
            if (!supplemental) tracks.Add(new((int)bone, keys));
        }
        if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("AST clip length mismatch.");
        if(_decoded.Count>=4)_decoded.Remove(_decoded.Keys.First());
        return _decoded[clip] = tracks.ToArray();
    }
    public float Duration(AnimationClip clip) => Tracks(clip).SelectMany(t=>t.Keys.Take(3)).SelectMany(k=>k).Select(k=>k[0]).DefaultIfEmpty(0).Max() / clip.FrameRate;

    public bool HasSupplementalTracks(AnimationClip clip) => Tracks(clip).Any(t=>t.Keys.Skip(3).Any(k=>k.Length>0));

    public Matrix4x4[] Skinning(AnimationClip? clip, float seconds)
    {
        if (clip == null) return Enumerable.Repeat(Matrix4x4.Identity, Bones.Length).ToArray();
        var locals = Bones.Select(b=>b.Local).ToArray();
        float frame = seconds * clip.FrameRate;
        foreach (var t in Tracks(clip))
        {
            if (!Matrix4x4.Decompose(Bones[t.Bone].Local,out var scale,out var rotation,out var position)) throw new InvalidDataException("AST transform cannot be decomposed.");
            var a = Sample(t.Keys[0],frame); if(a is {} s) scale = new(s[0],s[1],s[2]);
            var b = Sample(t.Keys[1],frame,true); if(b is {} q) rotation = new(q[0],q[1],q[2],q[3]);
            var c = Sample(t.Keys[2],frame); if(c is {} p) position = new(p[0],p[1],p[2]);
            locals[t.Bone] = Matrix4x4.CreateScale(scale)*Matrix4x4.CreateFromQuaternion(rotation)*Matrix4x4.CreateTranslation(position);
        }
        var world = Compose(locals);
        return world.Select((m,i)=>_inverseBind[i]*m).ToArray();
    }
    private static float[]? Sample(float[][] keys,float frame,bool quaternion=false)
    {
        if(keys.Length==0)return null;
        int right=Array.FindIndex(keys,k=>k[0]>=frame); if(right<0)right=keys.Length-1;
        int left=Math.Max(0,right-1); float amount=right==left||frame<=keys[left][0]?0:Math.Clamp((frame-keys[left][0])/(keys[right][0]-keys[left][0]),0,1);
        if(quaternion)
        {
            var a=new Quaternion(keys[left][1],keys[left][2],keys[left][3],keys[left][4]);var b=new Quaternion(keys[right][1],keys[right][2],keys[right][3],keys[right][4]);
            if(a.LengthSquared()<0.0001f||b.LengthSquared()<0.0001f)throw new InvalidDataException("Zero AST rotation.");
            var q=Quaternion.Normalize(Quaternion.Slerp(Quaternion.Normalize(a),Quaternion.Normalize(b),amount));return[q.X,q.Y,q.Z,q.W];
        }
        return Enumerable.Range(1,keys[left].Length-1).Select(i=>keys[left][i]+(keys[right][i]-keys[left][i])*amount).ToArray();
    }
    private Matrix4x4[] Compose(Matrix4x4[] local)
    {
        var done = new bool[Bones.Length]; var active = new bool[Bones.Length]; var world = new Matrix4x4[Bones.Length];
        Matrix4x4 Visit(int i)
        {
            if(done[i]) return world[i]; if(active[i])throw new InvalidDataException("Cyclic AST parent graph.");
            active[i]=true;world[i]=Bones[i].Parent>=0?local[i]*Visit(Bones[i].Parent):local[i]; active[i]=false; done[i]=true; return world[i];
        }
        for(int i=0;i<world.Length;i++)Visit(i);return world;
    }
}
