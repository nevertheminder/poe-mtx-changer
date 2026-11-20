using System.Numerics;
using System.Text;

namespace PoeMtxChanger.Core.Formats.Geometry;

public sealed record SkinVertex(Vector3 Position, Vector3 Normal, Vector2 Uv, byte[] Bones, byte[] Weights);
public sealed record MeshShape(string Name, int FirstTriangle, int TriangleCount);

/// <summary>PoE 1 SMD v1/v2 and v3 DOLm geometry, independently decoded from measured layouts.</summary>
public sealed record SkinnedMesh(byte Version, byte Flags, SkinVertex[] Vertices, int[] Indices, MeshShape[] Shapes)
{
    public static SkinnedMesh Read(byte[] bytes)
    {
        using var r = new BinaryReader(new MemoryStream(bytes, false));
        byte version = r.ReadByte();
        if (version == 3) return ReadLods(r);
        if (version is not (1 or 2)) throw new NotSupportedException($"SMD v{version}: the format is not supported yet.");
        uint triangles = r.ReadUInt32(), vertices = r.ReadUInt32();
        byte flags = r.ReadByte(); int shapes = r.ReadUInt16(); uint nameBytes = r.ReadUInt32();
        if ((flags & ~7) != 0 || triangles > 2_000_000 || vertices > 1_000_000 || shapes > 4096 || nameBytes > 1_000_000)
            throw new InvalidDataException("SMD counts/flags exceed supported bounds.");
        for (int i = 0; i < 6; i++) if (!float.IsFinite(r.ReadSingle())) throw new InvalidDataException("Invalid SMD bounds.");
        if (version == 2) r.ReadUInt32();
        var headers = new (int Length, int Start)[shapes]; long totalNames = 0;
        for (int i = 0; i < shapes; i++)
        {
            uint length = r.ReadUInt32(), start = r.ReadUInt32();
            if ((length & 1) != 0 || length > 8192 || start > triangles || (i > 0 && start < headers[i - 1].Start))
                throw new InvalidDataException("Invalid SMD shape range.");
            headers[i] = ((int)length, (int)start); totalNames += length;
        }
        if (totalNames != nameBytes) throw new InvalidDataException("SMD shape name lengths do not match.");
        int stride = 32 + ((flags & 2) != 0 ? 4 : 0) + ((flags & 1) != 0 ? 4 : 0);
        long required = totalNames + triangles * 3L * (vertices < 65536 ? 2 : 4) + vertices * stride;
        if (required > bytes.Length - r.BaseStream.Position) throw new InvalidDataException("Truncated SMD geometry.");
        var parts = new MeshShape[shapes];
        for (int i = 0; i < shapes; i++) parts[i] = new(Encoding.Unicode.GetString(r.ReadBytes(headers[i].Length)).TrimEnd('\0'),
            headers[i].Start, (i + 1 < shapes ? headers[i + 1].Start : (int)triangles) - headers[i].Start);
        var indices = new int[checked((int)triangles * 3)];
        for (int i = 0; i < indices.Length; i++)
        {
            uint index = vertices < 65536 ? r.ReadUInt16() : r.ReadUInt32();
            if (index >= vertices) throw new InvalidDataException("SMD index exceeds vertex count.");
            indices[i] = (int)index;
        }
        var data = new SkinVertex[vertices];
        for (int i = 0; i < data.Length; i++)
        {
            var position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var normal = new Vector3(r.ReadSByte() / 127f, r.ReadSByte() / 127f, r.ReadSByte() / 127f);
            r.ReadByte(); r.ReadUInt32(); // fourth normal component; packed tangent
            var uv = new Vector2((float)BitConverter.UInt16BitsToHalf(r.ReadUInt16()), (float)BitConverter.UInt16BitsToHalf(r.ReadUInt16()));
            byte[] bones = r.ReadBytes(4), weights = r.ReadBytes(4);
            if ((flags & 2) != 0) r.ReadUInt32(); // additional skin attribute: preserved layout, no guessed interpretation
            if ((flags & 1) != 0) r.ReadUInt32(); // UV1
            if (!Finite(position) || !float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) throw new InvalidDataException("Non-finite SMD vertex.");
            data[i] = new(position, normal, uv, bones, weights);
        }
        if (shapes == 0 && triangles > 0) parts = [new("mesh", 0, (int)triangles)];
        return new(version, flags, data, indices, parts);
    }
    private static SkinnedMesh ReadLods(BinaryReader r)
    {
        byte flags=r.ReadByte();int shapeCount=r.ReadUInt16();uint nameBytes=r.ReadUInt32();
        for(int i=0;i<6;i++)if(!float.IsFinite(r.ReadSingle()))throw new InvalidDataException("Invalid SMD bounds.");
        if(shapeCount>4096||nameBytes>1_000_000||!r.ReadBytes(4).AsSpan().SequenceEqual("DOLm"u8))throw new InvalidDataException("Invalid SMD v3 DOLm section.");
        ushort header=r.ReadUInt16();int levels=r.ReadByte(),innerShapes=r.ReadUInt16();uint format=r.ReadUInt32();
        // Bit 9 is present on the current base weapon meshes and does not alter the vertex stride.
        if(levels is <1 or >32||innerShapes!=shapeCount||(format&~0x27fu)!=0||header is <1 or >4)
            throw new NotSupportedException($"Unknown DOLm structure (header={header}, format=0x{format:X}).");
        var counts=new (uint Triangles,uint Vertices)[levels];
        for(int i=0;i<levels;i++){counts[i]=(r.ReadUInt32(),r.ReadUInt32());if(counts[i].Triangles>2_000_000||counts[i].Vertices>1_000_000)throw new InvalidDataException("DOLm counts too large.");}
        int stride=20+((format&8)!=0?4:0)+((format&4)!=0?8:0)+((format&2)!=0?4:0)+((format&1)!=0?4:0)+((format&64)!=0?4:0);
        int[] indices=[];SkinVertex[] vertices=[];var ranges=new (int First,int Count)[shapeCount];
        for(int lod=0;lod<levels;lod++)
        {
            var count=counts[lod];long bytes=shapeCount*8L+count.Triangles*3L*(count.Vertices<65536?2:4)+count.Vertices*stride;
            if(bytes>r.BaseStream.Length-r.BaseStream.Position)throw new InvalidDataException("Truncated DOLm LOD.");
            for(int i=0;i<shapeCount;i++)
            {
                uint first=r.ReadUInt32(),length=r.ReadUInt32();
                if(first%3!=0||length%3!=0||(ulong)first+length>count.Triangles*3UL)throw new InvalidDataException("Invalid DOLm shape extent.");
                if(lod==0)ranges[i]=((int)first/3,(int)length/3);
            }
            if(lod!=0){r.BaseStream.Position+=count.Triangles*3L*(count.Vertices<65536?2:4)+count.Vertices*stride;continue;}
            indices=new int[count.Triangles*3];
            for(int i=0;i<indices.Length;i++){uint index=count.Vertices<65536?r.ReadUInt16():r.ReadUInt32();if(index>=count.Vertices)throw new InvalidDataException("Invalid DOLm vertex index.");indices[i]=(int)index;}
            vertices=new SkinVertex[count.Vertices];
            for(int i=0;i<vertices.Length;i++)
            {
                var p=new Vector3(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());
                var n=new Vector3(r.ReadSByte()/127f,r.ReadSByte()/127f,r.ReadSByte()/127f);r.ReadByte();r.ReadUInt32();
                var uv=(format&8)!=0?new Vector2((float)BitConverter.UInt16BitsToHalf(r.ReadUInt16()),(float)BitConverter.UInt16BitsToHalf(r.ReadUInt16())):Vector2.Zero;
                byte[] bones=(format&4)!=0?r.ReadBytes(4):new byte[4],weights=(format&4)!=0?r.ReadBytes(4):new byte[4];
                if((format&2)!=0)r.ReadUInt32();if((format&1)!=0)r.ReadUInt32();if((format&64)!=0)r.ReadUInt32();
                if(!Finite(p)||!float.IsFinite(uv.X)||!float.IsFinite(uv.Y))throw new InvalidDataException("Non-finite DOLm vertex.");
                vertices[i]=new(p,n,uv,bones,weights);
            }
        }
        long extra=((format&64)!=0?shapeCount*(36L+(header==2?4:0)):0)+(header==4?4:0);
        if(extra>r.BaseStream.Length-r.BaseStream.Position)throw new InvalidDataException("Truncated DOLm extra data.");r.BaseStream.Position+=extra;
        var lengths=new uint[shapeCount];long total=0;for(int i=0;i<shapeCount;i++){lengths[i]=r.ReadUInt32();total+=lengths[i];if(lengths[i]>8192||(lengths[i]&1)!=0)throw new InvalidDataException("Invalid DOLm shape name.");}
        if(total!=nameBytes||total>r.BaseStream.Length-r.BaseStream.Position)throw new InvalidDataException("SMD v3 shape name bounds mismatch.");
        var shapes=new MeshShape[shapeCount];for(int i=0;i<shapeCount;i++)shapes[i]=new(Encoding.Unicode.GetString(r.ReadBytes((int)lengths[i])).TrimEnd('\0'),ranges[i].First,ranges[i].Count);
        return new(3,flags,vertices,indices,shapes);
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    public Vector3[] Positions(Matrix4x4[]? skinning = null)
    {
        var positions = new Vector3[Vertices.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            var vertex = Vertices[i];
            if (skinning == null) { positions[i] = vertex.Position; continue; }
            int total = 0; Vector3 value = default;
            for (int j = 0; j < 4; j++)
            {
                if (vertex.Weights[j] == 0) continue;
                if (vertex.Bones[j] >= skinning.Length) throw new InvalidDataException("Mesh bone is absent from the selected skeleton.");
                value += Vector3.Transform(vertex.Position, skinning[vertex.Bones[j]]) * vertex.Weights[j]; total += vertex.Weights[j];
            }
            positions[i] = total == 0 ? vertex.Position : value / total;
        }
        return positions;
    }
}
