using System.Buffers.Binary;
using System.Text;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Formats.Dat;

namespace PoeMtxChanger.Core.Mapping;

public sealed record VisualIdentityReferenceEdit(int Row, string Column, IReadOnlyList<string> ExpectedPaths, string ExpectedIdentityId);

/// <summary>
/// Changes only a named per-class appearance string-array pointer. Preserves every original byte outside
/// the selected 16-byte array headers and appends new strings/arrays. No stats, flags, keys or other tables can be edited.
/// </summary>
public static class VisualIdentityEditor
{
    public const string TablePath = "Data/ItemVisualIdentity.datc64";

    /// <param name="schema">Table layout to use; defaults to <see cref="DatSchema.Current"/> (the schema the catalog was built with).</param>
    public static byte[] Apply(byte[] original, IReadOnlyList<ReplacementMapping> mappings, DatTableSchema? schema = null)
    {
        schema ??= DatSchema.Current.Tables.TryGetValue("ItemVisualIdentity", out var t) ? t
            : throw new InvalidDataException("The active dat schema has no ItemVisualIdentity table.");
        var table = DatTable.Parse(original, schema);
        int variableStart = checked(4 + table.RowCount * table.RowLength);
        using var output = new MemoryStream();
        output.Write(original);
        var changed = new HashSet<(int Row, string Column)>();
        foreach (var mapping in mappings)
        {
            var edit = mapping.VisualIdentityEdit ?? throw new InvalidDataException("Missing structured visual edit.");
            if (!mapping.TargetPath.Equals(TablePath, StringComparison.OrdinalIgnoreCase)
                || !Path.GetExtension(mapping.SourcePath).Equals(".ao", StringComparison.OrdinalIgnoreCase)
                || !CharacterClasses.All.Any(c => c.Column == edit.Column))
                throw new InvalidDataException("Only per-class appearance string arrays in ItemVisualIdentity are editable.");
            if (!changed.Add((edit.Row, edit.Column))) throw new InvalidDataException("The same appearance field is edited more than once.");
            if (string.IsNullOrWhiteSpace(edit.ExpectedIdentityId) || table.GetString(edit.Row, "Id") != edit.ExpectedIdentityId)
                throw new InvalidDataException("The appearance row ID no longer matches the plan.");
            var current = table.GetStringArray(edit.Row, edit.Column);
            if (current.Count == 0 || !current.SequenceEqual(edit.ExpectedPaths, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("The base appearance no longer matches the plan: " + edit.Column);
            int columnOffset = 0;
            foreach (var column in schema.Columns)
            {
                if (column.Name == edit.Column) break;
                columnOffset += column.FixedSize;
            }
            long stringOffset = output.Length - variableStart;
            output.Position = output.Length;
            output.Write(Encoding.Unicode.GetBytes(mapping.SourcePath));
            output.Write(new byte[4]);
            long arrayOffset = output.Length - variableStart;
            output.Write(BitConverter.GetBytes((ulong)stringOffset));
            int field = checked(4 + edit.Row * table.RowLength + columnOffset);
            output.Position = field;
            output.Write(BitConverter.GetBytes(1UL));
            output.Write(BitConverter.GetBytes((ulong)arrayOffset));
        }
        var result = output.ToArray();
        var verified = DatTable.Parse(result, schema);
        foreach (var mapping in mappings)
            if (!verified.GetStringArray(mapping.VisualIdentityEdit!.Row, mapping.VisualIdentityEdit.Column).SequenceEqual([mapping.SourcePath]))
                throw new InvalidDataException("Visual-reference readback failed.");
        return result;
    }
}
