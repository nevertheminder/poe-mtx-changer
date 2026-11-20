using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Formats.Dat;
using PoeMtxChanger.Core.Storage;
using PoeMtxChanger.Core.Profiles;

namespace PoeMtxChanger.Core.Tests;

public class BuffVisualTests
{
    [Fact]
    public void Profile_keeps_the_selected_gem_and_both_visual_ids()
    {
        using var temp=new TempDir();var store=new ProfileManager(temp.Path);
        var selection=new BuffVisualSelection("hatred","player_aura_cold_damage","player_aura_cold_damage_alternate");
        store.Save(new CosmeticProfile{Entries=[new ProfileEntry{Kind=ProfileEntryKind.SkillEffect,Key="buff-visual:player_aura_cold_damage",BuffSelection=selection}]});
        Assert.Equal(selection,Assert.Single(Assert.Single(store.LoadAll()).Entries).BuffSelection);
    }
    [Fact]
    public void Buffs_use_named_visual_links_without_an_unnamed_variant_column()
    {
        var schema=DatSchema.LoadDefault();var tables=new Dictionary<string,DatTable>();
        void Put(string name,params Dictionary<string,object?>[] rows)=>tables[name]=DatTable.Parse(DatWriter.Write(schema.Tables[name],rows),schema.Tables[name]);
        Put("BuffVisuals",DatWriter.Row(("Id","base_aura"),("EPKFiles1",new[]{"Art/affected.epk"}),("EPKFiles2",new[]{"Art/base.epk"})),
            DatWriter.Row(("Id","skull_aura"),("EPKFiles2",new[]{"Art/skull.epk"})));
        Put("BuffVisualsArtVariations",DatWriter.Row(("Buff","hatred"),("BuffVisual",new[]{0})),DatWriter.Row(("Buff","hatred"),("BuffVisual",new[]{1})));
        var buffs=new BuffVisualCatalogBuilder(name=>tables.GetValueOrDefault(name)).Build();
        Assert.All(buffs,b=>Assert.Contains("hatred",b.DisplayName));
        var source=new InMemoryResourceSource().Add("Art/base.epk","1").Add("Art/skull.epk","1").Add("Art/affected.epk","1");
        var plan=BuffVisualCatalogBuilder.Plan(buffs.Single(b=>b.Id=="base_aura"),buffs.Single(b=>b.Id=="skull_aura"),source);
        Assert.True(plan.CanApply);Assert.Equal("Art/skull.epk",Assert.Single(plan.Mappings).SourcePath);
        Assert.Contains(plan.Limitations,n=>n.Contains("EPKFiles1"));Assert.All(plan.Mappings,m=>Assert.False(m.TargetPath.StartsWith("Data/")));
    }

    [Fact]
    public void Aura_art_joins_signed_variants_on_the_global_aura_stat()
    {
        var schema = DatSchema.LoadDefault();
        var tables = new Dictionary<string, DatTable>();
        void Put(string name, params Dictionary<string, object?>[] rows) =>
            tables[name] = DatTable.Parse(DatWriter.Write(schema.Tables[name], rows), schema.Tables[name]);

        Put("BuffVisuals",
            DatWriter.Row(("Id","lightbringer"), ("EPKFile","Art/lightbringer.epk")),
            DatWriter.Row(("Id","other_aura"), ("EPKFile","Art/other.epk"), ("BuffDDSFile","Art/other.dds")));
        Put("BuffVisualsArtVariations",
            DatWriter.Row(("Buff","global_auras"), ("BuffVisual",new[]{0}), ("Variant",-53)),
            DatWriter.Row(("Buff","global_auras"), ("BuffVisual",new[]{1}), ("Variant",-27)));
        Put("SkillArtVariations", DatWriter.Row(("Id","global_auras"), ("Stat",349)));
        Put("MtxTypes", DatWriter.Row(("Type","Aura Skin"), ("Name","Lightbringer Aura Effect")),
            DatWriter.Row(("Type","Aura Skin"), ("Name","Unrelated Aura Effect")));
        Put("MtxTypeGameSpecific", DatWriter.Row(("Type",0), ("DDSFile","Art/lightbringer.dds")),
            DatWriter.Row(("Type",1), ("DDSFile","Art/unrelated.dds")));
        Put("ItemisedVisualEffect",
            DatWriter.Row(("Mtx",0), ("Stats",new[]{77,349}), ("StatValues",new[]{-27,-53})),
            DatWriter.Row(("Mtx",1), ("Stats",new[]{77}), ("StatValues",new[]{-53})));

        var visuals = new BuffVisualCatalogBuilder(name => tables.GetValueOrDefault(name)).Build();
        var lightbringer = visuals.Single(visual => visual.Id == "lightbringer");
        Assert.Equal("Lightbringer Aura Effect", lightbringer.PickerName);
        Assert.Equal("Art/lightbringer.dds", lightbringer.IconPath);
        var other = visuals.Single(visual => visual.Id == "other_aura");
        Assert.Null(other.CosmeticName);
        Assert.Equal("Art/other.dds", other.IconPath);
    }

    [Fact]
    public void Conflicting_shared_resources_and_different_array_lengths_are_refused()
    {
        var source=new InMemoryResourceSource().Add("Art/base.epk","1").Add("Art/a.epk","1").Add("Art/b.epk","1");
        var original=new BuffVisualDefinition("base","",new Dictionary<string,IReadOnlyList<string>>{{"a",["Art/base.epk"]},{"b",["Art/base.epk"]}},[]){SkillGroups=["same-skill"]};
        var donor=new BuffVisualDefinition("donor","",new Dictionary<string,IReadOnlyList<string>>{{"a",["Art/a.epk"]},{"b",["Art/b.epk"]}},[]){SkillGroups=["same-skill"]};
        var plan=BuffVisualCatalogBuilder.Plan(original,donor,source);Assert.False(plan.CanApply);Assert.NotEmpty(plan.Errors);Assert.Empty(plan.Mappings);
        donor=donor with{Roles=new Dictionary<string,IReadOnlyList<string>>{{"a",["Art/a.epk","Art/b.epk"]}}};
        Assert.False(BuffVisualCatalogBuilder.Plan(original,donor,source).CanApply);
    }
    [Fact]
    public void Object_logic_cannot_be_smuggled_through_a_visual_role()
    {
        var source=new InMemoryResourceSource().Add("Metadata/base.ot","version 1").Add("Metadata/new.ot","version 1");
        var original=new BuffVisualDefinition("base","",new Dictionary<string,IReadOnlyList<string>>{{"EPKFile",["Metadata/base.ot"]}},[]){SkillGroups=["same-skill"]};
        var donor=new BuffVisualDefinition("donor","",new Dictionary<string,IReadOnlyList<string>>{{"EPKFile",["Metadata/new.ot"]}},[]){SkillGroups=["same-skill"]};
        var plan=BuffVisualCatalogBuilder.Plan(original,donor,source);Assert.False(plan.CanApply);Assert.NotEmpty(plan.Errors);
    }

    [Fact]
    public void Matching_resource_format_does_not_make_different_gems_compatible()
    {
        var original=new BuffVisualDefinition("hatred","",new Dictionary<string,IReadOnlyList<string>>{{"EPKFile",["Art/a.epk"]}},[]){SkillGroups=["hatred"]};
        var donor=new BuffVisualDefinition("herald","",new Dictionary<string,IReadOnlyList<string>>{{"EPKFile",["Art/b.epk"]}},[]){SkillGroups=["herald"]};
        var source=new InMemoryResourceSource().Add("Art/a.epk","1").Add("Art/b.epk","1");
        var plan=BuffVisualCatalogBuilder.Plan(original,donor,source);
        Assert.False(plan.CanApply);Assert.Empty(plan.Mappings);Assert.Contains(plan.Errors,e=>e.Contains("compatible skill"));
        Assert.False(BuffVisualCatalogBuilder.Compatible(original,donor with{SkillGroups=[]}));
    }

    [Fact]
    public void Gem_links_and_explicit_aura_whitelists_exclude_heralds_mines_and_monster_skills()
    {
        var schema=DatSchema.LoadDefault();var tables=new Dictionary<string,DatTable>();
        void Put(string name,params Dictionary<string,object?>[] rows)=>tables[name]=DatTable.Parse(DatWriter.Write(schema.Tables[name],rows),schema.Tables[name]);
        Put("BuffVisuals",Enumerable.Range(0,5).Select(i=>DatWriter.Row(("Id","visual"+i),("EPKFile","Art/visual"+i+".epk"))).ToArray());
        Put("BuffVisualsArtVariations",new[]{"hatred","herald","mine","monster","global_auras"}.Select((group,i)=>DatWriter.Row(("Buff",group),("BuffVisual",new[]{i}))).ToArray());
        Put("SkillArtVariations",new[]{"hatred","herald","mine","monster"}.Select((id,i)=>DatWriter.Row(("Id",id),("BuffVisuals",new[]{i}),("ActiveSkill",new[]{i}))).ToArray());
        Put("ActiveSkillType",DatWriter.Row(("Id","Aura")),DatWriter.Row(("Id","Herald")));
        Put("ActiveSkills",new[]{"Hatred","Herald","Mine","Monster"}.Select((name,i)=>DatWriter.Row(("Id",name),("DisplayedName",name),("ActiveSkillTypes",new[]{i==1?1:0}),("IsGem",true))).ToArray());
        Put("SkillGems",Enumerable.Range(0,3).Select(i=>DatWriter.Row(("BaseItemTypesKey",i),("GemVariants",new[]{i}))).ToArray());
        Put("GemEffects",Enumerable.Range(0,3).Select(i=>DatWriter.Row(("Id","gem"+i),("GrantedEffect",i))).ToArray());
        Put("GrantedEffects",Enumerable.Range(0,3).Select(i=>DatWriter.Row(("Id","effect"+i),("ActiveSkill",i))).ToArray());
        Put("MtxTypes",DatWriter.Row(("Id","aura-skin"),("Type","Aura Skin")));
        Put("MtxTypeGameSpecific",DatWriter.Row(("Type",0)));
        Put("ItemisedVisualEffect",DatWriter.Row(("Mtx",0),("BaseItemTypes",new[]{0,2})),DatWriter.Row(("Mtx",0),("BaseItemTypes",new[]{0})));
        var builder=new BuffVisualCatalogBuilder(name=>tables.GetValueOrDefault(name));var visuals=builder.Build();var skills=builder.BuildSkills(visuals);
        Assert.Equal(3,skills.Count);Assert.DoesNotContain(skills,s=>s.Id=="monster");
        var aura=skills.Single(s=>s.Id=="hatred");var herald=skills.Single(s=>s.Id=="herald");var mine=skills.Single(s=>s.Id=="mine");
        Assert.True(aura.AllowsGlobalAuras);Assert.False(herald.AllowsGlobalAuras);Assert.False(mine.AllowsGlobalAuras);
        var global=visuals.Single(v=>v.Id=="visual4");Assert.True(aura.Accepts(global));Assert.False(herald.Accepts(global));Assert.False(mine.Accepts(global));
        Assert.True(BuffVisualCatalogBuilder.Compatible(visuals.Single(v=>v.Id=="visual0"),global,aura));
        Assert.False(BuffVisualCatalogBuilder.Compatible(visuals.Single(v=>v.Id=="visual1"),global,herald));
    }
}
