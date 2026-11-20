using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Formats.Dat;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Tests;

public class SkillCatalogTests
{
    [Fact]
    public void Beam_and_effect_pack_roles_follow_named_links_without_copying_behavior_tables()
    {
        var schema=DatSchema.LoadDefault();var tables=new Dictionary<string,DatTable>();
        void Put(string name,params Dictionary<string,object?>[] rows)=>tables[name]=DatTable.Parse(DatWriter.Write(schema.Tables[name],rows),schema.Tables[name]);
        Put("SkillArtVariations",DatWriter.Row(("Id","test"),("BeamArt",new[]{1}),("EffectPacksArt",new[]{0}),("Variants",new[]{"Default","Red"})));
        Put("MiscAnimated",DatWriter.Row(("AOFile","Art/base.ao")),DatWriter.Row(("AOFile","Art/red.ao")));
        Put("MiscBeams",DatWriter.Row(("MiscAnimated",0)),DatWriter.Row(("MiscAnimated",1)));
        Put("MiscBeamsArtVariations",DatWriter.Row(("Id","beam"),("Beam",new[]{0}),("Variant",0)),DatWriter.Row(("Id","beam"),("Beam",new[]{1}),("Variant",1)));
        Put("MiscEffectPacks",DatWriter.Row(("EPKFile","Art/base.epk")),DatWriter.Row(("EPKFile","Art/red.epk")));
        Put("MiscEffectPacksArtVariations",DatWriter.Row(("Id","pack"),("EffectPack",new[]{0}),("Variant",0)),DatWriter.Row(("Id","pack"),("EffectPack",new[]{1}),("Variant",1)));
        var source=new InMemoryResourceSource().Add("Art/base.ao","version 3").Add("Art/red.ao","version 3").Add("Art/base.epk","1").Add("Art/red.epk","1");
        var variant=Assert.Single(new SkillVisualCatalogBuilder(source,name=>tables.GetValueOrDefault(name)).Build()).Variants.Single();
        Assert.True(variant.CanApply);Assert.Equal(2,variant.Mappings.Count);Assert.All(variant.Mappings,m=>Assert.False(m.TargetPath.StartsWith("Data/")));
    }
    private static IReadOnlyList<SkillVisualDefinition> Build(bool differentCount = false, bool duplicate = false)
    {
        var schema = DatSchema.LoadDefault();
        var source = new InMemoryResourceSource().Add("Art/base.ao", "version 3").Add("Art/celestial.ao", "version 3").Add("Art/extra.ao", "version 3");
        var tables = new Dictionary<string, DatTable>();
        void Put(string name, params Dictionary<string, object?>[] rows)
            => tables[name] = DatTable.Parse(DatWriter.Write(schema.Tables[name], rows), schema.Tables[name]);
        Put("SkillArtVariations", DatWriter.Row(("Id", "cyclone"), ("AnimatedArt", new[] { 0, 2 }),
            ("Variants", new[] { "Default", "White", "Blue", "Saw", "Illusionist", "Celestial" })));
        Put("MiscAnimated", DatWriter.Row(("Id", "base"), ("AOFile", "Art/base.ao")),
            DatWriter.Row(("Id", "celestial"), ("AOFile", "Art/celestial.ao")), DatWriter.Row(("Id", "extra"), ("AOFile", "Art/extra.ao")));
        var rows = new List<Dictionary<string, object?>>
        {
            DatWriter.Row(("Id", "spin"), ("Variant", 5), ("Animation", differentCount ? new[] { 1, 2 } : new[] { 1 })),
            DatWriter.Row(("Id", "spin"), ("Variant", 0), ("Animation", new[] { 0 })),
            DatWriter.Row(("Id", "impact"), ("Variant", 5), ("Animation", new[] { 2 })),
        };
        if (duplicate) rows.Add(DatWriter.Row(("Id", "spin"), ("Variant", 5), ("Animation", new[] { 2 })));
        Put("MiscAnimatedArtVariations", rows.ToArray());
        return new SkillVisualCatalogBuilder(source, name => tables.GetValueOrDefault(name)).Build();
    }

    [Fact]
    public void Sparse_variant_ids_and_nondefault_seed_rows_are_resolved_by_role_and_variant()
    {
        var skill = Assert.Single(Build());
        var variant = Assert.Single(skill.Variants, v => v.Number == 5);
        Assert.True(variant.CanApply);
        var mapping = Assert.Single(variant.Mappings);
        Assert.Equal("Art/base.ao", mapping.TargetPath);
        Assert.Equal("Art/celestial.ao", mapping.SourcePath);
        Assert.Contains("Celestial", variant.Name);
        Assert.Contains(variant.Limitations, l => l.Contains("impact"));
        Assert.DoesNotContain(variant.Mappings, m => m.TargetPath == "Art/extra.ao");
        Assert.False(skill.Variants.Single(v => v.Number == 1).CanApply);
    }

    [Fact]
    public void Shop_skin_is_matched_by_shared_stat_value_and_the_skills_gem()
    {
        var schema = DatSchema.LoadDefault();
        var tables = new Dictionary<string, DatTable>();
        void Put(string name, params Dictionary<string, object?>[] rows)
            => tables[name] = DatTable.Parse(DatWriter.Write(schema.Tables[name], rows), schema.Tables[name]);
        var source = new InMemoryResourceSource().Add("Art/base.ao", "version 3").Add("Art/celestial.ao", "version 3");
        Put("SkillArtVariations", DatWriter.Row(("Id", "cyclone"), ("AnimatedArt", new[] { 0 }), ("Stat", 7), ("ActiveSkill", new[] { 0 }),
            ("Variants", new[] { "Default", "White", "Blue", "Saw", "Illusionist", "Celestial" })));
        Put("ActiveSkills", DatWriter.Row(("Id", "cyclone"), ("DisplayedName", "Cyclone")));
        Put("MiscAnimated", DatWriter.Row(("Id", "base"), ("AOFile", "Art/base.ao")), DatWriter.Row(("Id", "celestial"), ("AOFile", "Art/celestial.ao")));
        Put("MiscAnimatedArtVariations", DatWriter.Row(("Id", "spin"), ("Variant", 0), ("Animation", new[] { 0 })),
            DatWriter.Row(("Id", "spin"), ("Variant", 5), ("Animation", new[] { 1 })));
        Put("BaseItemTypes", DatWriter.Row(("Id", "gem/cyclone"), ("Name", "Cyclone")), DatWriter.Row(("Id", "gem/rf"), ("Name", "Righteous Fire")));
        Put("MtxTypes", DatWriter.Row(("Type", "Cyclone Skin"), ("Name", "Celestial Cyclone Effect")),
            DatWriter.Row(("Type", "Righteous Fire Skin"), ("Name", "Celestial Righteous Fire Effect")));
        Put("MtxTypeGameSpecific", DatWriter.Row(("Type", 0), ("DDSFile", "Art/celestial_cyclone.dds")),
            DatWriter.Row(("Type", 1), ("DDSFile", "Art/celestial_rf.dds")));
        // Both skins set the same shared stat to 5; only the gem base items tell them apart.
        Put("ItemisedVisualEffect", DatWriter.Row(("Mtx", 0), ("Stats", new[] { 7 }), ("StatValues", new[] { 5 }), ("BaseItemTypes", new[] { 0 })),
            DatWriter.Row(("Mtx", 1), ("Stats", new[] { 7 }), ("StatValues", new[] { 5 }), ("BaseItemTypes", new[] { 1 })));
        var variant = Assert.Single(new SkillVisualCatalogBuilder(source, name => tables.GetValueOrDefault(name)).Build()).Variants.Single(v => v.Number == 5);
        Assert.Equal("Celestial Cyclone Effect", variant.PickerName);
        Assert.Equal("Art/celestial_cyclone.dds", variant.IconPath);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Ambiguous_or_different_length_roles_are_not_partially_applied(bool differentCount, bool duplicate)
    {
        var variant = Assert.Single(Build(differentCount, duplicate)).Variants.Single(v => v.Number == 5);
        Assert.False(variant.CanApply);
        Assert.NotEmpty(variant.Errors);
        Assert.Empty(variant.Mappings);
    }
}
