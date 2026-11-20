using PoeMtxChanger.Core.Formats.Dat;
using PoeMtxChanger.Core.Storage;

/// <summary>Read-only data exploration helpers for the probe ("explore" mode).</summary>
public static class Explore
{
    public static DatTable T(GameArchive a, DatSchema s, string name) => DatTable.Parse(a.Read(s.Tables[name].GamePath), s.Tables[name]);

    public static string Cell(DatTable t, int row, DatColumn c)
    {
        if (c.Name is null) return "";
        try
        {
            if (c.IsArray)
                return c.Type switch
                {
                    DatColumnType.String => "[" + string.Join(",", t.GetStringArray(row, c.Name)) + "]",
                    DatColumnType.Row or DatColumnType.ForeignRow => "[" + string.Join(",", t.GetRowRefArray(row, c.Name)) + "]",
                    _ => "[..]",
                };
            return c.Type switch
            {
                DatColumnType.String => t.GetString(row, c.Name),
                DatColumnType.I32 or DatColumnType.U32 or DatColumnType.EnumRow => t.GetInt32(row, c.Name).ToString(),
                DatColumnType.Bool => t.GetBool(row, c.Name).ToString(),
                DatColumnType.Row or DatColumnType.ForeignRow => t.GetRowRef(row, c.Name)?.ToString() ?? "null",
                _ => "?",
            };
        }
        catch (Exception ex) { return "!" + ex.GetType().Name; }
    }

    public static void Dump(DatTable t, int row, int maxLen = 90)
    {
        var parts = t.Schema.Columns.Where(c => c.Name != null).Select(c => $"{c.Name}={Cell(t, row, c)}").Where(p => !p.EndsWith("=") && !p.EndsWith("=[]") && !p.EndsWith("=null") && !p.EndsWith("=0") && !p.EndsWith("=False"));
        Console.WriteLine($"  #{row}: " + string.Join("; ", parts.Select(p => p.Length > maxLen ? p[..maxLen] + "…" : p)));
    }

    public static void Run(GameArchive a, DatSchema s, string[] args)
    {
        var mtx = T(a, s, "MtxTypes");
        var bit = T(a, s, "BaseItemTypes");
        var ivi = T(a, s, "ItemVisualIdentity");
        var gs = T(a, s, "MtxTypeGameSpecific");
        var shop = T(a, s, "ShopTag");

        if (needle0(args) is { } pfx && pfx.StartsWith("prefix:"))
        {
            var prefix = pfx[7..];
            Console.WriteLine($"== index paths starting with '{prefix}'");
            var paths = a.EnumeratePaths().Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(30).ToList();
            foreach (var p in paths) Console.WriteLine("  " + p);
            foreach (var p in paths.Where(p => p.EndsWith(".otc", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".ot", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".aoc", StringComparison.OrdinalIgnoreCase)).Take(3))
            {
                Console.WriteLine($"---- {p}");
                var text = PoeMtxChanger.Core.Resources.ReferenceExtractor.DecodeText(a.Read(p)) ?? "(binary)";
                foreach (var line in text.Split('\n').Take(40)) Console.WriteLine("    " + line.TrimEnd());
            }
            foreach (var name in new[] { "SkillArtVariations", "ItemisedVisualEffect" })
            {
                var t = T(a, s, name);
                Console.WriteLine($"== {name}: columns " + string.Join(", ", t.Schema.Columns.Select(c => (c.Name ?? "_") + ":" + c.Type + (c.IsArray ? "[]" : "") + (c.ReferencedTable is { } rt ? "->" + rt : ""))));
                for (int r = 0; r < Math.Min(3, t.RowCount); r++) Dump(t, r, 120);
            }
            return;
        }
        if (needle0(args) is { } gsArg && gsArg.StartsWith("gs:"))
        {
            int gsRow = int.Parse(gsArg[3..]);
            var ive = T(a, s, "ItemisedVisualEffect");
            var ivx = T(a, s, "ItemVisualEffect");
            var rows = Enumerable.Range(0, ive.RowCount).Where(r => ive.GetRowRef(r, "Mtx") == gsRow).ToList();
            Console.WriteLine($"== ItemisedVisualEffect rows for gs#{gsRow}: {rows.Count}");
            foreach (var r in rows)
            {
                Dump(ive, r, 160);
                if (ive.GetRowRef(r, "VisualIdentity") is int v)
                {
                    Console.WriteLine("   ItemVisualIdentity (all non-empty strings):");
                    foreach (var c in ivi.Schema.Columns.Where(c => c.Name != null && c.Type == DatColumnType.String))
                    {
                        var val = Cell(ivi, v, c);
                        if (val is not ("" or "[]")) Console.WriteLine($"     {c.Name} = {val}");
                    }
                    foreach (var p in ivi.Schema.Columns.Where(c => c.Name != null && c.Type == DatColumnType.String).Select(c => Cell(ivi, v, c)).SelectMany(x => x.Trim('[', ']').Split(',')).Where(x => x.Contains('/')))
                        Console.WriteLine($"     exists {p}: {a.Exists(p)}");
                }
                if (ive.GetRowRef(r, "VisualEffect") is int ve) Dump(ivx, ve, 160);
            }
            int withIdentity = Enumerable.Range(0, ive.RowCount).Count(r => ive.GetRowRef(r, "VisualIdentity") != null);
            int withEffect = Enumerable.Range(0, ive.RowCount).Count(r => ive.GetRowRef(r, "VisualEffect") != null);
            Console.WriteLine($"== ItemisedVisualEffect: {ive.RowCount} rows, with VisualIdentity {withIdentity}, with VisualEffect {withEffect}");
            return;
        }
        if (needle0(args) is { } baseArg && baseArg.StartsWith("base:"))
        {
            foreach (var name in baseArg[5..].Split('|'))
            {
                var r = Enumerable.Range(0, bit.RowCount).FirstOrDefault(i => bit.GetString(i, "Name") == name, -1);
                if (r < 0) { Console.WriteLine($"== {name}: not found"); continue; }
                Console.WriteLine($"== {name}: BaseItemTypes #{r} Id={bit.GetString(r, "Id")}");
                if (bit.GetRowRef(r, "ItemVisualIdentity") is int v)
                    foreach (var c in ivi.Schema.Columns.Where(c => c.Name != null && c.Type == DatColumnType.String))
                    {
                        var val = Cell(ivi, v, c);
                        if (val is not ("" or "[]")) Console.WriteLine($"     {c.Name} = {val}");
                    }
            }
            return;
        }
        if (needle0(args) is { } fileArg && fileArg.StartsWith("file:"))
        {
            foreach (var p in fileArg[5..].Split('|'))
            {
                Console.WriteLine($"---- {p} exists={a.Exists(p)}");
                if (!a.Exists(p)) continue;
                var text = PoeMtxChanger.Core.Resources.ReferenceExtractor.DecodeText(a.Read(p)) ?? "(binary)";
                foreach (var line in text.Split('\n').Take(35)) Console.WriteLine("    " + line.TrimEnd());
                Console.WriteLine("    refs: " + string.Join(", ", PoeMtxChanger.Core.Resources.ReferenceExtractor.Extract(a.Read(p))));
            }
            return;
        }
        if (needle0(args) is { } grepArg && grepArg.StartsWith("grep:"))
        {
            var parts = grepArg[5..].Split('|');
            var hits = a.EnumeratePaths().Where(p => parts.All(x => x.StartsWith('!') ? !p.Contains(x[1..], StringComparison.OrdinalIgnoreCase) : p.Contains(x, StringComparison.OrdinalIgnoreCase))).Take(60).ToList();
            Console.WriteLine($"== paths matching {grepArg[5..]}: {hits.Count}");
            foreach (var p in hits) Console.WriteLine("  " + p);
            var pv = T(a, s, "MicrotransactionPortalVariations");
            Console.WriteLine($"== portal variations with null MtxTypeGameSpecific: {Enumerable.Range(0, pv.RowCount).Count(r => pv.GetRowRef(r, "MtxTypeGameSpecific") is null)}");
            foreach (var r in Enumerable.Range(0, pv.RowCount).Where(r => pv.GetRowRef(r, "MtxTypeGameSpecific") is null).Take(3)) Dump(pv, r, 120);
            return;
        }
        static string? needle0(string[] a) => a.Length > 0 ? a[0] : null;
        var needle = args.Length > 0 ? args[0] : "Helmet";
        Console.WriteLine($"== MtxTypes rows matching '{needle}' (first 6)");
        var mtxRows = Enumerable.Range(0, mtx.RowCount).Where(r => mtx.GetString(r, "Name").Contains(needle, StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
        foreach (var r in mtxRows) Dump(mtx, r);

        Console.WriteLine("== ShopTag for those");
        foreach (var r in mtxRows) if (mtx.GetRowRef(r, "ShopTag") is int st) Dump(shop, st);

        Console.WriteLine("== MtxTypeGameSpecific rows pointing to first match");
        if (mtxRows.Count > 0)
            foreach (var r in Enumerable.Range(0, gs.RowCount).Where(r => gs.GetRowRef(r, "Type") == mtxRows[0]).Take(3)) Dump(gs, r);

        Console.WriteLine($"== BaseItemTypes with id containing 'Microtransaction' : {Enumerable.Range(0, bit.RowCount).Count(r => bit.GetString(r, "Id").Contains("Microtransaction", StringComparison.OrdinalIgnoreCase))}");
        Console.WriteLine($"== ItemVisualIdentity ids containing '{needle}' (first 6)");
        foreach (var r in Enumerable.Range(0, ivi.RowCount).Where(r => ivi.GetString(r, "Id").Contains(needle, StringComparison.OrdinalIgnoreCase)).Take(6)) Dump(ivi, r, 70);

        // Which tables reference MtxTypes or MtxTypeGameSpecific? (candidate link tables)
        Console.WriteLine("== tables with a column referencing MtxTypes / MtxTypeGameSpecific");
        foreach (var ts in s.Tables.Values.Where(t => t.Columns.Any(c => c.ReferencedTable is "MtxTypes" or "MtxTypeGameSpecific")).OrderBy(t => t.Name))
        {
            string info;
            try { info = a.Exists(ts.GamePath) ? $"{DatTable.Parse(a.Read(ts.GamePath), ts).RowCount} rows" : "absent"; }
            catch (Exception ex) { info = ex.Message; }
            var cols = string.Join(", ", ts.Columns.Where(c => c.Name != null && (c.FileExtension != null || c.ReferencedTable is "MtxTypes" or "MtxTypeGameSpecific" or "ItemVisualIdentity")).Select(c => c.Name + (c.FileExtension ?? "") ));
            Console.WriteLine($"  {ts.Name} ({info}): {cols}");
        }

        Console.WriteLine("== top ShopTag names among MtxTypes");
        foreach (var g in Enumerable.Range(0, mtx.RowCount).Select(r => mtx.GetRowRef(r, "ShopTag") is int st ? shop.GetString(st, "Name") : "(none)")
                     .GroupBy(x => x).OrderByDescending(g => g.Count()).Take(40))
            Console.Write($"{g.Key}={g.Count()}  ");
        Console.WriteLine();
        Console.WriteLine("== top MtxTypes.Type values");
        foreach (var g in Enumerable.Range(0, mtx.RowCount).Select(r => mtx.GetString(r, "Type")).GroupBy(x => x).OrderByDescending(g => g.Count()).Take(30))
            Console.Write($"'{g.Key}'={g.Count()}  ");
        Console.WriteLine();
    }
}
