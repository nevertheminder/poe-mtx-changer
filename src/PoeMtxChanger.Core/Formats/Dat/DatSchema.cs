using System.Reflection;
using System.Text.Json;

namespace PoeMtxChanger.Core.Formats.Dat;

public enum DatColumnType { Bool, String, I16, U16, I32, U32, F32, Array, Row, ForeignRow, EnumRow }

public sealed record DatColumn(string? Name, DatColumnType Type, bool IsArray, bool Interval, string? ReferencedTable, string? FileExtension)
{
    /// <summary>Size of one scalar value of this column's type in a .datc64 file.</summary>
    public int ScalarSize => Type switch
    {
        DatColumnType.Bool => 1,
        DatColumnType.I16 or DatColumnType.U16 => 2,
        DatColumnType.I32 or DatColumnType.U32 or DatColumnType.F32 or DatColumnType.EnumRow => 4,
        DatColumnType.String or DatColumnType.Row => 8,
        DatColumnType.ForeignRow => 16,
        DatColumnType.Array => 0, // element type unknown
        _ => throw new InvalidDataException($"Unknown column type {Type}"),
    };

    /// <summary>Bytes the column occupies in the fixed-width row.</summary>
    public int FixedSize => IsArray ? 16 : ScalarSize * (Interval ? 2 : 1);
}

public sealed class DatTableSchema(string name, IReadOnlyList<DatColumn> columns)
{
    public string Name { get; } = name;
    public IReadOnlyList<DatColumn> Columns { get; } = columns;
    public int RowLength { get; } = columns.Sum(c => c.FixedSize);

    /// <summary>Path of the table inside the game index.</summary>
    public string GamePath => $"Data/{Name}.datc64";
}

/// <summary>
/// Table definitions in the community "dat-schema" JSON format (github.com/poe-tool-dev/dat-schema, MIT).
/// A copy for Path of Exile 1 is embedded; users can point the app at a newer file after a game update.
/// </summary>
public sealed class DatSchema
{
    public int Version { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyDictionary<string, DatTableSchema> Tables { get; init; } = new Dictionary<string, DatTableSchema>();
    public string Source { get; init; } = "";

    public const int SupportedVersion = 7;
    private const int ValidForPoE1 = 0x01;

    private static DatSchema? _current;

    /// <summary>
    /// Schema the app is currently working with: the user's newer file from Settings when one is configured,
    /// otherwise the embedded copy. Writers (e.g. the ItemVisualIdentity editor) must use the same schema that
    /// produced the catalog and plans; the app sets this whenever it (re)loads the catalog.
    /// </summary>
    public static DatSchema Current
    {
        get => _current ??= LoadDefault();
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static DatSchema LoadDefault()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PoeMtxChanger.Core.DatSchemaPoE1.json")
            ?? throw new InvalidOperationException("Embedded dat schema missing.");
        return WithKnownAliases(Load(s, "embedded dat-schema (PoE1)"));
    }

    /// <summary>
    /// Names known columns the upstream schema omits. Applied to every loaded schema (embedded or user file) so
    /// a newer schema from Settings keeps them; each alias only applies when the table's exact column layout matches.
    /// </summary>
    public static DatSchema WithKnownAliases(DatSchema schema)
    {
        // The upstream snapshot omits these names. The beam and pack layouts are cross-checked against
        // PathOfBuilding's primary Export/spec.lua and current local rows; never rename an arbitrary integer.
        var tables = new Dictionary<string, DatTableSchema>(schema.Tables, StringComparer.OrdinalIgnoreCase);
        Alias("MiscEffectPacksArtVariations", 3, 2, "EffectPack", "MiscEffectPacks");
        Alias("MiscBeamsArtVariations", 5, 3, "Beam", "MiscBeams");
        if (tables.TryGetValue("BuffVisualsArtVariations", out var buff) && buff.Columns is
            [{ Name: "Buff", Type: DatColumnType.String, IsArray: false },
             { Name: "BuffVisual", ReferencedTable: "BuffVisuals", Type: DatColumnType.ForeignRow, IsArray: true },
             { Name: null, Type: DatColumnType.I32, IsArray: false } buffVariant])
            tables[buff.Name] = new(buff.Name, [buff.Columns[0], buff.Columns[1], buffVariant with { Name = "Variant" }]);
        if (tables.TryGetValue("ItemisedVisualEffect", out var itemised) && itemised.Columns.Count == 23 &&
            itemised.Columns[0] is { Name: "Mtx", ReferencedTable: "MtxTypeGameSpecific", Type: DatColumnType.ForeignRow, IsArray: false } &&
            itemised.Columns[3] is { Name: "Stats", ReferencedTable: "Stats", Type: DatColumnType.ForeignRow, IsArray: true } &&
            itemised.Columns[7] is { Name: null, Type: DatColumnType.I32, IsArray: true })
            tables[itemised.Name] = new(itemised.Name, itemised.Columns.Select((c, i) => i == 7 ? c with { Name = "StatValues" } : c).ToList());
        return new DatSchema { Version = schema.Version, CreatedAt = schema.CreatedAt, Source = schema.Source, Tables = tables };
        void Alias(string table, int count, int variant, string link, string referencedTable)
        {
            if (!tables.TryGetValue(table, out var t) || t.Columns.Count != count) return;
            var c = t.Columns;
            var types = count == 3 ? new[] { DatColumnType.String, DatColumnType.ForeignRow, DatColumnType.I32 }
                : new[] { DatColumnType.String, DatColumnType.ForeignRow, DatColumnType.I32, DatColumnType.I32, DatColumnType.ForeignRow };
            if(!c.Select(column=>column.Type).SequenceEqual(types) || c.Where((column,i)=>column.IsArray!=(i==1)).Any())return;
            if (c[0] is not { Name: "Id", Type: DatColumnType.String, IsArray: false }
                || c[1].Name != link || c[1].ReferencedTable != referencedTable || !c[1].IsArray
                || c[variant] is not { Name: null, Type: DatColumnType.I32, IsArray: false }) return;
            tables[table] = new(table, c.Select((column, i) => i == variant ? column with { Name = "Variant" } : column).ToList());
        }
    }

    public static DatSchema LoadFile(string path)
    {
        using var s = File.OpenRead(path);
        return WithKnownAliases(Load(s, path));
    }

    public static DatSchema Load(Stream json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int version = root.GetProperty("version").GetInt32();
        if (version != SupportedVersion)
            throw new InvalidDataException($"dat-schema version {version} is not supported (expected {SupportedVersion}).");
        var tables = new Dictionary<string, DatTableSchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in root.GetProperty("tables").EnumerateArray())
        {
            if (t.TryGetProperty("validFor", out var vf) && (vf.GetInt32() & ValidForPoE1) == 0) continue;
            var name = t.GetProperty("name").GetString()!;
            var cols = new List<DatColumn>();
            foreach (var c in t.GetProperty("columns").EnumerateArray())
            {
                if (c.TryGetProperty("until", out var until) && until.ValueKind == JsonValueKind.String) continue;
                string? refTable = c.TryGetProperty("references", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("table", out var rt) ? rt.GetString() : null;
                string? file = c.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                cols.Add(new DatColumn(
                    c.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    ParseType(c.GetProperty("type").GetString()!),
                    c.TryGetProperty("array", out var a) && a.GetBoolean(),
                    c.TryGetProperty("interval", out var iv) && iv.ValueKind == JsonValueKind.True,
                    refTable, file));
            }
            tables[name] = new DatTableSchema(name, cols);
        }
        long created = root.TryGetProperty("createdAt", out var ca) ? ca.GetInt64() : 0;
        return new DatSchema { Version = version, CreatedAt = DateTimeOffset.FromUnixTimeSeconds(created), Tables = tables, Source = source };
    }

    private static DatColumnType ParseType(string t) => t switch
    {
        "bool" => DatColumnType.Bool,
        "string" => DatColumnType.String,
        "i16" => DatColumnType.I16,
        "u16" => DatColumnType.U16,
        "i32" => DatColumnType.I32,
        "u32" => DatColumnType.U32,
        "f32" => DatColumnType.F32,
        "array" => DatColumnType.Array,
        "row" => DatColumnType.Row,
        "foreignrow" => DatColumnType.ForeignRow,
        "enumrow" => DatColumnType.EnumRow,
        _ => throw new InvalidDataException($"Unknown dat column type '{t}'."),
    };
}
