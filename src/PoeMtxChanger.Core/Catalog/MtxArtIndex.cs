using PoeMtxChanger.Core.Formats.Dat;

namespace PoeMtxChanger.Core.Catalog;

/// <summary>Shop name and inventory icon of a microtransaction that selects an art variant.</summary>
public sealed record MtxArt(string Name, string? Icon, string Type);

/// <summary>An MTX that sets a stat value, with the names of the base items (skill gems) it is limited to.</summary>
public sealed record MtxArtEntry(MtxArt Art, IReadOnlySet<string> BaseItems);

/// <summary>
/// Joins ItemisedVisualEffect rows to the stat values they set: a skill or aura skin MTX sets the art-variation stat
/// of SkillArtVariations to its variant number. Skill skins share one stat and are told apart by the gem base items
/// they list. Read-only; a (stat, value) pair claimed by several distinct MTX keeps every entry so callers can refuse
/// to guess.
/// </summary>
public static class MtxArtIndex
{
    public static Dictionary<(int Stat, int Value), List<MtxArtEntry>> Build(Func<string, DatTable?> table)
    {
        var result = new Dictionary<(int, int), List<MtxArtEntry>>();
        var baseItems = table("BaseItemTypes");
        var itemised = table("ItemisedVisualEffect");
        var gameTypes = table("MtxTypeGameSpecific");
        var types = table("MtxTypes");
        if (itemised == null || !itemised.HasColumn("Mtx") || !itemised.HasColumn("Stats") || !itemised.HasColumn("StatValues") ||
            gameTypes == null || !gameTypes.HasColumn("Type") || !gameTypes.HasColumn("DDSFile") ||
            types == null || !types.HasColumn("Type") || !types.HasColumn("Name")) return result;

        for (int row = 0; row < itemised.RowCount; row++)
        {
            if (itemised.GetRowRef(row, "Mtx") is not int mtx || mtx >= gameTypes.RowCount ||
                gameTypes.GetRowRef(mtx, "Type") is not int type || type >= types.RowCount) continue;
            string name = types.GetString(type, "Name").Trim();
            if (name.Length == 0 || DataCatalogBuilder.IsInternal(name)) continue;
            var stats = itemised.GetRowRefArray(row, "Stats");
            var values = itemised.GetInt32Array(row, "StatValues");
            if (stats.Count != values.Count) continue;
            string icon = gameTypes.GetString(mtx, "DDSFile");
            var art = new MtxArt(name, string.IsNullOrWhiteSpace(icon) ? null : icon, types.GetString(type, "Type"));
            var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (baseItems != null && baseItems.HasColumn("Name") && itemised.HasColumn("BaseItemTypes"))
                foreach (int item in itemised.GetRowRefArray(row, "BaseItemTypes").OfType<int>())
                    if (item < baseItems.RowCount && baseItems.GetString(item, "Name") is { Length: > 0 } baseName) bases.Add(baseName);
            var entry = new MtxArtEntry(art, bases);
            for (int i = 0; i < stats.Count; i++)
            {
                if (stats[i] is not int stat) continue;
                if (!result.TryGetValue((stat, values[i]), out var list)) result[(stat, values[i])] = list = [];
                list.Add(entry);
            }
        }
        return result;
    }

    /// <summary>
    /// The single MTX that sets <paramref name="stat"/> to <paramref name="value"/> for one of <paramref name="gems"/>
    /// (gem base item names), or null when none or several distinct MTX match.
    /// </summary>
    public static MtxArt? ForGem(this Dictionary<(int Stat, int Value), List<MtxArtEntry>> index, int stat, int value,
        IReadOnlyCollection<string> gems)
    {
        if (gems.Count == 0 || !index.TryGetValue((stat, value), out var entries)) return null;
        var arts = entries.Where(e => e.BaseItems.Overlaps(gems)).Select(e => e.Art).Distinct().Take(2).ToArray();
        return arts.Length == 1 ? arts[0] : null;
    }
}
