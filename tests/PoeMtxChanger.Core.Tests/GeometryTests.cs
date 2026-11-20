using System.Numerics;
using System.Text;
using PoeMtxChanger.Core.Formats.Bundles;
using PoeMtxChanger.Core.Formats.Geometry;
using PoeMtxChanger.Core.Compression;

namespace PoeMtxChanger.Core.Tests;

public class GeometryTests
{
    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Reads_fixed_particle_geometry_and_rejects_invalid_indices(byte version)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        bool twoShapes = version == 7;
        writer.Write(version);
        if(version==9)
        {
            writer.Write((ushort)1);
            writer.Write(new byte[28]);
            writer.Write("DOLm"u8);
            writer.Write((ushort)4);
            writer.Write((byte)1);
            writer.Write((ushort)1);
            writer.Write(0x238u);
            writer.Write(1u);
            writer.Write(3u);
            writer.Write(0u);
            writer.Write(3u);
        }
        else
        {
            writer.Write(twoShapes ? 2u : 1u);
            writer.Write(3u);
            writer.Write((ushort)(twoShapes ? 2 : 1));
            writer.Write(new byte[version==8?36:32]);
            writer.Write(0u);
            writer.Write(0u);
            if(twoShapes)
            {
                writer.Write(100u);
                writer.Write(0u);
                writer.Write(1u);
            }
        }
        int firstIndex = (int)output.Position;
        for(ushort index=0;index<3;index++)writer.Write(index);
        if(twoShapes)for(ushort index=0;index<3;index++)writer.Write(index);
        foreach(var p in new[]{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ})
        {
            writer.Write(p.X);
            writer.Write(p.Y);
            writer.Write(p.Z);
            writer.Write(new byte[8]);
            writer.Write(BitConverter.HalfToUInt16Bits((Half).25));
            writer.Write(BitConverter.HalfToUInt16Bits((Half).5));
        }
        byte[] bytes = output.ToArray();
        var mesh = FixedParticleMesh.Read(bytes);
        Assert.Equal(twoShapes ? [0,1,2,0,1,2] : [0,1,2],mesh.Indices);
        Assert.Equal(Vector3.UnitY,mesh.Vertices[1].Position);
        Assert.Equal(new Vector2(.25f,.5f),mesh.Vertices[1].Uv);
        bytes[firstIndex] = 3;
        Assert.Throws<InvalidDataException>(()=>FixedParticleMesh.Read(bytes));
    }

    private static byte[] Mesh(byte version)
    {
        using var output=new MemoryStream();using var w=new BinaryWriter(output);
        byte[] name=Encoding.Unicode.GetBytes("shape");
        w.Write(version);
        if(version<3){w.Write(1);w.Write(3);}
        w.Write((byte)4);w.Write((ushort)1);w.Write(name.Length);for(int i=0;i<6;i++)w.Write(0f);
        if(version==2)w.Write(0);
        if(version==3){w.Write("DOLm"u8);w.Write((ushort)1);w.Write((byte)1);w.Write((ushort)1);w.Write(12);w.Write(1);w.Write(3);w.Write(0);w.Write(3);}
        else{w.Write(name.Length);w.Write(0);w.Write(name);}
        for(ushort i=0;i<3;i++)w.Write(i);
        foreach(var p in new[]{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ})
        {w.Write(p.X);w.Write(p.Y);w.Write(p.Z);w.Write(new byte[8]);w.Write((ushort)0);w.Write((ushort)0);w.Write(new byte[]{0,0,0,0});w.Write(new byte[]{255,0,0,0});}
        if(version==3){w.Write(name.Length);w.Write(name);}
        return output.ToArray();
    }
    private static byte[] Skeleton(bool cyclic=false)
    {
        using var raw=new MemoryStream();using(var t=new BinaryWriter(raw,Encoding.UTF8,true))
        {t.Write((byte)0);t.Write(1u);foreach(uint count in new uint[]{0,0,2,0,0,0})t.Write(count);foreach(float v in new[]{0f,1f,0f,0f,24f,3f,0f,0f})t.Write(v);}
        byte[] compressed=BundleFile.Compress(raw.ToArray(),SyntheticGame.Codec,OodleCompressor.Mermaid,OodleCompressionLevel.Normal);
        using var output=new MemoryStream();using var w=new BinaryWriter(output);
        w.Write((byte)8);w.Write((byte)2);w.Write((byte)0);w.Write((ushort)1);w.Write(new byte[3]);
        for(int i=0;i<2;i++)
        {w.Write((byte)255);w.Write((byte)(i==0?1:cyclic?0:255));var m=i==0?Matrix4x4.Identity:Matrix4x4.CreateTranslation(1,0,0);foreach(float f in new[]{m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44})w.Write(f);w.Write((byte)1);w.Write((byte)0);w.Write((byte)('a'+i));}
        w.Write((byte)1);w.Write((byte)0);w.Write((byte)24);w.Write((byte)0);w.Write((byte)4);w.Write(0u);w.Write((uint)raw.Length);w.Write("idle"u8);w.Write(compressed);return output.ToArray();
    }
    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)] [InlineData((byte)3)]
    public void Mesh_versions_preserve_triangle_ranges_and_validate_indices(byte version)
    {
        var mesh=SkinnedMesh.Read(Mesh(version));Assert.Equal(new[]{0,1,2},mesh.Indices);Assert.Equal("shape",Assert.Single(mesh.Shapes).Name);
        Assert.Equal(new[]{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ},mesh.Positions());
        Assert.Equal(mesh.Positions(),mesh.Positions([Matrix4x4.Identity]));
        Assert.Throws<InvalidDataException>(()=>mesh.Positions([]));
    }
    [Fact]
    public void Animated_pose_uses_inverse_bind_and_frame_rate()
    {
        var skeleton=AnimationSkeleton.Read(Skeleton(),SyntheticGame.Codec);var clip=Assert.Single(skeleton.Clips);
        Assert.Equal(1,skeleton.Duration(clip));
        Assert.Equal(new Vector3(1,0,0),Vector3.Transform(Vector3.UnitX,skeleton.Skinning(clip,0)[1]));
        Assert.Equal(new Vector3(2,0,0),Vector3.Transform(Vector3.UnitX,skeleton.Skinning(clip,.5f)[1]));
        Assert.Equal(new Vector3(3,0,0),Vector3.Transform(Vector3.UnitX,skeleton.Skinning(clip,1)[1]));
        Assert.Equal(Matrix4x4.Identity,skeleton.Skinning(null,0)[1]);
    }
    [Fact]
    public void Skeleton_cycles_and_truncated_meshes_are_rejected()
    {
        Assert.Throws<InvalidDataException>(()=>AnimationSkeleton.Read(Skeleton(true),SyntheticGame.Codec));
        Assert.Throws<InvalidDataException>(()=>SkinnedMesh.Read(Mesh(1)[..50]));
    }
}
