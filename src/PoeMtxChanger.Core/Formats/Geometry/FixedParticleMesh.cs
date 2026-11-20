using System.Numerics;

namespace PoeMtxChanger.Core.Formats.Geometry;

public readonly record struct FixedParticleVertex(Vector3 Position, Vector3 Normal, Vector2 Uv);

/// <summary>Fixed mesh geometry used by legacy, single-material particle emitters.</summary>
public sealed record FixedParticleMesh(FixedParticleVertex[] Vertices, int[] Indices)
{
    public static FixedParticleMesh Read(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data, writable: false));
        byte version = reader.ReadByte();
        if (version == 9) return ReadLod(reader);
        if (version is < 6 or > 8) throw new NotSupportedException($"FMT v{version}: this particle format is not supported yet.");
        uint triangleCount = reader.ReadUInt32();
        uint vertexCount = reader.ReadUInt32();
        ushort shapeCount = reader.ReadUInt16();
        uint layout = reader.ReadUInt32();
        if (shapeCount is < 1 or > 256 || layout != 0)
            throw new NotSupportedException("The FMT particle format with additional attributes is not supported yet.");
        reader.BaseStream.Position = version < 8 ? 39 : 43;
        uint previous = 0;
        for (int i = 0; i < shapeCount; i++)
        {
            reader.ReadUInt32(); // additional shape metadata
            reader.ReadUInt32(); // name length
            uint first = reader.ReadUInt32();
            if (first < previous || first >= triangleCount)
                throw new InvalidDataException("Invalid FMT shape range.");
            previous = first;
        }
        return ReadGeometry(reader, triangleCount, vertexCount, 0x8);
    }

    private static FixedParticleMesh ReadLod(BinaryReader reader)
    {
        int shapeCount = reader.ReadUInt16();
        reader.ReadUInt32();
        reader.BaseStream.Position = 31;
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("DOLm"u8))
            throw new InvalidDataException("FMT mesh has no DOLm section.");
        ushort header = reader.ReadUInt16();
        int levels = reader.ReadByte(), lodShapes = reader.ReadUInt16();
        uint format = reader.ReadUInt32();
        if (header is < 1 or > 4 || levels is < 1 or > 32 || lodShapes != shapeCount || shapeCount is < 1 or > 256
            || (format & ~0x23bu) != 0)
            throw new NotSupportedException($"FMT v9 with layout 0x{format:X} is not supported yet.");

        uint triangles = reader.ReadUInt32(), vertices = reader.ReadUInt32();
        reader.BaseStream.Position += (levels - 1) * 8L;
        for (int i = 0; i < shapeCount; i++)
        {
            uint first = reader.ReadUInt32(), count = reader.ReadUInt32();
            if (first % 3 != 0 || count % 3 != 0 || (ulong)first + count > triangles * 3UL)
                throw new InvalidDataException("Invalid FMT shape range.");
        }
        return ReadGeometry(reader, triangles, vertices, format);
    }

    private static FixedParticleMesh ReadGeometry(BinaryReader reader, uint triangleCount, uint vertexCount, uint layout)
    {
        if (triangleCount is 0 or > 16384 || vertexCount is 0 or > 8192)
            throw new InvalidDataException("Invalid FMT geometry counts.");

        bool hasUv = (layout & 8) != 0;
        int extra = ((layout & 2) != 0 ? 4 : 0) + ((layout & 1) != 0 ? 4 : 0);
        int vertexStride = 20 + (hasUv ? 4 : 0) + extra;
        long required = reader.BaseStream.Position + triangleCount * 6L + vertexCount * vertexStride;
        if (required > reader.BaseStream.Length) throw new InvalidDataException("Truncated FMT geometry.");
        var indices = new int[triangleCount * 3];
        for (int i = 0; i < indices.Length; i++)
        {
            int index = reader.ReadUInt16();
            if (index >= vertexCount) throw new InvalidDataException("FMT index exceeds vertex count.");
            indices[i] = index;
        }
        var vertices = new FixedParticleVertex[vertexCount];
        for (int i = 0; i < vertices.Length; i++)
        {
            var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var normal = new Vector3(reader.ReadSByte(), reader.ReadSByte(), reader.ReadSByte()) / 127f;
            reader.ReadByte();
            reader.ReadUInt32(); // packed tangent
            var uv = hasUv ? new Vector2((float)BitConverter.UInt16BitsToHalf(reader.ReadUInt16()),
                (float)BitConverter.UInt16BitsToHalf(reader.ReadUInt16())) : Vector2.Zero;
            reader.BaseStream.Position += extra;
            if (!float.IsFinite(position.LengthSquared()) || !float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
                throw new InvalidDataException("Non-finite FMT vertex.");
            vertices[i] = new(position, normal, uv);
        }
        return new(vertices, indices);
    }
}
