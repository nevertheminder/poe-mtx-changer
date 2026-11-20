using System.Text.Json.Serialization;

namespace PoeMtxChanger.Core.Catalog;

[JsonConverter(typeof(JsonStringEnumConverter<CosmeticCategory>))]
public enum CosmeticCategory
{
    BodyArmour,
    Weapon,
    Shield,
    Helmet,
    Gloves,
    Boots,
    WingsAndBack,
    CharacterEffect,
    SkillEffect,
    SkillAudio,
    AuraAndHerald,
    Portal,
    Pet,
    FootprintsAndTrails,
    Other,
}

[JsonConverter(typeof(JsonStringEnumConverter<CompatibilityStatus>))]
public enum CompatibilityStatus
{
    /// <summary>Passes every static check (resources resolve, policy allows). Not a guarantee of in-game behaviour.</summary>
    Supported,
    /// <summary>Technically possible but unverified or with warnings.</summary>
    Experimental,
    /// <summary>Blocked by a technical limitation or by policy.</summary>
    Unsupported,
    /// <summary>Not yet evaluated.</summary>
    Unknown,
}

public static class CosmeticCategoryInfo
{
    public static string DisplayName(CosmeticCategory c) => c switch
    {
        CosmeticCategory.BodyArmour => "Body armour",
        CosmeticCategory.WingsAndBack => "Wings & back attachments",
        CosmeticCategory.CharacterEffect => "Character effects",
        CosmeticCategory.SkillEffect => "Skill visual effects",
        CosmeticCategory.SkillAudio => "Skill audio effects",
        CosmeticCategory.AuraAndHerald => "Auras & heralds",
        CosmeticCategory.FootprintsAndTrails => "Footprints & trails",
        CosmeticCategory.Weapon => "Weapons",
        CosmeticCategory.Shield => "Shields",
        CosmeticCategory.Helmet => "Helmets",
        CosmeticCategory.Portal => "Portals",
        CosmeticCategory.Pet => "Pets",
        _ => c.ToString(),
    };
}

/// <summary>A cosmetic discovered in the user's installation (or a clearly marked mock entry).</summary>
public sealed record CosmeticItem
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required CosmeticCategory Category { get; init; }
    public CompatibilityStatus Status { get; init; } = CompatibilityStatus.Unknown;
    public string StatusReason { get; init; } = "";
    /// <summary>Folder that groups this cosmetic's files.</summary>
    public string RootPath { get; init; } = "";
    /// <summary>Entry-point resources (.aoc/.ao/.epk...) best suited as replacement sources.</summary>
    public IReadOnlyList<string> PrimaryResources { get; init; } = [];
    public IReadOnlyList<string> AllResources { get; init; } = [];
    public bool HasAudio { get; init; }
    public bool HasVisualEffects { get; init; }
    /// <summary>True for demo data. Mock items can never be applied.</summary>
    public bool IsMock { get; init; }
    /// <summary>Icon texture inside the game index (.dds), if known.</summary>
    public string? IconPath { get; init; }
    /// <summary>Where the entry came from: "data" (game tables), "heuristic" (path rules) or "mock".</summary>
    public string Origin { get; init; } = "heuristic";
    /// <summary>Per character class worn resource (class → path), from ItemVisualIdentity.</summary>
    public IReadOnlyDictionary<string, string> ClassVariants { get; init; } = new Dictionary<string, string>();
    /// <summary>Named effect roles, e.g. portal, map portal, or a skill variation column. Never chosen by file order.</summary>
    public IReadOnlyDictionary<string, string> ResourceVariants { get; init; } = new Dictionary<string, string>();
    /// <summary>MtxTypes.Type, e.g. "Helmet Skin".</summary>
    public string? MtxType { get; init; }
    public string? HeldWeaponPath { get; init; }
    public bool IsWeaponEffect => Category == CosmeticCategory.Weapon && string.Equals(MtxType,"Weapon Effect",StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<string> AllowedItemClasses { get; init; } = [];
    public string? AutomaticMappingIssue { get; init; }
}

/// <summary>A resource the client loads for a base (non-MTX) appearance, offered as a replacement target for a slot.</summary>
public sealed record TargetSuggestion(string SlotKey, string Label, string Path, string Source)
{
    /// <summary>Per character class worn resource of the base item (class → path).</summary>
    public IReadOnlyDictionary<string, string> ClassVariants { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> ResourceVariants { get; init; } = new Dictionary<string, string>();
    public string? ItemClass { get; init; }
    public string? IconPath { get; init; }
    public int? VisualIdentityRow { get; init; }
    public string? VisualIdentityId { get; init; }
    public string? AutomaticMappingIssue { get; init; }
}

public static class CharacterClasses
{
    /// <summary>Class key → ItemVisualIdentity column holding that class's worn files.</summary>
    public static readonly IReadOnlyList<(string Key, string Column)> All =
    [
        ("Marauder", "MarauderSMFiles"), ("Ranger", "RangerSMFiles"), ("Witch", "WitchSMFiles"), ("Duelist", "DuelistDexSMFiles"),
        ("Templar", "TemplarSMFiles"), ("Shadow", "ShadowSMFiles"), ("Scion", "ScionSMFiles"),
    ];
}
