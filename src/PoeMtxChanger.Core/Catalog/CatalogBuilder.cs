using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoeMtxChanger.Core.Resources;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Catalog;

public sealed class CatalogRules
{
    public int Version { get; set; } = 1;
    public string Notes { get; set; } = "";
    public List<RootRule> Roots { get; set; } = [];
    public List<CategoryRule> CategoryRules { get; set; } = [];
    public Dictionary<string, StatusDefault> CategoryDefaults { get; set; } = [];

    public sealed class RootRule
    {
        public string Prefix { get; set; } = "";
        public int GroupDepth { get; set; } = 2;
    }

    public sealed class CategoryRule
    {
        public CosmeticCategory Category { get; set; }
        public List<string> Keywords { get; set; } = [];
    }

    public sealed class StatusDefault
    {
        public CompatibilityStatus Status { get; set; } = CompatibilityStatus.Unknown;
        public string Reason { get; set; } = "";
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static CatalogRules LoadDefault()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PoeMtxChanger.Core.DefaultCatalogRules.json")
            ?? throw new InvalidOperationException("Default catalog rules resource missing.");
        return JsonSerializer.Deserialize<CatalogRules>(s, JsonOptions)!;
    }

    public static CatalogRules Load(string path)
        => JsonSerializer.Deserialize<CatalogRules>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Empty rules file.");

    public StatusDefault DefaultFor(CosmeticCategory c)
        => CategoryDefaults.TryGetValue(c.ToString(), out var d) ? d
         : CategoryDefaults.TryGetValue("*", out var any) ? any
         : new StatusDefault();
}

/// <summary>Groups resources under the configured roots into cosmetic items using path heuristics.</summary>
public sealed class CatalogBuilder(CatalogRules rules)
{
    private static readonly string[] PrimaryOrder = [".aoc", ".ao", ".epk", ".pet", ".trl", ".sm", ".mat"];

    public IReadOnlyList<CosmeticItem> Build(IResourceSource source, CancellationToken ct = default)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in source.EnumeratePaths())
        {
            ct.ThrowIfCancellationRequested();
            var root = rules.Roots.FirstOrDefault(r => path.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase));
            if (root is null) continue;
            var rest = path[root.Prefix.Length..].Split('/');
            string key;
            if (rest.Length > root.GroupDepth)
                key = root.Prefix + string.Join('/', rest.Take(root.GroupDepth));
            else
                key = root.Prefix + string.Join('/', rest[..^1].Append(Path.GetFileNameWithoutExtension(rest[^1])));
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(path);
        }

        var items = new List<CosmeticItem>(groups.Count);
        foreach (var (key, files) in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var category = Categorize(key);
            var def = rules.DefaultFor(category);
            var primary = files
                .Where(f => PrimaryOrder.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => Array.IndexOf(PrimaryOrder, Path.GetExtension(f).ToLowerInvariant()))
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var kinds = files.Select(f => ResourceClassifier.Classify(f)).ToList();
            items.Add(new CosmeticItem
            {
                Id = key.ToLowerInvariant(),
                DisplayName = PrettyName(key),
                Category = category,
                Status = primary.Count == 0 ? CompatibilityStatus.Unsupported : def.Status,
                StatusReason = primary.Count == 0 ? "No entry-point resource (.aoc/.ao/.epk/...) found in this group." : def.Reason,
                RootPath = key,
                PrimaryResources = primary,
                AllResources = files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
                HasAudio = kinds.Any(k => k.Domain == ResourceDomain.Audio),
                HasVisualEffects = kinds.Any(k => k.Kind is ResourceKind.EffectPack or ResourceKind.ParticleEffect or ResourceKind.Trail),
            });
        }
        return items;
    }

    public CosmeticCategory Categorize(string path)
    {
        var tokens = path.ToLowerInvariant().Split(['/', '_', '-', '.', ' '], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var joined = path.ToLowerInvariant();
        foreach (var rule in rules.CategoryRules)
            foreach (var kw in rule.Keywords)
                if (kw.Contains('_') ? joined.Contains(kw) : tokens.Contains(kw) || tokens.Contains(kw + "s") || tokens.Contains(kw + "es"))
                    return rule.Category;
        return CosmeticCategory.Other;
    }

    private static string PrettyName(string key)
    {
        var last = key.TrimEnd('/').Split('/')[^1];
        var words = last.Replace('_', ' ').Replace('-', ' ');
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < words.Length; i++)
        {
            char c = words[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(words[i - 1])) sb.Append(' ');
            sb.Append(i == 0 || words[i - 1] == ' ' ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }
}

/// <summary>Demo data for UI work. Every entry is flagged <see cref="CosmeticItem.IsMock"/> and named "[MOCK] ...".</summary>
public static class MockCatalog
{
    public static IReadOnlyList<CosmeticItem> Create()
    {
        var list = new List<CosmeticItem>();
        void Add(CosmeticCategory c, string name, CompatibilityStatus s, string reason, bool audio = false, bool fx = true)
        {
            var id = $"mock/{c}/{name}".ToLowerInvariant().Replace(' ', '_');
            list.Add(new CosmeticItem
            {
                Id = id,
                DisplayName = "[MOCK] " + name,
                Category = c,
                Status = s,
                StatusReason = "Mock data — not from your installation. " + reason,
                RootPath = id,
                PrimaryResources = [id + "/main.aoc"],
                AllResources = [id + "/main.aoc", id + "/effect.epk"],
                HasAudio = audio,
                HasVisualEffects = fx,
                IsMock = true,
                Origin = "mock",
                IconPath = $"mock/icons/{list.Count}.dds",
            });
        }
        Add(CosmeticCategory.Portal, "Ember Rift Portal", CompatibilityStatus.Experimental, "Example of a portal swap.");
        Add(CosmeticCategory.Portal, "Abyssal Gate Portal", CompatibilityStatus.Supported, "Example of a fully resolved portal.");
        Add(CosmeticCategory.Pet, "Ashen Wisp Pet", CompatibilityStatus.Experimental, "Needs an owned pet.");
        Add(CosmeticCategory.WingsAndBack, "Carrion Wings", CompatibilityStatus.Experimental, "Needs an equipped back attachment.");
        Add(CosmeticCategory.BodyArmour, "Thornwarden Body Armour", CompatibilityStatus.Experimental, "Example armour.", fx: false);
        Add(CosmeticCategory.Helmet, "Thornwarden Helmet", CompatibilityStatus.Experimental, "Example helmet.", fx: false);
        Add(CosmeticCategory.Gloves, "Thornwarden Gloves", CompatibilityStatus.Experimental, "Example gloves.", fx: false);
        Add(CosmeticCategory.Boots, "Thornwarden Boots", CompatibilityStatus.Experimental, "Example boots.", fx: false);
        Add(CosmeticCategory.Weapon, "Gravecaller Sword", CompatibilityStatus.Unsupported, "Example of a missing dependency.");
        Add(CosmeticCategory.Shield, "Lantern Ward Shield", CompatibilityStatus.Experimental, "Example shield.", fx: false);
        Add(CosmeticCategory.CharacterEffect, "Smouldering Aura Effect", CompatibilityStatus.Experimental, "Needs an equipped character effect.");
        Add(CosmeticCategory.SkillEffect, "Frostbound Fireball", CompatibilityStatus.Experimental, "Example skill effect.");
        Add(CosmeticCategory.SkillAudio, "Thunderclap Strike Audio", CompatibilityStatus.Unsupported, "FMOD bank editing not implemented.", audio: true, fx: false);
        Add(CosmeticCategory.AuraAndHerald, "Herald of Embers Effect", CompatibilityStatus.Experimental, "Example herald.");
        Add(CosmeticCategory.FootprintsAndTrails, "Cinder Footprints", CompatibilityStatus.Experimental, "Needs equipped footprints.");
        return list;
    }

    /// <summary>
    /// Procedural BC1 (DXT1) icons for the mock items, so the DDS preview path can be exercised without game files.
    /// They are abstract gradients, not game art.
    /// </summary>
    public static Storage.InMemoryResourceSource CreateIconSource(IReadOnlyList<CosmeticItem> items)
    {
        var src = new Storage.InMemoryResourceSource();
        for (int i = 0; i < items.Count; i++)
            if (items[i].IconPath is { } p) src.Add(p, ProceduralBc1(64, 64, seed: i));
        return src;
    }

    internal static byte[] ProceduralBc1(int w, int h, int seed)
    {
        static ushort Rgb565(int r, int g, int b) => (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
        var rnd = new Random(seed * 7919 + 17);
        int hr = rnd.Next(80, 255), hg = rnd.Next(40, 200), hb = rnd.Next(20, 255);
        using var ms = new MemoryStream();
        var hdr = new byte[128];
        "DDS "u8.CopyTo(hdr);
        BitConverter.GetBytes(124).CopyTo(hdr, 4);
        BitConverter.GetBytes(0x1007).CopyTo(hdr, 8);
        BitConverter.GetBytes(h).CopyTo(hdr, 12);
        BitConverter.GetBytes(w).CopyTo(hdr, 16);
        BitConverter.GetBytes(32).CopyTo(hdr, 76);
        BitConverter.GetBytes(4).CopyTo(hdr, 80); // DDPF_FOURCC
        "DXT1"u8.CopyTo(hdr.AsSpan(84));
        ms.Write(hdr);
        for (int by = 0; by < h / 4; by++)
            for (int bx = 0; bx < w / 4; bx++)
            {
                double dx = (bx + 0.5) / (w / 4.0) - 0.5, dy = (by + 0.5) / (h / 4.0) - 0.5;
                double d = Math.Sqrt(dx * dx + dy * dy) * 2; // 0 centre … ~1.4 corners
                double k = Math.Clamp(1.15 - d, 0.05, 1);
                ushort c0 = Rgb565((int)(hr * k), (int)(hg * k), (int)(hb * k));
                ushort c1 = Rgb565((int)(hr * k * 0.35), (int)(hg * k * 0.35), (int)(hb * k * 0.35));
                if (c0 <= c1) c0 = (ushort)Math.Min(ushort.MaxValue, c1 + 1); // keep 4-colour mode
                ms.Write(BitConverter.GetBytes(c0));
                ms.Write(BitConverter.GetBytes(c1));
                ms.Write(BitConverter.GetBytes(((bx + by) & 1) == 0 ? 0x1B1B1B1Bu : 0xE4E4E4E4u));
            }
        return ms.ToArray();
    }
}
