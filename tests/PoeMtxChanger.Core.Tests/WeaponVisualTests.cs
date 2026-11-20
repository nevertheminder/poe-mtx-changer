using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Profiles;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Tests;

public sealed class WeaponVisualTests
{
    [Fact]
    public void Skin_resolution_uses_the_held_mesh_instead_of_the_drop_model()
    {
        var staff = new TargetSuggestion("weapon_main","Staff","Art/base_held.ao","test") { ItemClass="Staff" };
        var skin = new CosmeticItem { Id="staff", DisplayName="Staff skin", Category=CosmeticCategory.Weapon,
            MtxType="Weapon Skin", HeldWeaponPath="Art/skin_held.ao", PrimaryResources=["Art/skin_drop.ao","Art/skin_held.ao"],
            AllowedItemClasses=["Staff"] };
        var source = new InMemoryResourceSource().Add(staff.Path,"base").Add("Art/skin_held.ao","held").Add("Art/skin_drop.ao","drop");
        var result = SkinMappingResolver.Resolve(staff,null,skin,source);
        Assert.Equal("Art/skin_held.ao",Assert.Single(result.Mappings).SourcePath);
        Assert.Empty(SkinMappingResolver.Resolve(staff with { ItemClass="Bow" },null,skin,source).Mappings);
    }

    [Fact]
    public void Weapon_effect_replaces_only_the_role_for_a_compatible_type_and_needs_an_equipped_target()
    {
        CosmeticItem Effect(string id) => new()
        {
            Id=id, DisplayName=id, Category=CosmeticCategory.Weapon, MtxType="Weapon Effect",
            ResourceVariants=new Dictionary<string,string>{{"TwoHandedStaffEPKFile",$"Art/{id}_staff.epk"},{"BowEPKFile",$"Art/{id}_bow.epk"}}
        };
        var staff = new TargetSuggestion("weapon_main","Staff","Art/base.ao","test") { ItemClass="Staff" };
        var oldEffect=Effect("old");
        var newEffect=Effect("new");
        var replacement=WeaponVisuals.ReplaceEffect(oldEffect,newEffect,staff);
        Assert.NotNull(replacement);
        Assert.Equal("Art/old_staff.epk",replacement.TargetPath);
        Assert.Equal("Art/new_staff.epk",replacement.SourcePath);
        Assert.Null(WeaponVisuals.ReplaceEffect(null,newEffect,staff));
        Assert.Null(WeaponVisuals.ReplaceEffect(oldEffect,newEffect,staff with { ItemClass="Wand" }));
        Assert.Null(WeaponVisuals.ReplaceEffect(oldEffect,newEffect with { AllowedItemClasses=["Bow"] },staff));
    }

    [Fact]
    public void Equal_weapon_swaps_share_one_mapping_but_conflicting_swaps_remain_visible_to_validation()
    {
        var profile=new CosmeticProfile();
        var mapping=new ReplacementMapping { TargetPath="Art/base.ao",SourcePath="Art/new.ao" };
        profile.GetOrAddEntry(ProfileEntryKind.EquipmentSlot,"weapon_main").Mappings=[mapping];
        var off=profile.GetOrAddEntry(ProfileEntryKind.EquipmentSlot,"weapon_off");
        off.Mappings=[mapping];
        Assert.Single(profile.ActiveMappings());
        off.Mappings=[mapping with { SourcePath="Art/other.ao" }];
        Assert.Equal(2,profile.ActiveMappings().Count);
    }
}
