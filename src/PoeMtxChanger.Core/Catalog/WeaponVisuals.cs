using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Catalog;

public sealed record WeaponStance(string Id, string Name, string? MainClass, string? OffClass, string Idle);

/// <summary>PoE 1 held item types, named visual effect roles, and verified character idle clip families.</summary>
public static class WeaponVisuals
{
    private static readonly Dictionary<string,string> RoleByClass = new(StringComparer.OrdinalIgnoreCase)
    {
        ["One Hand Sword"]="OneHandedSwordEPKFile", ["Thrusting One Hand Sword"]="OneHandedSwordEPKFile",
        ["One Hand Axe"]="OneHandedAxeEPKFile", ["One Hand Mace"]="OneHandedMaceEPKFile", ["Sceptre"]="OneHandedMaceEPKFile",
        ["Dagger"]="DaggerEPKFile", ["Rune Dagger"]="DaggerEPKFile", ["Claw"]="ClawEPKFile",
        ["Bow"]="BowEPKFile", ["Staff"]="TwoHandedStaffEPKFile", ["Warstaff"]="TwoHandedStaffEPKFile",
        ["Two Hand Sword"]="TwoHandedSwordEPKFile", ["Two Hand Axe"]="TwoHandedAxeEPKFile", ["Two Hand Mace"]="TwoHandedMaceEPKFile",
        ["Shield"]="Shield"
    };
    public static IReadOnlyList<string> EffectRoles { get; } = RoleByClass.Values.Distinct().ToArray();

    public static IReadOnlyList<WeaponStance> Stances { get; } =
    [
        new("sword","One-handed sword","One Hand Sword",null,"idle_sword"),
        new("dual_swords","Two swords","One Hand Sword","One Hand Sword","idle_sword_sword"),
        new("sword_shield","Sword and shield","One Hand Sword","Shield","idle_sword_shield"),
        new("axe","One-handed axe","One Hand Axe",null,"idle_sword"),
        new("mace","One-handed mace","One Hand Mace",null,"idle_sword"),
        new("greatsword","Two-handed sword","Two Hand Sword",null,"idle_2hsword"),
        new("greataxe","Two-handed axe","Two Hand Axe",null,"idle_maul"),
        new("maul","Two-handed mace","Two Hand Mace",null,"idle_maul"),
        new("staff","Staff","Staff",null,"idle_staff"),
        new("bow","Bow","Bow",null,"idle_bow"),
        new("wand","Wand","Wand",null,"idle_wand"),
        new("dagger","Dagger","Dagger",null,"idle_dagger"),
        new("dual_daggers","Two daggers","Dagger","Dagger","idle_dagger_dagger"),
        new("claw","Claw","Claw",null,"idle_claw"),
        new("unarmed","Unarmed",null,null,"idle_unarmed")
    ];

    public static WeaponStance GetStance(string character, string? selected)
    {
        var id = selected ?? (character switch
        {
            "Marauder"=>"mace", "Ranger"=>"bow", "Witch"=>"wand",
            "Templar"=>"staff", "Shadow"=>"dagger", _=>"sword"
        });
        return Stances.FirstOrDefault(stance=>stance.Id==id) ?? Stances[0];
    }

    public static bool MatchesClass(string? itemClass, string? expected) =>
        itemClass != null && expected != null && (itemClass.Equals(expected,StringComparison.OrdinalIgnoreCase)
            || expected == "One Hand Sword" && itemClass == "Thrusting One Hand Sword"
            || expected == "One Hand Mace" && itemClass == "Sceptre"
            || expected == "Staff" && itemClass == "Warstaff"
            || expected == "Dagger" && itemClass == "Rune Dagger");

    public static TargetSuggestion? BaseItem(IEnumerable<TargetSuggestion> items, string? itemClass, string? preferred, string? otherHand = null)
    {
        if (itemClass == null) return null;
        var options = items.Where(item => (item.SlotKey is "weapon_main" or "shield")
            && MatchesClass(item.ItemClass,itemClass) && Path.GetExtension(item.Path).Equals(".ao",StringComparison.OrdinalIgnoreCase)).ToList();
        var saved = options.FirstOrDefault(item=>item.Path.Equals(preferred,StringComparison.OrdinalIgnoreCase));
        if (saved != null) return saved;
        if (otherHand != null)
        {
            var different=options.Where(item=>!item.Path.Equals(otherHand,StringComparison.OrdinalIgnoreCase)).ToList();
            if(different.Count>0)options=different;
        }
        var starter = itemClass switch
        {
            "One Hand Sword"=>"OneHandSword1", "One Hand Axe"=>"OneHandAxe1", "One Hand Mace"=>"OneHandMace1",
            "Two Hand Sword"=>"TwoHandSword2", "Two Hand Axe"=>"TwoHandAxe1", "Two Hand Mace"=>"TwoHandMace1",
            "Staff"=>"Staff1", "Bow"=>"Bow1", "Wand"=>"Wand1", "Dagger"=>"Dagger1", "Claw"=>"Claws1", "Shield"=>"ShieldStr1",
            _=>null
        };
        return (starter == null ? null : options.FirstOrDefault(item => item.VisualIdentityId == starter)) ?? options.FirstOrDefault();
    }

    public static string? EffectPath(CosmeticItem effect,string? itemClass)
        => effect.IsWeaponEffect && itemClass != null
            && (effect.AllowedItemClasses.Count==0||effect.AllowedItemClasses.Contains(itemClass,StringComparer.OrdinalIgnoreCase))
            && RoleByClass.TryGetValue(itemClass,out var role)
            ? effect.ResourceVariants.GetValueOrDefault(role) : null;

    public static string? SkinPath(CosmeticItem skin,TargetSuggestion weapon,string character)
    {
        if(skin.IsWeaponEffect || weapon.ItemClass == null || skin.AllowedItemClasses.Count>0
            && !skin.AllowedItemClasses.Contains(weapon.ItemClass,StringComparer.OrdinalIgnoreCase))return null;
        return skin.ResourceVariants.GetValueOrDefault(weapon.ItemClass) ?? skin.ClassVariants.GetValueOrDefault(character) ?? skin.HeldWeaponPath;
    }

    public static ReplacementMapping? ReplaceEffect(CosmeticItem? equipped,CosmeticItem replacement,TargetSuggestion? weapon)
    {
        if(equipped == null || weapon == null || EffectPath(equipped,weapon.ItemClass) is not {} target
            || EffectPath(replacement,weapon.ItemClass) is not {} donor || target.Equals(donor,StringComparison.OrdinalIgnoreCase))return null;
        return new ReplacementMapping { TargetPath=target, SourcePath=donor, SourceCosmeticId=replacement.Id, Note=weapon.ItemClass }.Normalized();
    }
}
