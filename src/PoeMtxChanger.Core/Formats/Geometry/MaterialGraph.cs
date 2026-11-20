using System.Linq.Expressions;
using System.Numerics;
using System.Text.Json;

namespace PoeMtxChanger.Core.Formats.Geometry;

public enum MaterialColourChannel { Albedo, Emissive, Glossiness }

/// <summary>Compiles colour-only branches which can run without a scene, texture sampler, or the game client.</summary>
public sealed class MaterialGraph
{
    private readonly record struct Id(string Type,int Index,string Stage);
    private sealed record Node(Id Id,string? Parameter,JsonElement Parameters);
    private readonly record struct Port(Id Node,string Variable,string Swizzle);
    private readonly record struct Link(Port From,Port To);
    private sealed record Value(Expression Code,byte Inputs);
    private readonly Dictionary<Id,Node> _nodes;
    private readonly HashSet<Id> _ambiguousNodes;
    private readonly Dictionary<(Id Node,string Variable),Link[]> _incoming;

    public string? BlendMode { get; }
    public string? LightingModel { get; }

    public MaterialGraph(string text)
    {
        using var document=JsonDocument.Parse(text);
        var root=document.RootElement;
        BlendMode=String(root,"overriden_blend_mode");
        LightingModel=String(root,"overriden_lighting_model");
        var groups=Array(root,"nodes").Select(n=>new Node(Key(n),String(n,"custom_parameter"),
            n.TryGetProperty("parameters",out var values)?values.Clone():default)).GroupBy(n=>n.Id).ToArray();
        _ambiguousNodes=groups.Where(g=>g.Skip(1).Any()).Select(g=>g.Key).ToHashSet();
        _nodes=groups.Where(g=>!_ambiguousNodes.Contains(g.Key)).ToDictionary(g=>g.Key,g=>g.First());
        _incoming=Array(root,"links").Select(l=>new Link(Endpoint(l.GetProperty("src")),Endpoint(l.GetProperty("dst"))))
            .GroupBy(l=>(l.To.Node,l.To.Variable)).ToDictionary(g=>g.Key,g=>g.ToArray());
    }

    public bool TryCompile(MaterialColourChannel channel,JsonElement graphInstance,out MaterialColourProgram? program,out string? unsupported,
        bool buildExecutable=true)
    {
        program=null;unsupported=null;
        string outputType=channel switch {MaterialColourChannel.Albedo=>"AlbedoColor",MaterialColourChannel.Emissive=>"EmissiveColor",_=>"Glossiness"};
        string inputType="Input"+outputType;
        var outputs=_nodes.Keys.Where(n=>n.Type==outputType).ToArray();
        if(outputs.Length!=1||!_nodes.Keys.Any(n=>n.Type==inputType))return false;
        var output=outputs[0];
        var overrides=Array(graphInstance,"custom_parameters").Where(x=>String(x,"name")!=null)
            .GroupBy(x=>String(x,"name")!,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Last(),StringComparer.Ordinal);
        var input=Expression.Parameter(typeof(Vector4),"input");
        string[] axes=[nameof(Vector4.X),nameof(Vector4.Y),nameof(Vector4.Z),nameof(Vector4.W)];
        Value Input(int component)=>new(Expression.Field(input,axes[component]),(byte)(1<<component));
        var cache=new Dictionary<(Id Node,int Component),Value>();
        var active=new HashSet<(Id Node,int Component)>();
        const int maxOperations=512;

        try
        {
            int length=channel==MaterialColourChannel.Glossiness?1:channel==MaterialColourChannel.Emissive?3:4;
            var result=Enumerable.Range(0,4).Select(i=>(i<length?Follow(output,"input",i):null)??Input(i)).ToArray();
            if(result.All(v=>v.Code is MemberExpression member&&member.Expression==input))
            {
                if(result.Select((v,i)=>v.Inputs==(1<<i)).All(v=>v))return false;
            }
            if(buildExecutable)
            {
                var newVector=Expression.New(typeof(Vector4).GetConstructor([typeof(float),typeof(float),typeof(float),typeof(float)])!,result.Select(v=>v.Code));
                program=new(Expression.Lambda<Func<Vector4,Vector4>>(newVector,input).Compile(),result.Select(v=>v.Inputs).ToArray());
            }
            return true;
        }
        catch(UnsupportedGraphException ex){unsupported=ex.Message;return false;}

        Value? Follow(Id id,string variable,int component)
        {
            if(!_incoming.TryGetValue((id,variable),out var links))return null;
            foreach(var link in links)
            {
                int relative=link.To.Swizzle.Length==0?component:link.To.Swizzle.IndexOf("xyzw"[component]);
                if(relative<0)continue;
                string source=link.From.Swizzle;
                int from=source.Length==0?relative:Axis(source[Math.Min(relative,source.Length-1)]);
                if(from<0||from>3)throw new UnsupportedGraphException("swizzle");
                return Visit(link.From.Node,from);
            }
            return null;
        }

        Value Visit(Id id,int component)
        {
            if(cache.TryGetValue((id,component),out var cached))return cached;
            if(active.Count>=64||cache.Count>=maxOperations||!active.Add((id,component)))throw new UnsupportedGraphException("graph size or cycle");
            if(!_nodes.TryGetValue(id,out var node))throw new UnsupportedGraphException(id.Type+(_ambiguousNodes.Contains(id)?" ambiguous reference":" reference"));
            string operation=Operation(id.Type);
            Value Read(string port,int? axis=null) => Follow(id,port,axis??component)??throw new UnsupportedGraphException(id.Type+"."+port);
            Value Parameter(float? defaultValue=null)
            {
                var setting=First(node.Parameters);
                var overridden=node.Parameter!=null&&overrides.TryGetValue(node.Parameter,out var item)&&item.TryGetProperty("parameters",out var incoming)
                    ?First(incoming):default;
                bool linearize=Boolean(overridden,"srgb")??Boolean(setting,"srgb")??false;
                JsonElement value=overridden.ValueKind==JsonValueKind.Object&&overridden.TryGetProperty("value",out var replacement)?replacement
                    :setting.ValueKind==JsonValueKind.Object&&setting.TryGetProperty("value",out var original)?original:default;
                if(value.ValueKind==JsonValueKind.Array&&value.GetArrayLength()>0)value=value[Math.Min(component,value.GetArrayLength()-1)];
                float number=value.ValueKind switch
                {
                    JsonValueKind.Number=>value.GetSingle(), JsonValueKind.True=>1, JsonValueKind.False=>0,
                    JsonValueKind.Undefined when defaultValue.HasValue=>defaultValue.Value,
                    _=>throw new UnsupportedGraphException(node.Parameter??id.Type+" parameter")
                };
                if(!float.IsFinite(number))throw new UnsupportedGraphException(id.Type+" non-finite parameter");
                return Constant(linearize&&component<3?DecodeSrgb(number):number);
            }
            Value Binary(Func<Expression,Expression,BinaryExpression> create) => Combine(create,Read("a"),Read("b"));
            Value Unary(Func<Expression,Expression> create,string port="input")
            {
                var source=Read(port);
                return new(create(source.Code),source.Inputs);
            }
            Value Function(string method,Value a,Value b) => new(Expression.Call(typeof(MathF),method,null,a.Code,b.Code),(byte)(a.Inputs|b.Inputs));
            Value Lerp()
            {
                var a=Read("a");var b=Read("b");var t=Read("alpha",0);
                return Combine(Expression.Add,a,Combine(Expression.Multiply,Combine(Expression.Subtract,b,a),t));
            }
            Value Select()
            {
                var condition=Read("condition",0);
                if(condition.Code is ConstantExpression {Value:float flag})return Read(flag==0?"a":"b");
                var a=Read("a");var b=Read("b");
                return new(Expression.Condition(Expression.Equal(condition.Code,Expression.Constant(0f)),a.Code,b.Code),(byte)(condition.Inputs|a.Inputs|b.Inputs));
            }
            Value Equal()
            {
                var a=Read("a",0);var b=Read("b",0);
                if(a.Code is ConstantExpression {Value:float av}&&b.Code is ConstantExpression {Value:float bv})return Constant(av==bv?1:0);
                return new(Expression.Condition(Expression.Equal(a.Code,b.Code),Expression.Constant(1f),Expression.Constant(0f)),(byte)(a.Inputs|b.Inputs));
            }
            Value FitRange()
            {
                var value=Read("value");var inMin=Read("in_min");var inMax=Read("in_max");
                var outMin=Read("out_min");var outMax=Read("out_max");
                var ratio=Combine(Expression.Divide,Combine(Expression.Subtract,value,inMin),Combine(Expression.Subtract,inMax,inMin));
                return Combine(Expression.Add,outMin,Combine(Expression.Multiply,ratio,Combine(Expression.Subtract,outMax,outMin)));
            }
            Value Luminance()
            {
                Value Weighted(int axis,float weight)=>Combine(Expression.Multiply,Read("input",axis),Constant(weight));
                return Combine(Expression.Add,Combine(Expression.Add,Weighted(0,.2126f),Weighted(1,.7152f)),Weighted(2,.0722f));
            }

            Value value=operation switch
            {
                var type when type==inputType=>Input(component),
                "ConstantPixel" or "ConstantVertex" or "ConstantFloat"=>Parameter(),
                "ConstantUInt"=>Parameter(node.Parameter==null?0:null),
                "ConstantBool" or "ConstantPixelBool"=>Parameter(0),
                "Zero"=>Constant(0), "Half"=>Constant(.5f), "One"=>Constant(1),
                "Multiply"=>Binary(Expression.Multiply), "Add"=>Binary(Expression.Add), "Subtract"=>Binary(Expression.Subtract),
                "Divide"=>Binary(Expression.Divide), "Min"=>Function(nameof(MathF.Min),Read("a"),Read("b")),
                "Max"=>Function(nameof(MathF.Max),Read("a"),Read("b")), "Power"=>Function(nameof(MathF.Pow),Read("a"),Read("b")),
                "MultiplyConst"=>Combine(Expression.Multiply,Read("a"),Parameter(1)),
                "AddConst"=>Combine(Expression.Add,Read("a"),Parameter(0)),
                "Dummy"=>Read("in_value"), "Lerp"=>Lerp(),
                "Saturate"=>Unary(code=>Expression.Call(typeof(Math),nameof(Math.Clamp),null,code,Expression.Constant(0f),Expression.Constant(1f))),
                "OneMinus"=>Unary(code=>Expression.Subtract(Expression.Constant(1f),code)),
                "SelectFloat"=>Select(), "EqualsUInt"=>Equal(), "FitRangeFromInput"=>FitRange(), "Luminance"=>Luminance(),
                _=>throw new UnsupportedGraphException(id.Type)
            };
            active.Remove((id,component));
            return cache[(id,component)]=value;
        }
    }

    private static Value Constant(float value)=>new(Expression.Constant(value),0);
    private static Value Combine(Func<Expression,Expression,BinaryExpression> operation,Value a,Value b)
        =>new(operation(a.Code,b.Code),(byte)(a.Inputs|b.Inputs));
    private static int Axis(char value)=>value switch { 'x' or 'r'=>0,'y' or 'g'=>1,'z' or 'b'=>2,'w' or 'a'=>3,_=>-1 };
    private static string Operation(string name)=>name.TrimEnd('2','3','4');
    private static Id Key(JsonElement e)=>new(e.GetProperty("type").GetString()!,e.GetProperty("index").GetInt32(),String(e,"stage")??string.Empty);
    private static Port Endpoint(JsonElement e)=>new(Key(e),String(e,"variable")??string.Empty,String(e,"swizzle")??string.Empty);
    private static JsonElement First(JsonElement e)=>e.ValueKind==JsonValueKind.Array&&e.GetArrayLength()>0?e[0]:default;
    private static IEnumerable<JsonElement> Array(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var items)&&items.ValueKind==JsonValueKind.Array?items.EnumerateArray():[];
    private static string? String(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
    private static bool? Boolean(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var value)&&value.ValueKind is JsonValueKind.False or JsonValueKind.True?value.GetBoolean():null;
    public static float DecodeSrgb(float value)=>value<=.04045f?value/12.92f:MathF.Pow((value+.055f)/1.055f,2.4f);
    public static float EncodeSrgb(float value)=>value<=.0031308f?12.92f*value:1.055f*MathF.Pow(value,1/2.4f)-.055f;
    private sealed class UnsupportedGraphException(string detail):Exception(detail);
}

public sealed record MaterialColourProgram(Func<Vector4,Vector4> Evaluate,IReadOnlyList<byte> Inputs);
