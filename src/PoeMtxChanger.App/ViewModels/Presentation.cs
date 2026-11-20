using System.Windows;
using System.Windows.Media;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Patching;

namespace PoeMtxChanger.App.ViewModels;

/// <summary>Russian labels, colours and icons for core enums.</summary>
public static class Ui
{
    /// <summary>Real icons are fully opaque; mock (procedural) icons are dimmed under the category glyph.</summary>
    public static readonly System.Windows.Data.IValueConverter GlyphOpacity = new BoolToDouble(0.45, 1.0);

    private sealed class BoolToDouble(double whenTrue, double whenFalse) : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value is true ? whenTrue : whenFalse;
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    public static string Category(CosmeticCategory c) => c switch
    {
        CosmeticCategory.BodyArmour => "Body armour",
        CosmeticCategory.Weapon => "Weapons",
        CosmeticCategory.Shield => "Shield",
        CosmeticCategory.Helmet => "Helmet",
        CosmeticCategory.Gloves => "Gloves",
        CosmeticCategory.Boots => "Boots",
        CosmeticCategory.WingsAndBack => "Back",
        CosmeticCategory.CharacterEffect => "Character effect",
        CosmeticCategory.SkillEffect => "Skill effect",
        CosmeticCategory.SkillAudio => "Skill sound",
        CosmeticCategory.AuraAndHerald => "Aura",
        CosmeticCategory.Portal => "Portal",
        CosmeticCategory.Pet => "Pet",
        CosmeticCategory.FootprintsAndTrails => "Footprints",
        _ => "Other",
    };

    public static string Status(CompatibilityStatus s) => s switch
    {
        CompatibilityStatus.Supported => "Checked statically",
        CompatibilityStatus.Experimental => "Experimental",
        CompatibilityStatus.Unsupported => "Not supported",
        _ => "Not checked",
    };

    public static Brush StatusBrush(CompatibilityStatus s) => Res(s switch
    {
        CompatibilityStatus.Supported => "B.Ok",
        CompatibilityStatus.Experimental => "B.Warn",
        CompatibilityStatus.Unsupported => "B.Bad",
        _ => "B.Unknown",
    });

    public static string FileStatus(GameFileStatus? s) => s switch
    {
        GameFileStatus.RecoveryRequired => "Write interrupted: close the game and click “Restore originals” to recover from the journal",
        GameFileStatus.NoBaseline => "Original files (no backup yet)",
        GameFileStatus.Clean => "Original files (backup exists)",
        GameFileStatus.PatchedByUs => "Changed by this program",
        GameFileStatus.ChangedExternally => "Changed externally (game update / Steam check / another mod)",
        GameFileStatus.BaselineDamaged => "Backup damaged or missing",
        _ => "Unknown",
    };

    public static string IconKey(CosmeticCategory c) => c switch
    {
        CosmeticCategory.BodyArmour => "I.Sets",
        CosmeticCategory.Helmet or CosmeticCategory.Gloves or CosmeticCategory.Boots or CosmeticCategory.Shield => "I.Armour",
        CosmeticCategory.Weapon => "I.Weapon",
        CosmeticCategory.SkillEffect => "I.Skill",
        CosmeticCategory.AuraAndHerald or CosmeticCategory.CharacterEffect => "I.Aura",
        CosmeticCategory.WingsAndBack => "I.Wings",
        CosmeticCategory.Pet => "I.Pet",
        CosmeticCategory.Portal => "I.Portal",
        CosmeticCategory.FootprintsAndTrails => "I.Trail",
        CosmeticCategory.SkillAudio => "I.Sound",
        _ => "I.Emblem",
    };

    /// <summary>Placeholder artwork tint per category (no game art is bundled).</summary>
    public static (Color Glow, Color Core) Tint(CosmeticCategory c) => c switch
    {
        CosmeticCategory.Portal => (Color.FromRgb(0x3A, 0x6F, 0xC9), Color.FromRgb(0x9E, 0xC9, 0xFF)),
        CosmeticCategory.Pet => (Color.FromRgb(0x2E, 0x8A, 0x72), Color.FromRgb(0x8F, 0xE6, 0xC4)),
        CosmeticCategory.WingsAndBack => (Color.FromRgb(0xB8, 0x8A, 0x3A), Color.FromRgb(0xFF, 0xE3, 0xA0)),
        CosmeticCategory.SkillEffect or CosmeticCategory.AuraAndHerald => (Color.FromRgb(0x8A, 0x3B, 0xC9), Color.FromRgb(0xD9, 0xA8, 0xFF)),
        CosmeticCategory.CharacterEffect => (Color.FromRgb(0xC2, 0x3B, 0x2A), Color.FromRgb(0xFF, 0x9A, 0x6E)),
        CosmeticCategory.Weapon => (Color.FromRgb(0x9E, 0x2B, 0x24), Color.FromRgb(0xFF, 0x70, 0x5A)),
        CosmeticCategory.FootprintsAndTrails => (Color.FromRgb(0x2D, 0x7F, 0xA8), Color.FromRgb(0x8B, 0xDB, 0xFF)),
        CosmeticCategory.SkillAudio => (Color.FromRgb(0x5A, 0x63, 0x73), Color.FromRgb(0xB8, 0xC4, 0xD6)),
        _ => (Color.FromRgb(0xA8, 0x7A, 0x2C), Color.FromRgb(0xFF, 0xD7, 0x8A)),
    };

    public static string SlotName(string key) => key switch
    {
        "body" => "Body armour",
        "helmet" => "Helmet",
        "gloves" => "Gloves",
        "boots" => "Boots",
        "weapon_main" => "Weapon (main)",
        "weapon_off" => "Weapon (off)",
        "shield" => "Shield",
        "back" => "Wings / back",
        "character_effect" => "Character effect",
        "aura" => "Aura / herald",
        "portal" => "Portal",
        "pet" => "Pet",
        "footprints" => "Footprints",
        _ => key,
    };

    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>Profile slot a catalog category is assigned to; null when it needs a skill choice or is not assignable.</summary>
    public static string? SlotFor(CosmeticCategory c) => c switch
    {
        CosmeticCategory.BodyArmour => "body",
        CosmeticCategory.Helmet => "helmet",
        CosmeticCategory.Gloves => "gloves",
        CosmeticCategory.Boots => "boots",
        CosmeticCategory.Weapon => "weapon_main",
        CosmeticCategory.Shield => "shield",
        CosmeticCategory.WingsAndBack => "back",
        CosmeticCategory.CharacterEffect => "character_effect",
        CosmeticCategory.AuraAndHerald => "aura",
        CosmeticCategory.Portal => "portal",
        CosmeticCategory.Pet => "pet",
        CosmeticCategory.FootprintsAndTrails => "footprints",
        _ => null,
    };
}
