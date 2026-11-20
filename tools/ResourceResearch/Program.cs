using PoeMtxChanger.Core.Formats.Dat;
using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Installation;
using PoeMtxChanger.Core.Storage;
using PoeMtxChanger.Core.Resources;

// Game folder and Oodle: first argument / second argument, otherwise detected on this computer.
var inst=(args.Length>0?GameInstallationDetector.Probe(args[0],"argument"):GameInstallationDetector.DetectAll().FirstOrDefault())
    ??throw new InvalidOperationException("Path of Exile not found; pass its folder as the first argument.");
using var codec=(NativeOodleCodec)(args.Length>1?NativeOodleCodec.TryLoad(args[1])
    :OodleLocator.FindAndLoad(AppContext.BaseDirectory)?.Codec??throw new InvalidOperationException("Oodle not found; pass oo2core_*_win64.dll as the second argument."));
using var source=GameArchive.Open(inst.OpenStorage(),codec);
var schema=DatSchema.LoadDefault();
var rawTables=new Dictionary<string,byte[]>();
string IntArray(DatTable t,int row,DatColumn c) {
if(!rawTables.TryGetValue(t.Schema.Name,out var bytes))rawTables[t.Schema.Name]=bytes=source.Read(t.Schema.GamePath);
int offset=4+row*t.RowLength+t.Schema.Columns.TakeWhile(x=>x!=c).Sum(x=>x.FixedSize);
int count=checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset)));
int start=4+t.RowCount*t.RowLength+checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset+8)));
return string.Join(',',Enumerable.Range(0,count).Select(i=>System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(start+i*4))));
}
string Cell(DatTable t,int r,DatColumn c) {
if(c.Name==null)return "";
try { if(c.IsArray)return c.Type switch {DatColumnType.String=>string.Join('|',t.GetStringArray(r,c.Name)),DatColumnType.Row or DatColumnType.ForeignRow=>string.Join(',',t.GetRowRefArray(r,c.Name)),DatColumnType.I32 or DatColumnType.U32 or DatColumnType.EnumRow=>IntArray(t,r,c),_=>"[...]"};
return c.Type switch{DatColumnType.String=>t.GetString(r,c.Name),DatColumnType.Row or DatColumnType.ForeignRow=>t.GetRowRef(r,c.Name)?.ToString()??"",DatColumnType.I32 or DatColumnType.U32 or DatColumnType.EnumRow=>t.GetInt32(r,c.Name).ToString(),DatColumnType.Bool=>t.GetBool(r,c.Name).ToString(),_=>"?"}; }catch(Exception ex){return "ERROR:"+ex.Message;}
}
void Dump(DatTable t,int r)=>Console.WriteLine($"#{r}: "+string.Join("; ",t.Schema.Columns.Where(c=>c.Name!=null).Select(c=>(c,Value:Cell(t,r,c))).Where(p=>p.Value is not ("" or "0" or "False")).Select(p=>p.c.Name+"="+p.Value)));
if(args[0]=="class-arrays") {
var t=DatTable.Parse(source.Read(schema.Tables["ItemVisualIdentity"].GamePath),schema.Tables["ItemVisualIdentity"]);
foreach(var c in PoeMtxChanger.Core.Catalog.CharacterClasses.All){var rows=Enumerable.Range(0,t.RowCount).Where(r=>t.GetStringArray(r,c.Column).Count>1).ToList();Console.WriteLine(c.Key+" multi-file rows="+rows.Count);foreach(var r in rows.Take(3))Console.WriteLine(t.GetString(r,"Id")+" "+string.Join(" | ",t.GetStringArray(r,c.Column)));}
}
else if(args[0]=="headers") foreach(var path in args.Skip(1)){Console.WriteLine("== "+path+" exists="+source.Exists(path));if(source.Exists(path)){var b=source.Read(path);Console.WriteLine("bytes="+b.Length+" hex="+Convert.ToHexString(b.AsSpan(0,Math.Min(160,b.Length))));}}
else if(args[0]=="extensions")foreach(var g in source.EnumeratePaths().Where(p=>p.StartsWith("audio/",StringComparison.OrdinalIgnoreCase)).GroupBy(System.IO.Path.GetExtension).OrderByDescending(g=>g.Count()))Console.WriteLine(g.Key+"="+g.Count()+" sample="+g.First());
else if(args[0]=="audit") {
DatTable T(string n)=>DatTable.Parse(source.Read(schema.Tables[n].GamePath),schema.Tables[n]);
var av=T("MiscAnimatedArtVariations");var sk=T("SkillArtVariations");var all=Enumerable.Range(0,av.RowCount).ToList();
var dup=all.GroupBy(r=>(Id:av.GetString(r,"Id"),Variant:av.GetInt32(r,"Variant"))).Where(g=>g.Count()>1).ToList();
Console.WriteLine($"animated duplicate (Id,Variant) groups={dup.Count}");foreach(var g in dup.Take(12)){Console.WriteLine($"{g.Key}");foreach(var r in g)Dump(av,r);}
Console.WriteLine($"SkillArt rows={sk.RowCount}; MTX links nonempty={Enumerable.Range(0,sk.RowCount).Count(r=>sk.GetRowRefArray(r,"Mtx").Count>0)}; AnimatedArt links nonempty={Enumerable.Range(0,sk.RowCount).Count(r=>sk.GetRowRefArray(r,"AnimatedArt").Count>0)}");
foreach(var n in new[]{"global_auras","cyclone","steelskin","fireball","hatred","flame_dash"}){var r=Enumerable.Range(0,sk.RowCount).FirstOrDefault(i=>sk.GetString(i,"Id")==n,-1);if(r<0)continue;Dump(sk,r);foreach(var seed in sk.GetRowRefArray(r,"AnimatedArt").OfType<int>()){Dump(av,seed);var id=av.GetString(seed,"Id");Console.WriteLine("variants "+string.Join(",",all.Where(i=>av.GetString(i,"Id")==id).Select(i=>av.GetInt32(i,"Variant"))));}}
}
else if(args[0]=="summary") foreach(var path in args.Skip(1)){Console.WriteLine("== "+path+" exists="+source.Exists(path));if(source.Exists(path)){var b=source.Read(path);var text=ReferenceExtractor.DecodeText(b);Console.WriteLine("bytes="+b.Length+"; refs="+string.Join("; ",ReferenceExtractor.Extract(b)));if(text!=null)Console.WriteLine(string.Join('\n',text.Split('\n').Take(22)));}}
else if(args[0]=="file") foreach(var path in args.Skip(1)){Console.WriteLine("== "+path+" exists="+source.Exists(path));if(source.Exists(path)){var b=source.Read(path);Console.WriteLine(ReferenceExtractor.DecodeText(b)??Convert.ToHexString(b.AsSpan(0,Math.Min(160,b.Length))));}}
else if(args[0]=="paths")foreach(var p in source.EnumeratePaths().Where(p=>args.Skip(1).All(x=>p.Contains(x,StringComparison.OrdinalIgnoreCase))).Take(150))Console.WriteLine(p);
else {foreach(var tab in args[0].Split('|')){if(!schema.Tables.TryGetValue(tab,out var ts)){Console.WriteLine("NO SCHEMA "+tab);continue;} if(args.Contains("--unknown"))ts=new DatTableSchema(ts.Name,ts.Columns.Select((c,i)=>c.Name==null?c with {Name="Unknown"+i}:c).ToList());Console.WriteLine("== "+tab+" exists="+source.Exists(ts.GamePath));if(!source.Exists(ts.GamePath))continue;DatTable t;try{t=DatTable.Parse(source.Read(ts.GamePath),ts);}catch(Exception ex){Console.WriteLine(ex.Message);continue;}Console.WriteLine($"{t.RowCount} rows; "+string.Join("; ",ts.Columns.Where(c=>c.Name!=null).Select(c=>c.Name+":"+c.Type+(c.IsArray?"[]":"")+(c.ReferencedTable!=null?"->"+c.ReferencedTable:""))));var rows=args.Length>1&&args[1].StartsWith("#")?args[1][1..].Split(',').Select(int.Parse):args.Length>1&&args[1]!="--unknown"?Enumerable.Range(0,t.RowCount).Where(r=>ts.Columns.Where(c=>c.Name!=null).Any(c=>Cell(t,r,c).Contains(args[1],StringComparison.OrdinalIgnoreCase))).Take(25):Enumerable.Range(0,Math.Min(8,t.RowCount));foreach(var r in rows)Dump(t,r);}}
