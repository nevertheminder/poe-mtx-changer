using System.Text.Json;
using System.Text.Json.Serialization;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Patching;
using PoeMtxChanger.Core.Util;

namespace PoeMtxChanger.Core.Profiles;

[JsonConverter(typeof(JsonStringEnumConverter<ProfileEntryKind>))]
public enum ProfileEntryKind { EquipmentSlot, SkillEffect, SkillAudio, Manual }

/// <summary>Equipment slots shown on the Character Customization screen.</summary>
public static class CosmeticSlots
{
    public static readonly IReadOnlyList<(string Key, string Name, CosmeticCategory Category)> All =
    [
        ("body", "Body armour", CosmeticCategory.BodyArmour),
        ("helmet", "Helmet", CosmeticCategory.Helmet),
        ("gloves", "Gloves", CosmeticCategory.Gloves),
        ("boots", "Boots", CosmeticCategory.Boots),
        ("weapon_main", "Main-hand weapon", CosmeticCategory.Weapon),
        ("weapon_off", "Off-hand weapon", CosmeticCategory.Weapon),
        ("shield", "Shield", CosmeticCategory.Shield),
        ("back", "Wings / back attachment", CosmeticCategory.WingsAndBack),
        ("character_effect", "Character effect", CosmeticCategory.CharacterEffect),
        ("aura", "Aura / herald", CosmeticCategory.AuraAndHerald),
        ("portal", "Portal", CosmeticCategory.Portal),
        ("pet", "Pet", CosmeticCategory.Pet),
        ("footprints", "Footprints / trail", CosmeticCategory.FootprintsAndTrails),
    ];
}

public sealed class ProfileEntry
{
    public BuffVisualSelection? BuffSelection { get; set; }
    public ProfileEntryKind Kind { get; set; }
    /// <summary>Slot key (see <see cref="CosmeticSlots"/>) or skill identifier.</summary>
    public string Key { get; set; } = "";
    public string? CosmeticId { get; set; }
    public string? CosmeticName { get; set; }
    public List<ReplacementMapping> Mappings { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

public sealed record BuffVisualSelection(string SkillId,string OriginalId,string DonorId);

public sealed class WardrobeWeaponSetup
{
    public string? StanceId { get; set; }
    public string? MainBasePath { get; set; }
    public string? OffBasePath { get; set; }
    public string? MainEffectId { get; set; }
    public string? OffEffectId { get; set; }
    public string? MainEffectTargetId { get; set; }
    public string? OffEffectTargetId { get; set; }
}

public sealed class CosmeticProfile
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Chosen visual tier per cosmetic id for tiered microtransactions (the game's desired_visual_tier, e.g.
    /// Lord of Ogham: 1 = black feathers, 2 = red cloth). Missing = the asset's own default.
    /// </summary>
    public Dictionary<string, int> VisualTiers { get; set; } = new(StringComparer.Ordinal);

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New profile";
    public string Description { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Informational: game build the profile was created against.</summary>
    public string? GameBuild { get; set; }
    public List<ProfileEntry> Entries { get; set; } = [];
    public List<string> WardrobeSkills { get; set; } = [];
    public WardrobeWeaponSetup WeaponSetup { get; set; } = new();
    /// <summary>Audio plans are applied per bank from the Sounds window, independently of visual mappings.</summary>
    public List<AudioSampleSwap> AudioSwaps { get; set; } = [];

    public IReadOnlyList<ReplacementMapping> ActiveMappings()
    {
        var weaponResources=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Entries.Where(e=>e.Enabled).SelectMany(entry=>entry.Mappings.Select(m=>m.Normalized())
            .Where(mapping=>entry.Kind!=ProfileEntryKind.EquipmentSlot || entry.Key is not ("weapon_main" or "weapon_off" or "weapon_effect_main" or "weapon_effect_off")
                || mapping.VisualIdentityEdit!=null || weaponResources.Add(mapping.TargetPath+"\0"+mapping.SourcePath))).ToList();
    }

    public ProfileEntry GetOrAddEntry(ProfileEntryKind kind, string key)
    {
        var e = Entries.FirstOrDefault(x => x.Kind == kind && x.Key == key);
        if (e is null) Entries.Add(e = new ProfileEntry { Kind = kind, Key = key });
        return e;
    }
}

public sealed class ProfileImportException(string message) : Exception(message);

/// <summary>Stores profiles as one JSON file each. Import validates the schema and the paths it contains.</summary>
public sealed class ProfileManager(string directory)
{
    public const long MaxImportBytes = 4 * 1024 * 1024;
    public string Directory { get; } = directory;

    private string PathFor(string id) => System.IO.Path.Combine(Directory, FileUtil.SafeFileName(id) + ".json");

    public IReadOnlyList<CosmeticProfile> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var list = new List<CosmeticProfile>();
        foreach (var f in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try { list.Add(Parse(File.ReadAllText(f))); }
            catch (Exception ex) when (ex is JsonException or ProfileImportException or IOException) { /* skip corrupt files */ }
        }
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Save(CosmeticProfile profile)
    {
        Sanitize(profile);
        profile.ModifiedUtc = DateTime.UtcNow;
        FileUtil.WriteJsonAtomic(PathFor(profile.Id), profile);
    }

    public bool Delete(string id)
    {
        var p = PathFor(id);
        if (!File.Exists(p)) return false;
        File.Delete(p);
        return true;
    }

    public void Export(CosmeticProfile profile, string destination)
    {
        Sanitize(profile);
        FileUtil.WriteJsonAtomic(destination, profile);
    }

    /// <summary>Imports a profile file. It always gets a fresh id so it cannot overwrite an existing profile.</summary>
    public CosmeticProfile Import(string sourceFile)
    {
        var info = new FileInfo(sourceFile);
        if (!info.Exists) throw new ProfileImportException("File not found.");
        if (info.Length > MaxImportBytes) throw new ProfileImportException("Profile file is too large.");
        var p = Parse(File.ReadAllText(sourceFile));
        p.Id = Guid.NewGuid().ToString("N");
        p.Name = string.IsNullOrWhiteSpace(p.Name) ? "Imported profile" : p.Name.Trim();
        Save(p);
        return p;
    }

    public static CosmeticProfile Parse(string json)
    {
        CosmeticProfile? p;
        try { p = JsonSerializer.Deserialize<CosmeticProfile>(json, FileUtil.Json); }
        catch (JsonException ex) { throw new ProfileImportException("Not a valid profile: " + ex.Message); }
        if (p is null) throw new ProfileImportException("Empty profile.");
        if (p.SchemaVersion < 1 || p.SchemaVersion > CosmeticProfile.CurrentSchemaVersion)
            throw new ProfileImportException($"Unsupported profile schema version {p.SchemaVersion}.");
        Sanitize(p);
        return p;
    }

    /// <summary>Profiles may only contain virtual game paths, never file-system paths.</summary>
    private static void Sanitize(CosmeticProfile p)
    {
        p.Entries ??= [];
        p.WeaponSetup ??= new();
        p.WardrobeSkills = (p.WardrobeSkills ?? []).Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 200)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(256).ToList();
        p.AudioSwaps ??= [];
        foreach (var swap in p.AudioSwaps)
        {
            if(swap is null || string.IsNullOrEmpty(swap.TargetBankSha256) || string.IsNullOrEmpty(swap.SourceBankSha256))
                throw new ProfileImportException("Invalid audio sample plan.");
            foreach (string bank in new[] { swap.TargetBank, swap.SourceBank })
                if (string.IsNullOrWhiteSpace(bank) || bank != System.IO.Path.GetFileName(bank) || bank.IndexOfAny(['/', '\\', ':']) >= 0 || !bank.EndsWith(".bank", StringComparison.OrdinalIgnoreCase))
                    throw new ProfileImportException("Invalid audio bank name.");
            if (swap.TargetIndex < 0 || swap.SourceIndex < 0 || swap.TargetBankSha256.Length != 64 || swap.SourceBankSha256.Length != 64)
                throw new ProfileImportException("Invalid audio sample plan.");
        }
        foreach (var e in p.Entries)
        {
            e.Mappings ??= [];
            e.Notes ??= [];
            for (int i = 0; i < e.Mappings.Count; i++)
            {
                var m = e.Mappings[i].Normalized();
                foreach (var path in new[] { m.TargetPath, m.SourcePath })
                {
                    if (string.IsNullOrWhiteSpace(path) || path.Contains("..") || path.Contains(':') || System.IO.Path.IsPathRooted(path) || path.Length > 400)
                        throw new ProfileImportException($"Invalid resource path in profile: '{path}'.");
                }
                e.Mappings[i] = m;
            }
        }
    }
}
