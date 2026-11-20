using PoeMtxChanger.Core.Formats.Dat;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Patching;
using PoeMtxChanger.Core.Util;
using PoeMtxChanger.Core.Profiles;

namespace PoeMtxChanger.Core.Tests;

public class VisualIdentityEditorTests
{
    private static readonly DatTableSchema Schema = DatSchema.LoadDefault().Tables["ItemVisualIdentity"];
    private static byte[] Original() => DatWriter.Write(Schema,
    [
        DatWriter.Row(("Id", "BaseHelmet"), ("MarauderSMFiles", new[] { "Art/base.sm" }), ("WitchSMFiles", new[] { "Art/int.sm" }), ("UniqueStat1", 42)),
        DatWriter.Row(("Id", "OtherItem"), ("MarauderSMFiles", new[] { "Art/other.sm" }), ("UniqueStat1", 91)),
    ]);
    private static ReplacementMapping Edit(int row = 0, string column = "MarauderSMFiles") => new()
    {
        TargetPath = VisualIdentityEditor.TablePath, SourcePath = "Art/full-skin.ao",
        VisualIdentityEdit = new(row, column, ["Art/base.sm"], "BaseHelmet"),
    };

    [Fact]
    public void Only_the_selected_16_byte_reference_field_changes_in_original_data()
    {
        var original = Original();
        var changed = VisualIdentityEditor.Apply(original, [Edit()]);
        int offset = 4 + Schema.Columns.TakeWhile(c => c.Name != "MarauderSMFiles").Sum(c => c.FixedSize);
        for (int i = 0; i < original.Length; i++)
            if (i < offset || i >= offset + 16) Assert.Equal(original[i], changed[i]);
        var parsed = DatTable.Parse(changed, Schema);
        Assert.Equal(["Art/full-skin.ao"], parsed.GetStringArray(0, "MarauderSMFiles"));
        Assert.Equal(["Art/int.sm"], parsed.GetStringArray(0, "WitchSMFiles"));
        Assert.Equal(42, parsed.GetRowRef(0, "UniqueStat1"));
        Assert.Equal(91, parsed.GetRowRef(1, "UniqueStat1"));
        Assert.Equal(["Art/other.sm"], parsed.GetStringArray(1, "MarauderSMFiles"));
    }

    [Fact]
    public void Stat_fields_other_tables_stale_paths_and_duplicate_fields_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => VisualIdentityEditor.Apply(Original(), [Edit(column: "UniqueStat1")]));
        Assert.Throws<InvalidDataException>(() => VisualIdentityEditor.Apply(Original(), [Edit() with { TargetPath = "Data/Stats.datc64" }]));
        Assert.Throws<InvalidDataException>(() => VisualIdentityEditor.Apply(Original(), [Edit() with { VisualIdentityEdit = new(0, "MarauderSMFiles", ["Art/stale.sm"], "BaseHelmet") }]));
        Assert.Throws<InvalidDataException>(() => VisualIdentityEditor.Apply(Original(), [Edit() with { VisualIdentityEdit = new(0, "MarauderSMFiles", ["Art/base.sm"], "WrongItem") }]));
        Assert.Throws<InvalidDataException>(() => VisualIdentityEditor.Apply(Original(), [Edit(), Edit()]));
    }

    [Fact]
    public void Structured_reference_edits_use_the_same_atomic_patch_and_restore_pipeline()
    {
        using var temp = new TempDir();
        var files = SyntheticGame.SampleFiles();
        files[VisualIdentityEditor.TablePath] = Original();
        files["Art/full-skin.ao"] = SyntheticGame.Utf16("version 3\nclient { ObjectHiding { hide_parent_segments = \"hairShape\" } }");
        using var storage = SyntheticGame.Create(temp.Combine("game"), files);
        var before = FileUtil.Sha256OfFile(storage.IndexPath);
        var engine = new PatchEngine(storage, SyntheticGame.Codec, temp.Combine("backups"), isGameRunning: () => false);
        var result = engine.Apply(new PatchRequest { UserConfirmed = true, Mappings = [Edit()] });
        Assert.Equal(1, result.ReplacedFiles);
        using (var archive = Storage.GameArchive.Open(storage, SyntheticGame.Codec))
            Assert.Equal(["Art/full-skin.ao"], DatTable.Parse(archive.Read(VisualIdentityEditor.TablePath), Schema).GetStringArray(0, "MarauderSMFiles"));
        engine.Restore(true);
        Assert.Equal(before, FileUtil.Sha256OfFile(storage.IndexPath));
    }

    [Fact]
    public void Editor_uses_the_given_schema_layout_not_the_embedded_one()
    {
        // A newer game version adds a column after the class arrays: the embedded layout would misread every row.
        var newer = new DatTableSchema("ItemVisualIdentity", [.. Schema.Columns, new DatColumn("AddedInNewLeague", DatColumnType.I32, false, false, null, null)]);
        var original = DatWriter.Write(newer, [DatWriter.Row(("Id", "BaseHelmet"), ("MarauderSMFiles", new[] { "Art/base.sm" }), ("AddedInNewLeague", 7))]);
        Assert.Throws<DatSchemaMismatchException>(() => VisualIdentityEditor.Apply(original, [Edit()], Schema));
        var changed = VisualIdentityEditor.Apply(original, [Edit()], newer);
        var parsed = DatTable.Parse(changed, newer);
        Assert.Equal(["Art/full-skin.ao"], parsed.GetStringArray(0, "MarauderSMFiles"));
        Assert.Equal(7, parsed.GetInt32(0, "AddedInNewLeague"));
    }

    [Fact]
    public void Schema_loaded_from_a_file_gets_the_same_column_aliases_as_the_embedded_one()
    {
        using var temp = new TempDir();
        var path = temp.Combine("schema.json");
        using (var s = typeof(DatSchema).Assembly.GetManifestResourceStream("PoeMtxChanger.Core.DatSchemaPoE1.json")!)
        using (var f = File.Create(path)) s.CopyTo(f);
        var embedded = DatSchema.LoadDefault();
        var fromFile = DatSchema.LoadFile(path);
        foreach (var table in new[] { "MiscEffectPacksArtVariations", "MiscBeamsArtVariations" })
            Assert.Equal(embedded.Tables[table].Columns.Select(c => c.Name), fromFile.Tables[table].Columns.Select(c => c.Name));
    }

    [Fact]
    public void Profiles_preserve_structured_edits_and_caveats_on_export_import()
    {
        var profile = new CosmeticProfile();
        profile.Entries.Add(new ProfileEntry { Key = "helmet", Mappings = [Edit()], Notes = [SkinMappingResolver.WrapperNote] });
        var json = System.Text.Json.JsonSerializer.Serialize(profile, FileUtil.Json);
        var restored = ProfileManager.Parse(json);
        var edit = Assert.Single(Assert.Single(restored.Entries).Mappings).VisualIdentityEdit!;
        Assert.Equal("MarauderSMFiles", edit.Column);
        Assert.Equal(["Art/base.sm"], edit.ExpectedPaths);
        Assert.Contains(SkinMappingResolver.WrapperNote, restored.Entries[0].Notes);
    }
}
