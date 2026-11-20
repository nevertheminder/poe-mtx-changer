using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Formats.Dat;
using PoeMtxChanger.Core.Formats.Dds;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Tests;

/// <summary>TEST-ONLY .datc64 writer: fills the columns given, writes empty/null values for every other column of the schema.</summary>
public static class DatWriter
{
    public static byte[] Write(DatTableSchema schema, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var fixedPart = new MemoryStream();
        var variable = new MemoryStream();
        variable.Write(DatTable.VariableDataMagic);
        long emptyString = variable.Position;
        variable.Write(new byte[4]);

        long AddString(string s)
        {
            long at = variable.Position;
            variable.Write(Encoding.Unicode.GetBytes(s));
            variable.Write(new byte[4]);
            return at;
        }
        void U64(Stream st, ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); st.Write(b); }
        ulong Key(object? v) => v is int i ? (ulong)i : 0xFEFEFEFEFEFEFEFEUL;

        foreach (var row in rows)
            foreach (var c in schema.Columns)
            {
                row.TryGetValue(c.Name ?? "", out var v);
                if (c.IsArray)
                {
                    var items = (v as System.Collections.IEnumerable)?.Cast<object?>().ToList() ?? [];
                    long at = variable.Position;
                    foreach (var it in items)
                    {
                        switch (c.Type)
                        {
                            case DatColumnType.String: U64(variable, (ulong)AddStringDeferred(it as string ?? "")); break;
                            case DatColumnType.ForeignRow: U64(variable, Key(it)); U64(variable, 0); break;
                            case DatColumnType.Row: U64(variable, Key(it)); break;
                            default: variable.Write(BitConverter.GetBytes(Convert.ToInt32(it))); break;
                        }
                    }
                    U64(fixedPart, (ulong)items.Count);
                    U64(fixedPart, items.Count == 0 ? 0 : (ulong)at);
                    continue;
                }
                switch (c.Type)
                {
                    case DatColumnType.Bool: fixedPart.WriteByte(v is true ? (byte)1 : (byte)0); break;
                    case DatColumnType.I16 or DatColumnType.U16: fixedPart.Write(BitConverter.GetBytes((short)(v is int s ? s : 0))); break;
                    case DatColumnType.I32 or DatColumnType.U32 or DatColumnType.EnumRow: fixedPart.Write(BitConverter.GetBytes(v is int n ? n : 0)); break;
                    case DatColumnType.F32: fixedPart.Write(BitConverter.GetBytes(0f)); break;
                    case DatColumnType.String: U64(fixedPart, (ulong)(v is string str ? AddString(str) : emptyString)); break;
                    case DatColumnType.Row: U64(fixedPart, Key(v)); break;
                    case DatColumnType.ForeignRow: U64(fixedPart, Key(v)); U64(fixedPart, 0); break;
                    default: throw new NotSupportedException(c.Type.ToString());
                }
            }

        // String arrays: element offsets must point at strings, so they are appended after the array block.
        long AddStringDeferred(string s)
        {
            // Write the string at the end later is complex; instead store strings in a side buffer resolved at the end.
            pending.Add((variable.Position, s));
            return 0; // patched below
        }

        var output = new MemoryStream();
        output.Write(BitConverter.GetBytes(rows.Count));
        output.Write(fixedPart.ToArray());
        var varBytes = variable.ToArray().ToList();
        foreach (var (slot, s) in pending)
        {
            long at = varBytes.Count;
            varBytes.AddRange(Encoding.Unicode.GetBytes(s));
            varBytes.AddRange(new byte[4]);
            var b = BitConverter.GetBytes((ulong)at);
            for (int i = 0; i < 8; i++) varBytes[(int)slot + i] = b[i];
        }
        pending.Clear();
        output.Write(varBytes.ToArray());
        return output.ToArray();
    }

    [ThreadStatic] private static List<(long Slot, string Value)>? _pending;
    private static List<(long Slot, string Value)> pending => _pending ??= [];

    public static Dictionary<string, object?> Row(params (string Col, object? Value)[] values) => values.ToDictionary(v => v.Col, v => v.Value);
}

public class DatTests
{
    private static DatTableSchema TestSchema() => new("Test",
    [
        new DatColumn("Id", DatColumnType.String, false, false, null, null),
        new DatColumn("Count", DatColumnType.I32, false, false, null, null),
        new DatColumn("Flag", DatColumnType.Bool, false, false, null, null),
        new DatColumn("Other", DatColumnType.ForeignRow, false, false, "Test", null),
        new DatColumn("Files", DatColumnType.String, true, false, null, ".ao"),
        new DatColumn("Links", DatColumnType.ForeignRow, true, false, "Test", null),
        new DatColumn(null, DatColumnType.F32, false, false, null, null),
    ]);

    [Fact]
    public void Row_length_follows_datc64_field_sizes()
        => Assert.Equal(8 + 4 + 1 + 16 + 16 + 16 + 4, TestSchema().RowLength);

    [Fact]
    public void Reads_scalars_strings_keys_and_arrays()
    {
        var schema = TestSchema();
        var bytes = DatWriter.Write(schema,
        [
            DatWriter.Row(("Id", "First"), ("Count", 42), ("Flag", true), ("Other", 1), ("Files", new[] { "Art/a.ao", "Art/b.ao" }), ("Links", new object?[] { 1, null })),
            DatWriter.Row(("Id", ""), ("Count", -7)),
        ]);
        var t = DatTable.Parse(bytes, schema);
        Assert.Equal(2, t.RowCount);
        Assert.Equal("First", t.GetString(0, "Id"));
        Assert.Equal(42, t.GetInt32(0, "Count"));
        Assert.True(t.GetBool(0, "Flag"));
        Assert.Equal(1, t.GetRowRef(0, "Other"));
        Assert.Null(t.GetRowRef(1, "Other"));
        Assert.Equal(["Art/a.ao", "Art/b.ao"], t.GetStringArray(0, "Files"));
        Assert.Equal([1, null], t.GetRowRefArray(0, "Links"));
        Assert.Empty(t.GetStringArray(1, "Files"));
        Assert.Equal("", t.GetString(1, "Id"));
        Assert.Equal(-7, t.GetInt32(1, "Count"));
    }

    [Fact]
    public void Outdated_schema_is_reported_instead_of_misreading()
    {
        var bytes = DatWriter.Write(TestSchema(), [DatWriter.Row(("Id", "x"))]);
        var shorter = new DatTableSchema("Test", TestSchema().Columns.Take(3).ToList());
        var ex = Assert.Throws<DatSchemaMismatchException>(() => DatTable.Parse(bytes, shorter));
        Assert.Equal(13, ex.Expected);
        Assert.Equal(TestSchema().RowLength, ex.Actual);
    }

    [Fact]
    public void Type_misuse_and_missing_magic_are_errors()
    {
        var t = DatTable.Parse(DatWriter.Write(TestSchema(), [DatWriter.Row(("Id", "x"))]), TestSchema());
        Assert.Throws<InvalidOperationException>(() => t.GetInt32(0, "Id"));
        Assert.Throws<KeyNotFoundException>(() => t.GetString(0, "Nope"));
        Assert.Throws<InvalidDataException>(() => DatTable.Parse(new byte[64], TestSchema()));
    }

    [Fact]
    public void Embedded_schema_has_the_tables_the_catalog_uses()
    {
        var s = DatSchema.LoadDefault();
        Assert.Equal(DatSchema.SupportedVersion, s.Version);
        foreach (var name in new[] { "MtxTypes", "MtxTypeGameSpecific", "ShopTag", "MicrotransactionPortalVariations", "Pet", "Footprints", "BaseItemTypes", "ItemVisualIdentity", "ItemClasses" })
            Assert.Contains(name, s.Tables.Keys);
        Assert.Contains(s.Tables["ItemVisualIdentity"].Columns, c => c.Name == "AOFile" && c.Type == DatColumnType.String);
    }
}

public class DataCatalogTests
{
    [Fact]
    public void Builds_named_items_with_resources_icons_and_slot_targets_from_tables()
    {
        var schema = DatSchema.LoadDefault();
        var src = new InMemoryResourceSource();
        void Put(string table, params Dictionary<string, object?>[] rows) => src.Add(schema.Tables[table].GamePath, DatWriter.Write(schema.Tables[table], rows));

        // Mirrors the structure observed in the real tables (build 26694821).
        Put("MtxTypes",
            DatWriter.Row(("Id", "EmberPortal"), ("Name", "Ember Portal"), ("Type", "Portal Modification")),
            DatWriter.Row(("Id", "AshWings"), ("Name", "Ash Wings"), ("Type", "Body Armour Attachment")),
            DatWriter.Row(("Id", "Unknown"), ("Name", "Mystery Effect"), ("Type", "Character Effect")),
            DatWriter.Row(("Id", "SeraphHelm"), ("Name", "Seraph Helmet"), ("Type", "Helmet Skin")),
            DatWriter.Row(("Id", "Test"), ("Name", "[DNT] Internal Thing"), ("Type", "Helmet Skin")));
        Put("MtxTypeGameSpecific",
            DatWriter.Row(("Type", 0), ("DDSFile", "Art/2DArt/MTX/ember.dds")),
            DatWriter.Row(("Type", 1), ("DDSFile", "Art/2DArt/MTX/wings.dds")),
            DatWriter.Row(("Type", 2)),
            DatWriter.Row(("Type", 3), ("DDSFile", "Art/2DArt/MTX/seraph.dds")),
            DatWriter.Row(("Type", 4)));
        Put("MicrotransactionPortalVariations",
            DatWriter.Row(("MtxTypeGameSpecific", 0), ("AOFile", "Metadata/Effects/Microtransactions/Portal/ember.ao")),
            DatWriter.Row(("MtxTypeGameSpecific", 4), ("AOFile", "Metadata/Effects/Microtransactions/Portal/dnt.ao")));
        Put("ItemClasses", DatWriter.Row(("Id", "Body Armour"), ("Name", "Body Armours")), DatWriter.Row(("Id", "Helmet"), ("Name", "Helmets")));
        Put("ItemVisualIdentity",
            DatWriter.Row(("Id", "AstralPlate"), ("AOFile", "Metadata/Items/Armours/astral.ao"), ("MarauderSMFiles", new[] { "Metadata/Items/Armours/AstralStr.ao" })),
            DatWriter.Row(("Id", "AshWings"), ("AOFile", "Metadata/Items/Currency/CurrencyOrb.ao"), ("SMFiles", new[] { "Art/Microtransactions/wings/ash.ao" })),
            DatWriter.Row(("Id", "Aventail"), ("MarauderSMFiles", new[] { "Art/Models/Helmets/AventailStr.sm" }), ("WitchSMFiles", new[] { "Art/Models/Helmets/AventailInt.sm" })),
            DatWriter.Row(("Id", "SeraphHelm"), ("AOFile", "Metadata/Items/Currency/CurrencyOrb.ao"),
                ("MarauderSMFiles", new[] { "Metadata/Items/Armours/Helmets/SeraphStr.ao" }), ("WitchSMFiles", new[] { "Metadata/Items/Armours/Helmets/SeraphInt.ao" })));
        Put("ItemisedVisualEffect",
            DatWriter.Row(("Mtx", 1), ("VisualIdentity", 1)),
            DatWriter.Row(("Mtx", 3), ("VisualIdentity", 3)));
        Put("BaseItemTypes",
            DatWriter.Row(("Id", "Metadata/Items/Armours/BodyArmours/AstralPlate"), ("Name", "Astral Plate"), ("ItemClassesKey", 0), ("ItemVisualIdentity", 0)),
            DatWriter.Row(("Id", "Metadata/Items/Armours/Helmets/Aventail"), ("Name", "Aventail Helmet"), ("ItemClassesKey", 1), ("ItemVisualIdentity", 2)));
        foreach (var f in new[] { "Metadata/Effects/Spells/portal/portal.ao", "Metadata/Effects/Spells/portal/portal2.ao", "Metadata/Effects/Microtransactions/Portal/ember.ao",
                     "Metadata/Items/Armours/astral.ao", "Metadata/Items/Armours/AstralStr.ao", "Metadata/Items/Currency/CurrencyOrb.ao", "Art/Microtransactions/wings/ash.ao",
                     "Art/Models/Helmets/AventailStr.sm", "Art/Models/Helmets/AventailInt.sm", "Art/Models/Helmets/SeraphStr.sm", "Art/Models/Helmets/SeraphInt.sm" })
            src.Add(f, "version 2");
        src.Add("Metadata/Items/Armours/Helmets/SeraphStr.ao", "version 3\r\nclient\r\n{\r\n\tSkinMesh\r\n\t{\r\n\t\tskin = \"Art/Models/Helmets/SeraphStr.sm\"\r\n\t}\r\n}\r\n");
        src.Add("Metadata/Items/Armours/Helmets/SeraphInt.ao", "version 3\r\nclient\r\n{\r\n\tSkinMesh\r\n\t{\r\n\t\tskin = \"Art/Models/Helmets/SeraphInt.sm\"\r\n\t}\r\n}\r\n");

        var result = new DataCatalogBuilder(src, schema, CatalogRules.LoadDefault()).Build();

        Assert.True(result.HasMtxNames);
        var portal = Assert.Single(result.Items, i => i.DisplayName == "Ember Portal");
        Assert.Equal(CosmeticCategory.Portal, portal.Category);
        Assert.Equal(["Metadata/Effects/Microtransactions/Portal/ember.ao"], portal.PrimaryResources);
        Assert.Equal("Art/2DArt/MTX/ember.dds", portal.IconPath);
        Assert.Equal("data", portal.Origin);
        Assert.Equal(CompatibilityStatus.Experimental, portal.Status);

        var wings = Assert.Single(result.Items, i => i.DisplayName == "Ash Wings");
        Assert.Equal(CosmeticCategory.WingsAndBack, wings.Category);
        Assert.Contains("Art/Microtransactions/wings/ash.ao", wings.AllResources);
        Assert.DoesNotContain("Metadata/Items/Currency/CurrencyOrb.ao", wings.AllResources); // drop model is not the worn look
        Assert.Contains("ItemisedVisualEffect", wings.StatusReason);

        var mystery = Assert.Single(result.Items, i => i.DisplayName == "Mystery Effect");
        Assert.Equal(CosmeticCategory.CharacterEffect, mystery.Category);
        Assert.Equal(CompatibilityStatus.Unsupported, mystery.Status);
        Assert.DoesNotContain(result.Items, i => i.DisplayName.StartsWith("[DNT"));

        var seraph = Assert.Single(result.Items, i => i.DisplayName == "Seraph Helmet");
        Assert.Equal(CosmeticCategory.Helmet, seraph.Category);
        Assert.Equal(2, seraph.ClassVariants.Count);

        var portalTarget = Assert.Single(result.Suggestions, s => s.SlotKey == "portal");
        Assert.Equal(2, portalTarget.ClassVariants.Count);
        Assert.DoesNotContain(result.Suggestions, s => s.Path.Contains("dnt.ao")); // a [DNT] MTX row is not the default portal
        var helmet = Assert.Single(result.Suggestions, s => s.SlotKey == "helmet");
        Assert.Equal("Aventail Helmet", helmet.Label);
        Assert.Equal("Art/Models/Helmets/AventailStr.sm", helmet.ClassVariants["Marauder"]);
        Assert.Contains(result.Suggestions, s => s.SlotKey == "body" && s.Label == "Astral Plate");
        Assert.Contains(result.Diagnostics, d => d.Table == "Pet" && !d.Ok); // not present in this fixture → reported, not fatal

        // Per-class resolution: helmet base meshes (.sm) ← meshes referenced by the MTX's SkinMesh (.ao)
        var resolved = Core.Mapping.SkinMappingResolver.Resolve(helmet, null, seraph, src);
        Assert.Equal(2, resolved.Mappings.Count);
        Assert.Contains(resolved.Mappings, m => m.Note == "Marauder" && m.VisualIdentityEdit?.Row == 2 && m.SourcePath == "Metadata/Items/Armours/Helmets/SeraphStr.ao");
        Assert.Contains(resolved.Mappings, m => m.Note == "Witch" && m.VisualIdentityEdit?.Column == "WitchSMFiles" && m.SourcePath == "Metadata/Items/Armours/Helmets/SeraphInt.ao");
        Assert.Contains(Core.Mapping.SkinMappingResolver.WrapperNote, resolved.Notes);
        var report = new Core.Mapping.MappingValidator(Core.Mapping.ResourcePolicy.Default).Validate(resolved.Mappings, src);
        Assert.False(report.HasErrors);

        // Portal: both default town portal objects get the MTX portal
        var portalMaps = Core.Mapping.SkinMappingResolver.Resolve(portalTarget, null, portal, src).Mappings;
        Assert.Equal(2, portalMaps.Count);
        Assert.All(portalMaps, m => Assert.Equal("Metadata/Effects/Microtransactions/Portal/ember.ao", m.SourcePath));
    }

    [Fact]
    public void Missing_tables_produce_diagnostics_not_crashes()
    {
        var result = new DataCatalogBuilder(new InMemoryResourceSource(), DatSchema.LoadDefault(), CatalogRules.LoadDefault()).Build();
        Assert.False(result.HasMtxNames);
        Assert.Empty(result.Items);
        Assert.All(result.Diagnostics, d => Assert.False(d.Ok));
    }
}

public class DdsTests
{
    private static byte[] Header(int w, int h, string fourCC, uint dxgi = 0)
    {
        var hdr = new byte[fourCC == "DX10" ? 148 : 128];
        "DDS "u8.CopyTo(hdr);
        BitConverter.GetBytes(124).CopyTo(hdr, 4);
        BitConverter.GetBytes(h).CopyTo(hdr, 12);
        BitConverter.GetBytes(w).CopyTo(hdr, 16);
        BitConverter.GetBytes(4).CopyTo(hdr, 80);
        Encoding.ASCII.GetBytes(fourCC).CopyTo(hdr, 84);
        if (fourCC == "DX10") BitConverter.GetBytes(dxgi).CopyTo(hdr, 128);
        return hdr;
    }

    private static byte[] Bc1Block(ushort c0, ushort c1, uint idx) => [.. BitConverter.GetBytes(c0), .. BitConverter.GetBytes(c1), .. BitConverter.GetBytes(idx)];

    private static byte[] Px(DecodedImage img, int x, int y) => img.Bgra.AsSpan((y * img.Width + x) * 4, 4).ToArray();

    [Fact]
    public void Bc1_four_colour_block_decodes_endpoints_and_interpolants()
    {
        // pixel 0 → index 0 (red), pixel 1 → index 1 (blue), pixel 2 → index 2 (2/3 red), pixel 3 → index 3 (1/3 red)
        uint idx = 0 | (1u << 2) | (2u << 4) | (3u << 6);
        var img = DdsDecoder.Decode([.. Header(4, 4, "DXT1"), .. Bc1Block(0xF800, 0x001F, idx)]);
        Assert.Equal((4, 4, "DXT1"), (img.Width, img.Height, img.Format));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Px(img, 0, 0));       // BGRA red
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Px(img, 1, 0));       // blue
        Assert.Equal(new byte[] { 85, 0, 170, 255 }, Px(img, 2, 0));      // (2*R + B)/3
        Assert.Equal(new byte[] { 170, 0, 85, 255 }, Px(img, 3, 0));
    }

    [Fact]
    public void Bc1_three_colour_mode_has_transparent_index_3()
    {
        var img = DdsDecoder.Decode([.. Header(4, 4, "DXT1"), .. Bc1Block(0x001F, 0xF800, 3u)]);
        Assert.Equal(0, Px(img, 0, 0)[3]);
        Assert.Equal(255, Px(img, 1, 0)[3]);
    }

    [Fact]
    public void Bc3_alpha_block_interpolates_eight_values()
    {
        // alpha endpoints 255/0, pixel0 idx0 (255), pixel1 idx1 (0), pixel2 idx2 (6*255/7)
        ulong bits = 0 | (1UL << 3) | (2UL << 6);
        var alpha = new byte[8];
        alpha[0] = 255; alpha[1] = 0;
        for (int i = 0; i < 6; i++) alpha[2 + i] = (byte)(bits >> (8 * i));
        var img = DdsDecoder.Decode([.. Header(4, 4, "DXT5"), .. alpha, .. Bc1Block(0xFFFF, 0x0000, 0)]);
        Assert.Equal(255, Px(img, 0, 0)[3]);
        Assert.Equal(0, Px(img, 1, 0)[3]);
        Assert.Equal(6 * 255 / 7, Px(img, 2, 0)[3]);
        Assert.Equal(new byte[] { 255, 255, 255 }, Px(img, 0, 0)[..3]);
    }

    [Fact]
    public void Non_multiple_of_four_sizes_are_clipped()
    {
        var img = DdsDecoder.Decode([.. Header(5, 3, "DXT1"), .. Bc1Block(0xF800, 0, 0), .. Bc1Block(0x07E0, 0, 0)]);
        Assert.Equal(5 * 3 * 4, img.Bgra.Length);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Px(img, 4, 2)); // second block is green
    }

    [Fact]
    public void Redirects_and_brotli_wrappers_are_followed()
    {
        var real = Catalog.MockCatalog.ProceduralBc1(16, 8, seed: 3);
        var redirect = Encoding.UTF8.GetBytes("*Art/real.dds");
        var img = DdsDecoder.Decode(redirect, p => p == "Art/real.dds" ? real : null);
        Assert.Equal((16, 8), (img.Width, img.Height));

        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(real.Length)];
        Assert.True(BrotliEncoder.TryCompress(real, compressed, out int n));
        byte[] wrapped = [.. BitConverter.GetBytes(real.Length), .. compressed.AsSpan(0, n)];
        Assert.Equal(img.Bgra, DdsDecoder.Decode(wrapped).Bgra);
    }

    [Fact]
    public void Unsupported_and_broken_inputs_fail_cleanly()
    {
        Assert.Throws<DdsNotSupportedException>(() => DdsDecoder.Decode([.. Header(4, 4, "DX10", 95), .. new byte[16]]));
        Assert.Throws<DdsNotSupportedException>(() => DdsDecoder.Decode(Encoding.ASCII.GetBytes("not a texture at all")));
        Assert.Throws<InvalidDataException>(() => DdsDecoder.Decode([.. Header(64, 64, "DXT1"), .. new byte[8]]));
        Assert.Throws<DdsNotSupportedException>(() => DdsDecoder.Decode(Encoding.UTF8.GetBytes("*Art/missing.dds"), _ => null));
    }

    [Fact]
    public void Bc7_mode6_uniform_white_decodes_bgra_and_checks_top_mip_bounds()
    {
        byte[] block=new byte[16];int bit=0;
        void Put(int value,int count){for(int i=0;i<count;i++,bit++)if((value&(1<<i))!=0)block[bit/8]|=(byte)(1<<(bit%8));}
        Put(64,7);for(int i=0;i<8;i++)Put(127,7);Put(3,2);
        var image=DdsDecoder.Decode([.. Header(4,4,"DX10",98),.. block]);
        Assert.Equal("BC7",image.Format);Assert.All(image.Bgra,pixel=>Assert.Equal(255,pixel));
        Assert.Throws<InvalidDataException>(()=>DdsDecoder.Decode([.. Header(4,4,"DX10",98),.. block[..8]]));
    }
}
