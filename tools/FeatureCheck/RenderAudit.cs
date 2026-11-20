using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Formats.Bundles;
using PoeMtxChanger.Core.Formats.Geometry;
using PoeMtxChanger.Core.Resources;
using PoeMtxChanger.Core.Storage;

internal static class RenderAudit
{
    private static readonly HashSet<string> TextFormats=new(StringComparer.OrdinalIgnoreCase)
        { ".ao",".aoc",".epk",".pet",".trl",".sm",".mat",".fxgraph" };
    private static readonly HashSet<string> VisualFormats=new(TextFormats,StringComparer.OrdinalIgnoreCase)
        { ".dds",".smd",".ast",".fmt",".png",".amd" };
    private static readonly Regex Extends=new("(?im)^[ \\t]*extends[ \\t]+\"((?:Metadata|Art)/[^\"\\r\\n]+)\"");
    private static readonly Regex PackCommand=new("(?m)^[ \\t]*([A-Za-z][A-Za-z0-9_]+)(?=[ \\t]+\"|[ \\t]*\\r?$)");

    public static void Run(GameArchive archive,IReadOnlyList<CosmeticItem> items,string output)
    {
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frontier=new List<(string Path,FileRecord Record)>();
        var kinds=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        var nodes=new Dictionary<string,int>(StringComparer.Ordinal);
        var state=new Dictionary<string,int>(StringComparer.Ordinal);
        var pack=new Dictionary<string,int>(StringComparer.Ordinal);
        var graphs=new Dictionary<string,int>(StringComparer.Ordinal);
        var compiledStages=new Dictionary<string,int>(StringComparer.Ordinal);
        var unsupportedStages=new Dictionary<string,int>(StringComparer.Ordinal);
        var definitions=new Dictionary<string,MaterialGraph?>(StringComparer.OrdinalIgnoreCase);
        var graphChecks=new Dictionary<(string Path,string Parameters),(string[] Compiled,string[] Unsupported)>();
        var missing=new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors=new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string original)
        {
            string path=BundleIndex.NormalizePath(original);
            string extension=Path.GetExtension(path);
            if(!VisualFormats.Contains(extension)||!seen.Add(path))return;
            if(!archive.TryGetRecord(path,out var record)){missing.Add(path);return;}
            Count(kinds,extension);
            if(TextFormats.Contains(extension))frontier.Add((path,record));
        }

        foreach(var item in items)
        {
            foreach(string path in item.PrimaryResources.Concat(item.ClassVariants.Values).Concat(item.ResourceVariants.Values))Add(path);
        }
        int checkedTexts=0;
        while(frontier.Count>0)
        {
            var current=frontier.OrderBy(x=>x.Record.BundleIndex).ThenBy(x=>x.Record.Offset).ToArray();
            frontier.Clear();
            foreach(var (path,record) in current)
            {
                if(record.Size>8*1024*1024){errors.Add(path+" (over 8 MiB)");continue;}
                try
                {
                    string text=ReferenceExtractor.DecodeText(archive.Read(record))??throw new InvalidDataException("Expected text");
                    checkedTexts++;
                    foreach(string child in ReferenceExtractor.Extract(text))Add(child);
                    string extension=Path.GetExtension(path);
                    if(extension is ".ao" or ".aoc")
                    {
                        var parent=Extends.Match(text);
                        if(parent.Success)
                        {
                            string source=parent.Groups[1].Value;
                            Add(Path.GetExtension(source).Length==0?source+".ao":source);
                        }
                    }
                    if(extension==".epk")foreach(Match match in PackCommand.Matches(text))Count(pack,match.Groups[1].Value);
                    if(extension is not (".mat" or ".fxgraph"))continue;
                    using var json=JsonDocument.Parse(text);
                    var root=json.RootElement;
                    if(extension==".fxgraph"&&root.TryGetProperty("nodes",out var graphNodes))
                        foreach(var node in graphNodes.EnumerateArray())
                            if(node.TryGetProperty("type",out var type)&&type.GetString() is {} kind)Count(nodes,kind);
                    if(root.TryGetProperty("graphinstances",out var instances))
                        foreach(var instance in instances.EnumerateArray())
                            if(instance.TryGetProperty("parent",out var parent)&&parent.GetString() is {} refPath)
                            {
                                Count(graphs,refPath);
                                if(extension!=".mat")continue;
                                var details=CheckStages(refPath,instance);
                                foreach(string stageName in details.Compiled)Count(compiledStages,stageName);
                                foreach(string nodeName in details.Unsupported)Count(unsupportedStages,nodeName);
                            }
                    if(root.TryGetProperty("defaultgraph",out var defaultGraph))root=defaultGraph;
                    foreach(var field in root.EnumerateObject().Where(p=>p.Name.StartsWith("overrid",StringComparison.Ordinal)))
                        Count(state,field.Name+" = "+field.Value);
                }
                catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException or InvalidOperationException)
                {errors.Add(path+" ("+ex.GetType().Name+")");}
            }
            Console.WriteLine($"Render audit: {checkedTexts:N0} text resources read, {frontier.Count:N0} new links queued.");
        }

        var report=new StringBuilder("# PoE wardrobe resource check\n\n");
        report.AppendLine($"Batch resource analysis for **{items.Count:N0} catalog entries**, read-only. The counters show coverage of files and graph branches. The look is checked by comparing frames.");
        report.AppendLine($"\n- Unique resource paths: {seen.Count:N0}\n- Text files read: {checkedTexts:N0}\n- Referenced paths missing from the index: {missing.Count:N0}\n- Resources with read or parse errors: {errors.Count:N0}");
        AddTable("Resources by format",kinds);
        AddTable("Frequently used material graphs",graphs,35);
        AddTable("Frequently used shader node types",nodes,45);
        AddTable("Material branches the wardrobe compiler can execute",compiledStages);
        AddTable("First operations the compiler cannot evaluate from a single input colour",unsupportedStages,35);
        AddTable("Render settings from resources",state,35);
        AddTable("Frequently used effect pack commands",pack,25);
        if(missing.Count>0)report.AppendLine("\n## Examples of missing paths\n\n"+string.Join("\n",missing.Take(15).Select(p=>"- `"+p+"`")));
        if(errors.Count>0)report.AppendLine("\n## Examples of read or parse errors\n\n"+string.Join("\n",errors.Take(15).Select(p=>"- `"+p+"`")));
        File.WriteAllText(output,report.ToString());
        Console.WriteLine($"Render audit written: {output}. {checkedTexts:N0} text resources, {nodes.Count:N0} distinct shader nodes.");

        void AddTable(string title,IReadOnlyDictionary<string,int> counts,int limit=int.MaxValue)
        {
            report.AppendLine($"\n## {title}\n\n| Item | Count |\n|---|---:|");
            foreach(var (key,value) in counts.OrderByDescending(p=>p.Value).ThenBy(p=>p.Key,StringComparer.Ordinal).Take(limit))
                report.AppendLine($"| `{key.Replace("|","\\|")}` | {value:N0} |");
        }

        (string[] Compiled,string[] Unsupported) CheckStages(string path,JsonElement instance)
        {
            var key=(path,instance.TryGetProperty("custom_parameters",out var values)?values.GetRawText():string.Empty);
            if(graphChecks.TryGetValue(key,out var found))return found;
            if(!definitions.TryGetValue(path,out var graph))
            {
                try { graph=archive.Exists(path)&&ReferenceExtractor.DecodeText(archive.Read(path)) is {} text?new MaterialGraph(text):null; }
                catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
                { graph=null;errors.Add(path+" (graph structure: "+ex.GetType().Name+")"); }
                definitions[path]=graph;
            }
            var supported=new List<string>();var unsupported=new List<string>();
            if(graph!=null)
                foreach(var channel in Enum.GetValues<MaterialColourChannel>())
                {
                    if(graph.TryCompile(channel,instance,out _,out string? reason,buildExecutable:false))supported.Add(channel.ToString());
                    else if(reason!=null)unsupported.Add(reason);
                }
            return graphChecks[key]=(supported.ToArray(),unsupported.ToArray());
        }
    }

    private static void Count(Dictionary<string,int> counts,string key)=>counts[key]=counts.GetValueOrDefault(key)+1;
}
