using System.Buffers.Binary;
using System.Text;

namespace PoeMtxChanger.Core.Formats.Dat;

/// <summary>The schema's row length does not match the file: the schema is outdated for this game version.</summary>
public sealed class DatSchemaMismatchException(string table, int expected, int actual)
    : FormatException($"Schema for {table} describes {expected}-byte rows, the file has {actual}-byte rows. The schema is out of date for this game version.")
{
    public string Table { get; } = table;
    public int Expected { get; } = expected;
    public int Actual { get; } = actual;
}

/// <summary>
/// Read-only .datc64 table: uint32 row count, fixed-width rows, then a variable-data section that starts with
/// eight 0xBB bytes. Strings/arrays store 64-bit offsets relative to the start of that section.
/// </summary>
public sealed class DatTable
{
    public static ReadOnlySpan<byte> VariableDataMagic => [0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB];
    private const ulong NullKey = 0xFEFEFEFEFEFEFEFEUL;

    private readonly byte[] _data;
    private readonly int _fixedStart;
    private readonly int _variableStart;
    private readonly int[] _offsets;
    private readonly Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);

    public DatTableSchema Schema { get; }
    public int RowCount { get; }
    public int RowLength { get; }

    private DatTable(byte[] data, DatTableSchema schema, int rowCount, int rowLength, int variableStart)
    {
        _data = data;
        Schema = schema;
        RowCount = rowCount;
        RowLength = rowLength;
        _fixedStart = 4;
        _variableStart = variableStart;
        _offsets = new int[schema.Columns.Count];
        int o = 0;
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            _offsets[i] = o;
            o += schema.Columns[i].FixedSize;
            if (schema.Columns[i].Name is { } n) _byName.TryAdd(n, i);
        }
    }

    public static DatTable Parse(byte[] data, DatTableSchema schema)
    {
        if (data.Length < 12) throw new InvalidDataException("Dat file is too small.");
        int rowCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data));
        var body = data.AsSpan(4);
        int boundary = -1, from = 0;
        while (true)
        {
            int idx = body[from..].IndexOf(VariableDataMagic);
            if (idx < 0) break;
            idx += from;
            if (rowCount == 0 || idx % rowCount == 0) { boundary = idx; break; }
            from = idx + 1;
        }
        if (boundary < 0) throw new InvalidDataException("Dat file has no variable-data section.");
        int rowLength = rowCount == 0 ? schema.RowLength : boundary / rowCount;
        if (rowCount > 0 && rowLength != schema.RowLength)
            throw new DatSchemaMismatchException(schema.Name, schema.RowLength, rowLength);
        return new DatTable(data, schema, rowCount, rowLength, 4 + boundary);
    }

    public bool HasColumn(string name) => _byName.ContainsKey(name);

    private (DatColumn Col, int Pos) Locate(int row, string column)
    {
        if ((uint)row >= (uint)RowCount) throw new ArgumentOutOfRangeException(nameof(row));
        if (!_byName.TryGetValue(column, out var ci)) throw new KeyNotFoundException($"{Schema.Name} has no column '{column}'.");
        return (Schema.Columns[ci], _fixedStart + row * RowLength + _offsets[ci]);
    }

    private static void Expect(DatColumn c, bool array, params DatColumnType[] types)
    {
        if (c.IsArray != array || !types.Contains(c.Type))
            throw new InvalidOperationException($"Column '{c.Name}' is {c.Type}{(c.IsArray ? "[]" : "")}, not {string.Join("/", types)}{(array ? "[]" : "")}.");
    }

    public string GetString(int row, string column)
    {
        var (c, pos) = Locate(row, column);
        Expect(c, false, DatColumnType.String);
        return ReadStringAt(BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(pos)));
    }

    public int GetInt32(int row, string column)
    {
        var (c, pos) = Locate(row, column);
        Expect(c, false, DatColumnType.I32, DatColumnType.U32, DatColumnType.EnumRow);
        return BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(pos));
    }

    public bool GetBool(int row, string column)
    {
        var (c, pos) = Locate(row, column);
        Expect(c, false, DatColumnType.Bool);
        return _data[pos] != 0;
    }

    /// <summary>Row index stored in a row/foreignrow column, or null for the 0xFE.. null marker.</summary>
    public int? GetRowRef(int row, string column)
    {
        var (c, pos) = Locate(row, column);
        Expect(c, false, DatColumnType.Row, DatColumnType.ForeignRow);
        return DecodeKey(BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(pos)));
    }

    public IReadOnlyList<string> GetStringArray(int row, string column)
    {
        var (c, pos) = Locate(row, column);
        Expect(c, true, DatColumnType.String);
        var (count, offset) = ArrayHeader(pos, 8);
        var list = new List<string>(count);
        for (int i = 0; i < count; i++)
            list.Add(ReadStringAt(BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(_variableStart + offset + i * 8))));
        return list;
    }

    public IReadOnlyList<int?> GetRowRefArray(int row, string column)
    {
        var (c, pos) = Locate(row, column);
        Expect(c, true, DatColumnType.Row, DatColumnType.ForeignRow);
        int el = c.Type == DatColumnType.ForeignRow ? 16 : 8;
        var (count, offset) = ArrayHeader(pos, el);
        var list = new List<int?>(count);
        for (int i = 0; i < count; i++)
            list.Add(DecodeKey(BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(_variableStart + offset + i * el))));
        return list;
    }

    public IReadOnlyList<int> GetInt32Array(int row, string column)
    {
        var (c, pos) = Locate(row, column);
        Expect(c, true, DatColumnType.I32);
        var (count, offset) = ArrayHeader(pos, sizeof(int));
        var values = new int[count];
        var data = _data.AsSpan(_variableStart + offset);
        for (int i = 0; i < count; i++)
            values[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(i * sizeof(int))..]);
        return values;
    }

    private (int Count, int Offset) ArrayHeader(int pos, int elementSize)
    {
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(pos));
        ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(pos + 8));
        if (count == 0) return (0, 0);
        long end = _variableStart + (long)offset + (long)count * elementSize;
        if (count > int.MaxValue || offset > int.MaxValue || end > _data.Length)
            throw new InvalidDataException("Dat array points outside the variable-data section.");
        return ((int)count, (int)offset);
    }

    private static int? DecodeKey(ulong v)
    {
        if (v == NullKey || (v & 0xFFFFFFFFUL) == 0xFEFEFEFEUL || v > int.MaxValue) return null;
        return (int)v;
    }

    private string ReadStringAt(ulong offset)
    {
        if (offset > int.MaxValue) throw new InvalidDataException("Dat string offset out of range.");
        int start = _variableStart + (int)offset;
        if (start > _data.Length) throw new InvalidDataException("Dat string offset out of range.");
        // UTF-16LE, terminated by four zero bytes at an even distance from the start.
        int p = start;
        while (p + 3 < _data.Length)
        {
            if (_data[p] == 0 && _data[p + 1] == 0 && _data[p + 2] == 0 && _data[p + 3] == 0) break;
            p += 2;
        }
        if (p + 3 >= _data.Length) throw new InvalidDataException("Unterminated dat string.");
        return Encoding.Unicode.GetString(_data, start, p - start);
    }
}
