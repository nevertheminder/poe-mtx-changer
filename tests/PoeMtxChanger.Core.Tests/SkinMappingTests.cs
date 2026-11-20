using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Tests;

public class SkinMappingTests
{
    private static CosmeticItem Skin(params string[] paths) => new()
    {
        Id = "skin", DisplayName = "Test", Category = CosmeticCategory.Helmet,
        PrimaryResources = paths, AllResources = paths,
    };

    [Fact]
    public void Missing_class_never_falls_back_to_another_class_or_applies_partial_set()
    {
        var target = new TargetSuggestion("helmet", "base", "Art/base.sm", "test")
        { ClassVariants = new Dictionary<string, string> { ["Witch"] = "Art/base.sm", ["Marauder"] = "Art/str.sm" } };
        var skin = Skin("Art/witch.sm") with { ClassVariants = new Dictionary<string, string> { ["Witch"] = "Art/witch.sm" } };
        var resolved = SkinMappingResolver.Resolve(target, null, skin, null);
        Assert.Empty(resolved.Mappings);
        Assert.Contains(resolved.Notes, s => s.Contains("Marauder"));
    }

    [Fact]
    public void Multiple_same_kind_entry_points_are_ambiguous()
    {
        var r = SkinMappingResolver.Resolve(null, "Art/base.ao", Skin("Art/a.ao", "Art/b.ao"), null);
        Assert.Empty(r.Mappings);
        Assert.Contains(r.Notes, s => s.Contains("2"));
    }

    [Fact]
    public void Named_roles_preserve_distinct_effect_variants()
    {
        var t = new TargetSuggestion("portal", "base", "Art/base.ao", "test")
        { ResourceVariants = new Dictionary<string, string> { ["portal"] = "Art/base.ao", ["map"] = "Art/base-map.ao" } };
        var c = Skin("Art/a.ao", "Art/b.ao") with
        { ResourceVariants = new Dictionary<string, string> { ["portal"] = "Art/a.ao", ["map"] = "Art/b.ao" } };
        var r = SkinMappingResolver.Resolve(t, null, c, null);
        Assert.Equal(2, r.Mappings.Count);
        Assert.Contains(r.Mappings, m => m.TargetPath == "Art/base-map.ao" && m.SourcePath == "Art/b.ao");
    }

    [Fact]
    public void Mesh_is_taken_only_from_SkinMesh_skin_not_an_unrelated_reference()
    {
        var src = new InMemoryResourceSource();
        src.Add("Art/a.ao", "version 3\nOther { path = \"Art/aaa.sm\" }\nSkinMesh { skin = \"Art/zzz.sm\" }");
        src.Add("Art/aaa.sm", "unrelated"); src.Add("Art/zzz.sm", "mesh");
        var r = SkinMappingResolver.Resolve(null, "Art/base.sm", Skin("Art/a.ao"), src);
        Assert.Equal("Art/zzz.sm", Assert.Single(r.Mappings).SourcePath);
    }

    [Fact]
    public void Two_SkinMesh_assignments_are_not_guessed()
    {
        var src = new InMemoryResourceSource();
        src.Add("Art/a.ao", "SkinMesh { skin = \"Art/a.sm\" }\nSkinMesh { skin = \"Art/b.sm\" }");
        src.Add("Art/a.sm", "a"); src.Add("Art/b.sm", "b");
        Assert.Empty(SkinMappingResolver.Resolve(null, "Art/base.sm", Skin("Art/a.ao"), src).Mappings);
    }

    [Fact]
    public void Shared_identical_class_targets_are_deduplicated()
    {
        var t = new TargetSuggestion("helmet", "base", "Art/base.sm", "test")
        { ClassVariants = new Dictionary<string, string> { ["Witch"] = "Art/base.sm", ["Scion"] = "Art/base.sm" } };
        var c = Skin() with { ClassVariants = new Dictionary<string, string> { ["Witch"] = "Art/a.sm", ["Scion"] = "Art/a.sm" } };
        Assert.Single(SkinMappingResolver.Resolve(t, null, c, null).Mappings);
        c = c with { ClassVariants = new Dictionary<string, string> { ["Witch"] = "Art/a.sm", ["Scion"] = "Art/b.sm" } };
        Assert.Empty(SkinMappingResolver.Resolve(t, null, c, null).Mappings);
    }

    [Fact]
    public void Item_class_constraints_and_extra_effect_roles_are_enforced()
    {
        var target = new TargetSuggestion("weapon_main", "bow", "Art/base.ao", "test") { ItemClass = "Bow" };
        var skin = Skin("Art/a.ao") with { AllowedItemClasses = ["Wand"] };
        Assert.Empty(SkinMappingResolver.Resolve(target, null, skin, null).Mappings);
        target = target with { ItemClass = "Wand", ResourceVariants = new Dictionary<string, string> { ["Active:0"] = "Art/base.ao" } };
        skin = skin with { ResourceVariants = new Dictionary<string, string> { ["Active:0"] = "Art/a.ao", ["Idle:0"] = "Art/b.ao" } };
        Assert.Empty(SkinMappingResolver.Resolve(target, null, skin, null).Mappings);
    }

    [Fact]
    public void Unresolved_multi_part_class_arrays_block_automatic_mapping()
    {
        var target = new TargetSuggestion("helmet", "base", "Art/base.ao", "test");
        var skin = Skin("Art/a.ao") with { AutomaticMappingIssue = "Two hand-specific parts require research." };
        Assert.Empty(SkinMappingResolver.Resolve(target, null, skin, null).Mappings);
    }
}
