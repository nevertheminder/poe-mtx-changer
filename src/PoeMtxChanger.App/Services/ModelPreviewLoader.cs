using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Numerics;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Formats.Dds;
using PoeMtxChanger.Core.Formats.Effects;
using PoeMtxChanger.Core.Formats.Geometry;
using PoeMtxChanger.Core.Formats.Shaders;
using PoeMtxChanger.Core.Rendering;
using PoeMtxChanger.Core.Resources;
using PoeMtxChanger.Core.Storage;
using PoeMtxChanger.Core.Profiles;

namespace PoeMtxChanger.App.Services;

public sealed record PreviewMesh(SkinnedMesh Geometry, ImageSource?[] Textures, string Path, HashSet<string> HiddenShapes)
{
    public ImageSource?[] Glow { get; init; } = [];
    public PreviewSurface[] Surfaces { get; init; } = [];
    public PreviewRenderPass[] RenderPasses { get; init; } = [];
    public AnimationSkeleton? LocalRig { get; init; }
    public AnimationClip? LocalIdle { get; init; }
    public float LocalIdleSeconds { get; init; }
    public AnimationClip? LocalRun { get; init; }
    public float LocalRunSeconds { get; init; }
    public int MountBone { get; init; } = -1;
    public bool MountFromOrigin { get; init; }
    public PreviewMesh? ParentMount { get; init; }
    public Matrix4x4 MountOffset { get; init; } = Matrix4x4.Identity;
    public Matrix4x4 RenderTransform { get; init; } = Matrix4x4.Identity;
    public bool IgnoreMountRotation { get; init; }
    public float? GroundOffsetZ { get; init; }
    public float? ActorOriginZ { get; init; }
    public float ClothFlex { get; init; }
    /// <summary>Bone physics of the phys_* chains of <see cref="LocalRig"/> (ClientAnimationController), or null.</summary>
    public BonePhysicsSettings? Physics { get; init; }
    public IReadOnlyList<PreviewEmitter> Emitters { get; init; } = [];
    public IReadOnlyList<PreviewTrail> Trails { get; init; } = [];

    public Vector3[] Pose(AnimationSkeleton? body,Matrix4x4[]? pose,float seconds,bool running=false)
    {
        var local=MountBone<0?pose:LocalPose(seconds,running);
        var mount=MountTransform(body,pose,seconds,running);
        return WorldPositions(local,mount,seconds);
    }

    public Vector3[] WorldPositions(Matrix4x4[]? localPose,Matrix4x4 mount,float seconds,
        Vector3[]? localNormals=null,Matrix4x4? trailingMount=null)
    {
        var positions=Geometry.Positions(localPose);
        if(ClothFlex>0&&positions.Length>0)
            BendCloth(positions,localNormals,mount,trailingMount,seconds);
        for(int i=0;i<positions.Length;i++)positions[i]=Vector3.Transform(positions[i],mount);
        return positions;
    }

    private void BendCloth(Vector3[] positions,Vector3[]? normals,Matrix4x4 mount,Matrix4x4? trailingMount,float seconds)
    {
        float crown=positions.Min(p=>p.Z),height=positions.Max(p=>p.Z)-crown;
        if(height<8)return;
        float lag=0;
        if(trailingMount is {} previous&&Matrix4x4.Invert(mount,out var inverse))
        {
            Vector3 probe=new(0,0,crown+height*.7f);
            lag=Math.Clamp((Vector3.Transform(probe,previous*inverse).Y-probe.Y)*.5f,-height*.12f,height*.12f);
        }

        float amplitude=height*ClothFlex;
        float stretch=1/(.75f*height);
        for(int i=0;i<positions.Length;i++)
        {
            var p=positions[i];
            float edge=(p.Z-crown-height*.25f)*stretch;
            if(edge<=0)continue;
            float a=seconds*2.2f-edge*2.7f+p.X*.026f,b=seconds*3.8f-edge*4.2f;
            float motion=lag+amplitude*(MathF.Sin(a)*.76f+MathF.Sin(b)*.24f);
            positions[i].Y+=edge*edge*motion;
            if(normals==null)continue;

            // Transform the normal by the inverse transpose of the local shear.
            float da=amplitude*.76f*MathF.Cos(a),db=amplitude*.24f*MathF.Cos(b);
            float dx=edge*edge*da*.026f;
            float dz=edge*stretch*(2*motion-edge*(2.7f*da+4.2f*db));
            var bent=normals[i]-normals[i].Y*new Vector3(dx,0,dz);
            if(bent.LengthSquared()>1e-8f)normals[i]=Vector3.Normalize(bent);
        }
    }

    public Matrix4x4 MountTransform(AnimationSkeleton? body,Matrix4x4[]? pose,float seconds=0,bool running=false)
    {
        if(MountBone<0 || body==null)return RenderTransform;
        if((GroundOffsetZ??ActorOriginZ) is float ground)
        {
            var root=body.BindWorld[MountBone].Translation;
            var rootPosition=pose==null?root:Vector3.Transform(root,pose[MountBone]);
            return RenderTransform*Matrix4x4.CreateTranslation(rootPosition.X,rootPosition.Y,ground);
        }
        var rig=ParentMount?.LocalRig??body;
        var parentPose=ParentMount==null?pose:ParentMount.LocalPose(seconds,running);
        var delta=parentPose?[MountBone]??Matrix4x4.Identity;
        var matrix=MountFromOrigin?MountOffset*rig.BindWorld[MountBone]*delta:delta;
        if(ParentMount!=null)matrix*=ParentMount.MountTransform(body,pose,seconds,running);
        if(IgnoreMountRotation&&Matrix4x4.Decompose(matrix,out var scale,out _,out var position))
            matrix=Matrix4x4.CreateScale(scale)*Matrix4x4.CreateTranslation(position);
        return RenderTransform*matrix;
    }

    public Matrix4x4[]? LocalPose(float seconds,bool running=false)
    {
        var (clip,duration)=running&&LocalRun!=null?(LocalRun,LocalRunSeconds):(LocalIdle,LocalIdleSeconds);
        return LocalRig!=null&&clip!=null&&duration>0?LocalRig.Skinning(clip,seconds%duration):null;
    }
}
public sealed record PreviewRenderPass(string Path,PreviewGameShader Shader,ImageSource?[] Textures);
public enum PreviewOpacity { Opaque, Cutout, Transparent, Additive, Multiply }
public enum PreviewReflectance { None, Metal, Specular, SpecularAlpha, MetallicCoating }
public sealed record PreviewSurface(ImageSource? Colour=null,ImageSource? Glow=null,bool Procedural=false)
{
    public ImageSource? Normal { get; init; }
    public ImageSource? ReflectanceMap { get; init; }
    public PreviewReflectance Reflectance { get; init; }
    public PreviewOpacity Opacity { get; init; }
    public bool NormalGlossAo { get; init; }
    public bool UsesRoughness { get; init; }
    public Vector2 ScrollSpeed { get; init; }
    public float RotationSpeed { get; init; }
    public Vector2 UvTiling { get; init; } = Vector2.One;
    public Vector2 UvMovement { get; init; }
    public float SoftDepth { get; init; }
    public float EmissionStrength { get; init; } = 1;
    public bool Unlit { get; init; }
    public int ColourGraphCount { get; init; }
    public PreviewCoating? Coating { get; init; }
    /// <summary>Pixel shader composed from the game's own fragments; when set the Direct3D preview draws with it.</summary>
    public PreviewGameShader? GameShader { get; init; }
    /// <summary>Textures of <see cref="GameShader"/> per register (null = missing, drawn as white).</summary>
    public ImageSource?[] GameTextures { get; init; } = [];
    /// <summary>The .mat the surface was read from (needed to append effect passes to it).</summary>
    public string? MaterialPath { get; init; }
    /// <summary>Extra draws of the shape: effect render passes appended to its material but drawn separately.</summary>
    public PreviewRenderPass[] ExtraPasses { get; init; } = [];
}
public sealed record PreviewCoating(float Brightness=1,Vector3 EdgeColour=default,float EdgePower=1,ImageSource? Mask=null,
    ImageSource? Metal=null,ImageSource? Normal=null);
public sealed record PreviewScene(IReadOnlyList<PreviewMesh> Meshes, AnimationSkeleton? Skeleton, string Description)
{
    public string PreferredIdle { get; init; } = "idle_unarmed";
    public bool HasIncompleteLayers { get; init; }
    public float? ReferenceExposure { get; init; }
}
public sealed record PreviewEmitter(ImageSource BrushTexture,Vector3 Start,Vector3 End,float Size,float Lifetime,float Rate,float Speed)
{
    public int StartBone { get; init; } = -1;
    public int EndBone { get; init; } = -1;
    public IReadOnlyList<PreviewAnchor> Anchors { get; init; } = [];
    public PreviewOpacity Blend { get; init; } = PreviewOpacity.Additive;
    public float Strength { get; init; } = 1.2f;
    public float[] SizeOverLife { get; init; } = [];
    public float[] AlphaOverLife { get; init; } = [];
    public float[] OpacityOverLife { get; init; } = [];
    public float[] StretchOverLife { get; init; } = [];
    public bool SoftGlow { get; init; }
    public bool FaceGround { get; init; }
    public Vector2 UvTiling { get; init; } = Vector2.One;
    public Vector2 UvMovement { get; init; }
    public float SoftDepth { get; init; }
    public string? Animation { get; init; }
    public float StartsAt { get; init; }
    public bool UsesCharacterAnimation { get; init; }
    public int AtlasSide { get; init; } = 1;
    public float AtlasSpeed { get; init; } = 1;
    public bool AtlasOnce { get; init; }
    /// <summary>The emitter as defined in the .pet file, used by the particle simulator.</summary>
    public PetEmitter? Definition { get; init; }
    public int? GpuEmitterSeed { get; init; }
    public FixedParticleMesh? ParticleMesh { get; init; }
    /// <summary>Particle variant of the emitter's material composed from the game's shaders.</summary>
    public PreviewGameShader? GameShader { get; init; }
    public ImageSource?[] GameTextures { get; init; } = [];
}
public readonly record struct PreviewAnchor(Vector3 Position,int Bone,Matrix4x4? SocketOffset=null,bool IgnoreRotation=false);
public sealed record PreviewTrail(ImageSource Texture,PreviewAnchor Anchor,float Width,float Duration,float MinimumDistance)
{
    /// <summary>Second edge of the ribbon when the trail is emitted between two anchors.</summary>
    public PreviewAnchor? EdgeAnchor { get; init; }
    public TrlTrail? Definition { get; init; }
    public float StartsAt { get; init; }
    public bool UsesLocalAnimation { get; init; }
    public PreviewGameShader? GameShader { get; init; }
    public ImageSource?[] GameTextures { get; init; } = [];
}

/// <summary>Reads only local visual files. No game client or renderer is opened.</summary>
public sealed class ModelPreviewLoader(AppServices app)
{
    private readonly List<string> _materialIssues=[];
    private readonly Dictionary<string,string> _text=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,MaterialGraph?> _graphs=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Path,Color? Tint),PreviewSurface> _particleMaterials=[];
    private readonly Dictionary<string,FixedParticleMesh?> _particleMeshes=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,Dictionary<string,Socket>> _socketsByText=[];
    // Placement confirmed against the official effect previews. A wildcard EPK anchor alone is not a ground marker.
    private static readonly HashSet<string> GroundEffects=new(StringComparer.OrdinalIgnoreCase)
    {
        "Metadata/Effects/Microtransactions/Aura/seraph_aura/seraph_aura.ao"
    };
    private static readonly Dictionary<string,string> Characters = new()
    {
        ["Marauder"]="Metadata/Characters/Str/Str.ao", ["Witch"]="Metadata/Characters/Int/Int.ao",
        ["Ranger"]="Metadata/Characters/Dex/Dex.ao", ["Duelist"]="Metadata/Characters/StrDex/StrDex.ao",
        ["Templar"]="Metadata/Characters/StrInt/StrInt.ao", ["Shadow"]="Metadata/Characters/DexInt/DexInt.ao",
        ["Scion"]="Metadata/Characters/StrDexInt/StrDexInt.ao",
    };
    private IResourceSource Source => app.Game.Resources ?? throw new InvalidOperationException("Read the game catalog first.");
    private string Text(string path)
    {
        if(_text.TryGetValue(path,out string? cached))return cached;
        string text=ReferenceExtractor.DecodeText(Source.Read(path))??throw new InvalidDataException("Expected text: "+path);
        _text.Add(path,text);
        return text;
    }
    private sealed record Socket(string? Parent,Matrix4x4 Offset,bool IgnoreRotation);
    private sealed record ObjectVisual(string? Skin,string? Skeleton,HashSet<string> Hidden,Dictionary<string,string> Materials,
        Dictionary<string,Socket> Sockets,string? AttachmentOverride,string? DefaultAnimation)
    {
        public HashSet<string> HiddenSiblingSegments { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> HiddenSiblingAttachments { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string,string[]> SegmentAliases { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public bool UsesBaseHeadController { get; init; }
        public string[] StandalonePasses { get; init; } = [];
        public Vector3 RenderScale { get; init; } = Vector3.One;
        public Vector3 RenderTranslation { get; init; }
    }

    private const string BaseHeadController="Metadata/Items/Armours/Helmets/BaseCharacterBody/BaseCharacterHeadController.ao";
    private ObjectVisual Object(string path, HashSet<string>? visited=null)
    {
        if(path.EndsWith(".sm",StringComparison.OrdinalIgnoreCase))return new(path,null,[],new(StringComparer.OrdinalIgnoreCase),new(StringComparer.OrdinalIgnoreCase),null,null);
        visited??=new(StringComparer.OrdinalIgnoreCase);
        if(visited.Count>=16||!visited.Add(path))throw new InvalidDataException("AO inheritance cycle.");
        string text=Text(path); ObjectVisual? parent=null;
        string? Value(string pattern) {var m=Regex.Match(text,pattern,RegexOptions.IgnoreCase);return m.Success?m.Groups[1].Value:null;}
        string? extends=Value("extends\\s+\"([^\"]+)\"");
        if(extends is not null and not "nothing")parent=Object(extends.EndsWith(".ao",StringComparison.OrdinalIgnoreCase)?extends:extends+".ao",visited);
        var hidden=parent?.Hidden??new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(Match m in Regex.Matches(text,"hide_parent_segments\\s*=\\s*\"([^\"]+)\"",RegexOptions.IgnoreCase))
            foreach(var name in m.Groups[1].Value.Split([' ', ',', ';'],StringSplitOptions.RemoveEmptyEntries))hidden.Add(name);
        ApplyDefaultVisualTier(text,hidden,path);
        var hiddenSiblingSegments=ReadHidingRules("hide_sibling_segments",parent?.HiddenSiblingSegments);
        var hiddenSiblingAttachments=ReadHidingRules("hide_sibling_attachments",parent?.HiddenSiblingAttachments);
        HashSet<string> ReadHidingRules(string field,HashSet<string>? inherited)
        {
            var set=inherited??new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(Match match in Regex.Matches(text,$@"(?im)^\s*(add_)?{field}[ \t]*=[ \t]*""([^""\r\n]*)"""))
            {
                if(!match.Groups[1].Success)set.Clear();
                set.UnionWith(SegmentNames(match.Groups[2].Value));
            }
            return set;
        }
        var aliases=parent?.SegmentAliases??new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase);
        foreach(Match match in Regex.Matches(text,@"(?im)^\s*alias[ \t]*=[ \t]*""([^""\r\n]+)"""))
        {
            var names=SegmentNames(match.Groups[1].Value);
            if(names.Length>1)aliases[names[0]]=names[1..];
        }
        var materials=parent?.Materials??new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(Match m in Regex.Matches(text,@"(?im)^\s*([^\s=]+)\s*=\s*""([^""\r\n]+\.mat)(?::\d+)?"""))
            materials[m.Groups[1].Value]=m.Groups[2].Value;
        var sockets=parent?.Sockets??new Dictionary<string,Socket>(StringComparer.OrdinalIgnoreCase);
        foreach(var (name,socket) in Sockets(text))sockets[name]=socket;
        var standalone=Regex.Match(text,@"(?im)^[ \t]*RenderPasses\s*=\s*'([^']*)'");
        var render=Regex.Match(text,@"(?im)^[ \t]*AnimatedRender\s*\{([^{}]*)\}").Groups[1].Value;
        float TransformField(string field,float fallback)
        {
            var match=Regex.Match(render,$@"(?im)^[ \t]*{field}[ \t]*=[ \t]*(\S+)");
            return match.Success&&float.TryParse(match.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out float value)
                &&float.IsFinite(value)?value:fallback;
        }
        var scale=parent?.RenderScale??Vector3.One;
        var translation=parent?.RenderTranslation??Vector3.Zero;
        return new(Value("\\bskin\\s*=\\s*\"([^\"]+\\.sm)\"")??parent?.Skin,
            Value("\\bskeleton\\s*=\\s*\"([^\"]+\\.ast)\"")??parent?.Skeleton,hidden,materials,sockets,
            Value("\\battach_bone_override\\s*=\\s*\"([^\"]+)\"")??parent?.AttachmentOverride,
            Value("\\bdefault_animation\\s*=\\s*\"([^\"]+)\"")??parent?.DefaultAnimation)
        {
            HiddenSiblingSegments=hiddenSiblingSegments,HiddenSiblingAttachments=hiddenSiblingAttachments,SegmentAliases=aliases,
            UsesBaseHeadController=path.Equals(BaseHeadController,StringComparison.OrdinalIgnoreCase)||parent?.UsesBaseHeadController==true,
            StandalonePasses=standalone.Success?StandalonePasses(standalone.Groups[1].Value,path):parent?.StandalonePasses??[],
            RenderScale=new(TransformField("x_scale",scale.X),TransformField("y_scale",scale.Y),TransformField("z_scale",scale.Z)),
            RenderTranslation=new(TransformField("x_translation",translation.X),TransformField("y_translation",translation.Y),TransformField("z_translation",translation.Z))
        };
    }

    private string[] StandalonePasses(string text,string path)
    {
        try
        {
            using var json=JsonDocument.Parse(text);
            if(json.RootElement.ValueKind!=JsonValueKind.Object||
                !json.RootElement.TryGetProperty("passes",out var passes)||passes.ValueKind!=JsonValueKind.Array)return [];
            return passes.EnumerateArray()
                .Where(pass=>pass.ValueKind==JsonValueKind.Object &&
                    pass.TryGetProperty("type",out var type)&&type.ValueKind==JsonValueKind.String&&type.ValueEquals("StandAlone") &&
                    (!pass.TryGetProperty("is_main",out var main)||main.ValueKind!=JsonValueKind.True))
                .Select(pass=>pass.TryGetProperty("filename",out var file)&&file.ValueKind==JsonValueKind.String?file.GetString():null)
                .OfType<string>().Where(file=>file.EndsWith(".mat",StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        }
        catch(JsonException)
        {
            _materialIssues.Add(Path.GetFileName(path)+": could not read the additional passes.");
            return [];
        }
    }

    /// <summary>Visual tiers chosen in the profile, by AO path and by AO file name.</summary>
    private readonly Dictionary<string,int> _visualTiers=new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tiers a tiered microtransaction's objects offer (on_or_create_state_tier_N handlers), ascending.</summary>
    public static int[] VisualTiers(IResourceSource source,CosmeticItem item)
    {
        var tiers=new SortedSet<int>();
        foreach(var path in item.PrimaryResources.Where(p=>p.EndsWith(".ao",StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6))
        {
            if(!source.Exists(path)||ReferenceExtractor.DecodeText(source.Read(path)) is not {} text)continue;
            foreach(Match m in Regex.Matches(text,@"(?im)^[ \t]*on_or_create_state_tier_(\d+)\s*="))tiers.Add(int.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture));
        }
        return tiers.ToArray();
    }

    private void ApplyDefaultVisualTier(string text,HashSet<string> hidden,string path)
    {
        // The profile's chosen tier (the game's desired_visual_tier), else the asset's fallback.
        var tier=Regex.Match(text,@"(?is)VariableExists\(\s*this\.desired_visual_tier\s*\).*?\}\s*,\s*\{\s*SetStateTo\(\s*tier\s*,\s*(\d+)\s*\)");
        if(!tier.Success)return;
        string chosen=_visualTiers.TryGetValue(path,out int desired)||_visualTiers.TryGetValue(Path.GetFileName(path),out desired)
            ? desired.ToString(CultureInfo.InvariantCulture) : tier.Groups[1].Value;
        var callback=Regex.Match(text,@"(?im)^[ \t]*on_or_create_state_tier_"+chosen+@"\s*=\s*\{\s*Call\(\s*(\w+)\s*\)\s*;?\s*\}");
        if(!callback.Success)callback=Regex.Match(text,@"(?im)^[ \t]*on_or_create_state_tier_"+tier.Groups[1].Value+@"\s*=\s*\{\s*Call\(\s*(\w+)\s*\)\s*;?\s*\}");
        if(!callback.Success)return;
        var body=Regex.Match(text,@"(?im)^[ \t]*"+Regex.Escape(callback.Groups[1].Value)+@"\s*=\s*\{([^{}]*)\}");
        foreach(Match operation in Regex.Matches(body.Groups[1].Value,@"(?i)\b(Hide|Show)MeshSegments\s*\(\s*([^()]+)\)"))
            foreach(string segment in SegmentNames(operation.Groups[2].Value))
                if(operation.Groups[1].Value.Equals("Hide",StringComparison.OrdinalIgnoreCase))hidden.Add(segment);
                else hidden.Remove(segment);
    }

    private static string[] SegmentNames(string value) => value.Split([',',';',' ','\t'],
        StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);

    private string? BaseCharacterHead(string helmet)
    {
        string file=Path.GetFileName(helmet);
        var branches=Regex.Matches(Text(BaseHeadController),
            @"'/(\w+\.ao)'\s*,\s*\{[^{}]*?AddAttached\(\s*'([^'\r\n]+\.ao)'",RegexOptions.IgnoreCase);
        foreach(Match match in branches.OrderByDescending(branch=>branch.Groups[1].Length))
            if(file.EndsWith(match.Groups[1].Value,StringComparison.OrdinalIgnoreCase)&&Source.Exists(match.Groups[2].Value))
                return match.Groups[2].Value;
        return null;
    }

    private Dictionary<string,Socket> Sockets(string text)
    {
        if(_socketsByText.TryGetValue(text,out var cache))return cache;
        var sockets=new Dictionary<string,Socket>(StringComparer.OrdinalIgnoreCase);
        foreach(Match m in Regex.Matches(text,@"(?im)^([ \t]*)socket[ \t]*=[ \t]*""([^""\r\n]+)""[^\n]*\n((?:\1[ \t]+\w+[ \t]*=[^\n]*\n)*)"))
        {
            string settings=m.Groups[3].Value;
            string? Setting(string key)=>Regex.Match(settings,@"(?im)^[ \t]*"+key+@"[ \t]*=[ \t]*""([^""\r\n]+)""").Groups[1] is {Success:true} field?field.Value:null;
            var scale=ParseVector(Setting("scale"))??Vector3.One;
            var translation=ParseVector(Setting("translation"))??Vector3.Zero;
            sockets.TryAdd(m.Groups[2].Value,new(Setting("parent"),Matrix4x4.CreateScale(scale)*Matrix4x4.CreateTranslation(translation),
                Setting("ignore_parent_rotation")=="yawpitchroll"));
        }
        return _socketsByText[text]=sockets;
    }

    private static PreviewAnchor? ResolveSocket(AnimationSkeleton rig,IReadOnlyDictionary<string,Socket> sockets,string name)
    {
        if(!sockets.TryGetValue(name,out var socket))return null;
        var offset=socket.Offset;
        bool ignore=socket.IgnoreRotation;
        for(int depth=0;depth<8;depth++)
        {
            if(socket.Parent==null)return new(offset.Translation,-1,offset,ignore);
            int bone=Bone(rig,socket.Parent);
            if(bone>=0)return new((offset*rig.BindWorld[bone]).Translation,bone,offset,ignore);
            if(!sockets.TryGetValue(socket.Parent,out socket))return null;
            offset*=socket.Offset;
            ignore|=socket.IgnoreRotation;
        }
        return null;
    }

    public PreviewScene Load(CosmeticItem? selected,string character,bool onCharacter,bool useProfile,CancellationToken ct,
        bool showWeapons=false)
    {
        ct.ThrowIfCancellationRequested();
        _visualTiers.Clear();
        if(app.Profiles.Active?.VisualTiers is {Count:>0} chosenTiers)
            foreach(var (id,tier) in chosenTiers)
                if(app.Game.Catalog.FirstOrDefault(i=>i.Id==id) is {} tieredItem)
                    foreach(var path in tieredItem.PrimaryResources.Where(p=>p.EndsWith(".ao",StringComparison.OrdinalIgnoreCase)))
                    {
                        _visualTiers[path]=tier;
                        _visualTiers[Path.GetFileName(path)]=tier;
                    }
        var meshes=new List<PreviewMesh>(); var notes=new List<string>(); AnimationSkeleton? skeleton=null;
        ObjectVisual? characterBody=null;
        PreviewCoating? characterCoating=null;
        string idle="idle_unarmed";
        if(onCharacter)
        {
            var body=characterBody=Object(Characters[character]);
            if(body.Skin==null||body.Skeleton==null)throw new InvalidDataException("No character model or skeleton was found.");
            skeleton=AnimationSkeleton.Read(Source.Read(body.Skeleton),app.Game.Codec);
            var hidden=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var worn=new Dictionary<CosmeticCategory,CosmeticItem>();
            var siblingSkins=new List<(PreviewMesh Mesh,ObjectVisual Visual)>();
            string? baseHead=null;
            var characterEffects=new List<CosmeticItem>();
            CosmeticItem? helmetAttachment=null;
            var profile=useProfile?app.Profiles.Active:null;
            if(profile != null)
                foreach(var entry in profile.Entries.Where(e=>e.Enabled&&e.CosmeticId!=null))
                {
                    var item=FindCosmetic(entry.CosmeticId);
                    if(item?.MtxType=="Helmet Attachment"){helmetAttachment=item;continue;}
                    if(item!=null&&(Wearable(item)||item.Category==CosmeticCategory.WingsAndBack))worn[item.Category]=item;
                    if(item?.MtxType=="Character Effect")characterEffects.Add(item);
                }
            if(selected?.MtxType=="Helmet Attachment")helmetAttachment=selected;
            else if(selected!=null&&(Wearable(selected)||selected.Category==CosmeticCategory.WingsAndBack))worn[selected.Category]=selected;
            if(selected?.MtxType=="Character Effect")
            { characterEffects.Clear();characterEffects.Add(selected); }
            foreach(var item in worn.Values)
            {
                ct.ThrowIfCancellationRequested();
                if(item.Category==CosmeticCategory.WingsAndBack)
                {
                    try
                    {
                        var attachment=BackObject(item,character);
                        int anchor=Array.FindIndex(skeleton.Bones,b=>b.Name.Equals("spine3",StringComparison.OrdinalIgnoreCase));
                        if(anchor<0)anchor=Array.FindIndex(skeleton.Bones,b=>b.Name.Equals("spine2",StringComparison.OrdinalIgnoreCase));
                        if(attachment!=null&&anchor>=0)
                        {
                            bool cape=attachment.Contains("/Capes/",StringComparison.OrdinalIgnoreCase)||
                                item.DisplayName.Contains("Cloak",StringComparison.OrdinalIgnoreCase)||item.DisplayName.Contains("Cape",StringComparison.OrdinalIgnoreCase);
                            AddAttachment(attachment,anchor,false,item.DisplayName,allowEffectsOnly:true,clothFlex:cape ? .035f : 0);
                        }
                        else notes.Add(item.DisplayName+": no unambiguous model or back attachment point.");
                    }
                    catch(Exception ex)when(ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
                    { notes.Add(item.DisplayName+": "+ex.Message); }
                    continue;
                }
                if(!item.ClassVariants.TryGetValue(character,out var path)) {notes.Add(item.DisplayName+": no variant for "+character);continue;}
                try
                {
                    var visual=Object(path);if(visual.Skin==null)throw new NotSupportedException("no SkinMesh");
                    var mesh=LoadMesh(visual);CheckBones(mesh.Geometry,skeleton);
                    mesh=mesh with { Emitters=LoadAttachmentEmitters(path,mesh.Path,skeleton,visual.DefaultAnimation,characterAnimations:true),
                        Trails=LoadAttachmentTrails(path,mesh.Path,skeleton) };
                    meshes.Add(mesh);
                    siblingSkins.Add((mesh,visual));
                    if(visual.UsesBaseHeadController&&item.Category==CosmeticCategory.Helmet)
                        baseHead=BaseCharacterHead(path);
                    string ao=ObjectText(path);
                    foreach(var child in ObjectChildren(ao))
                        foreach(var joint in AttachmentAnchors(visual.Skin,ao,skeleton,child))
                            AddAttachment(child.Path,joint.Bone,true,item.DisplayName,allowEffectsOnly:true,authoredSocket:joint);
                    hidden.UnionWith(visual.Hidden);
                    int anchor=Bone(skeleton,item.Category==CosmeticCategory.Helmet?"Head_Attachment":"aux_chest_attachment");
                    if(anchor>=0)
                        foreach(string effect in item.PrimaryResources.Where(p=>p.StartsWith("Metadata/Effects/",StringComparison.OrdinalIgnoreCase)
                            &&Path.GetExtension(p).ToLowerInvariant() is ".ao" or ".aoc").Distinct(StringComparer.OrdinalIgnoreCase).Take(4))
                            AddAttachment(effect,anchor,true,item.DisplayName,allowEffectsOnly:true);
                    foreach(string pack in EffectPacks(item))AddCharacterPack(pack,item.DisplayName);
                }
                catch(Exception ex)when(ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException){notes.Add(item.DisplayName+": "+ex.Message);}
            }
            if(baseHead!=null)
            {
                try
                {
                    var head=Object(baseHead);
                    AddHeadSkin(head);
                    var hiddenAttachments=siblingSkins.SelectMany(skin=>skin.Visual.HiddenSiblingAttachments)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach(var child in ObjectChildren(ObjectText(baseHead)))
                        if(child.Anchor=="<root>"&&(child.Alias==null||!hiddenAttachments.Contains(child.Alias)))
                            AddHeadSkin(Object(child.Path));

                    void AddHeadSkin(ObjectVisual visual)
                    {
                        if(visual.Skin==null)return;
                        var mesh=LoadMesh(visual);
                        CheckBones(mesh.Geometry,skeleton);
                        meshes.Add(mesh);
                        siblingSkins.Add((mesh,visual));
                        hidden.UnionWith(visual.Hidden);
                    }
                }
                catch(Exception ex)when(ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
                { notes.Add("Character head: "+ex.Message); }
            }
            foreach(var (mesh,visual) in siblingSkins)
                foreach(string segment in siblingSkins.Where(skin=>!ReferenceEquals(skin.Mesh,mesh))
                    .SelectMany(skin=>skin.Visual.HiddenSiblingSegments))
                {
                    mesh.HiddenShapes.Add(segment);
                    if(visual.SegmentAliases.TryGetValue(segment,out var parts))mesh.HiddenShapes.UnionWith(parts);
                }
            meshes.Insert(0,LoadMesh(body,hidden));
            if(helmetAttachment!=null&&UniqueObject(helmetAttachment) is {} headObject)
            {
                int anchor=Bone(skeleton,"Head_Attachment");
                if(anchor>=0)AddAttachment(headObject,anchor,true,helmetAttachment.DisplayName);
            }
            foreach(var item in characterEffects.DistinctBy(i=>i.Id))
            {
                var packs=EffectPacks(item).ToArray();
                if(packs.Length==0)notes.Add(item.DisplayName+": no effect pack found for the stance.");
                foreach(string pack in packs)AddCharacterPack(pack,item.DisplayName);
            }
            if(profile!=null)
            {
                var activeBuffs=profile.Entries.Where(entry=>entry.Enabled).Select(entry=>entry.BuffSelection).OfType<BuffVisualSelection>()
                    .Distinct().Take(8);
                var activePacks=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var selection in activeBuffs)
                {
                    var skill=app.Game.BuffSkills.FirstOrDefault(s=>s.Id.Equals(selection.SkillId,StringComparison.OrdinalIgnoreCase));
                    var visual=app.Game.BuffVisuals.FirstOrDefault(v=>v.Id.Equals(selection.DonorId,StringComparison.OrdinalIgnoreCase));
                    if(skill==null||visual==null||!skill.Accepts(visual))continue;
                    foreach(string path in visual.Roles.Where(role=>role.Key.StartsWith("EPKFile",StringComparison.OrdinalIgnoreCase))
                        .SelectMany(role=>role.Value).Where(path=>path.EndsWith(".epk",StringComparison.OrdinalIgnoreCase)&&Source.Exists(path))
                        .Where(activePacks.Add).Take(4))
                        AddCharacterPack(path,visual.DisplayName,onGround:skill.AllowsGlobalAuras);
                }
            }
            if(showWeapons)
            {
                var setup=profile?.WeaponSetup??new WardrobeWeaponSetup();
                var stance=WeaponVisuals.GetStance(character,setup.StanceId);
                var main=WeaponVisuals.BaseItem(app.Game.Suggestions,stance.MainClass,setup.MainBasePath);
                var off=WeaponVisuals.BaseItem(app.Game.Suggestions,stance.OffClass,setup.OffBasePath,main?.Path);
                idle=stance.Idle;
                AddWeapon(main,"weapon_main",stance.MainClass=="Bow" ? "L_Weapon" : "R_Weapon",setup.MainEffectId);
                AddWeapon(off,stance.OffClass=="Shield"?"shield":"weapon_off","L_Weapon",setup.OffEffectId);
            }
            if(characterCoating!=null)
                for(int i=0;i<meshes.Count;i++)
                {
                    var mesh=meshes[i];
                    if(mesh.Geometry.Shapes.Length>0&&(mesh.MountBone<0||mesh.Path.StartsWith("Art/Models/Items/Weapons/",StringComparison.OrdinalIgnoreCase)))
                        meshes[i]=ApplyCoating(mesh,characterCoating);
                }
            if(selected!=null&&!Wearable(selected)&&selected.Category!=CosmeticCategory.WingsAndBack&&selected.MtxType!="Character Effect")
                notes.Add("This model is available in the separate preview.");

            void AddCharacterPack(string path,string name,bool onGround=false)
            {
                try
                {
                    var pack=ReadPack(path);
                    var coating=ReadCoating(pack.Materials);
                    if(coating!=null)characterCoating=MergeCoating(characterCoating,coating);
                    var emitters=new List<PreviewEmitter>();
                    var trails=new List<PreviewTrail>();
                    foreach(var effect in pack.Particles)
                        emitters.AddRange(EmitAt(body.Skin,ObjectText(Characters[character]),skeleton,effect.Anchor,effect.Path,12));
                    foreach(var effect in pack.Trails)
                        trails.AddRange(ReadTrails(effect.Path,ResolveAnchors(body.Skin,ObjectText(Characters[character]),skeleton,effect.Anchor)));
                    if(emitters.Count>0||trails.Count>0)meshes.Add(new PreviewMesh(new(0,0,[],[],[]),[],path,[]){Emitters=emitters,Trails=trails});
                    int previous=meshes.Count;
                    foreach(var child in pack.Attached)
                    {
                        foreach(var joint in AttachmentAnchors(body.Skin,ObjectText(Characters[character]),skeleton,child))
                            AddAttachment(child.Path,joint.Bone,true,name,allowEffectsOnly:true,
                                placeOnGround:(onGround&&child.Anchor=="<lock>")||
                                    (child.Anchor=="*"&&GroundEffects.Contains(child.Path.Replace('\\','/'))),
                                authoredSocket:joint,lockToActorOrigin:child.Anchor=="<lock>"&&!onGround);
                    }
                    if(pack.HasUnsupportedAttachments)notes.Add(name+": a nested effect has additional attachment settings.");
                    if(pack.Particles.Count>0&&emitters.Count==0||coating==null&&emitters.Count==0&&trails.Count==0&&meshes.Count==previous)
                        notes.Add(name+": the definition has layers that could not be attached to the character.");
                }
                catch(Exception ex)when(ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException or JsonException)
                {notes.Add(name+": "+ex.Message);}
            }

            void AddWeapon(TargetSuggestion? weapon,string slot,string bone,string? effectId)
            {
                if(weapon==null)return;
                var skin=FindCosmetic(profile?.Entries.FirstOrDefault(e=>e.Kind==ProfileEntryKind.EquipmentSlot&&e.Enabled&&e.Key==slot)?.CosmeticId);
                string basePath=weapon.ClassVariants.GetValueOrDefault(character)??weapon.Path;
                string path=skin==null?basePath:WeaponVisuals.SkinPath(skin,weapon,character)??basePath;
                if(skin!=null&&path.Equals(basePath,StringComparison.OrdinalIgnoreCase))
                    notes.Add(skin.DisplayName+": the selected weapon has no compatible skin model.");
                var effect=FindCosmetic(effectId);
                string? epk=effect==null?null:WeaponVisuals.EffectPath(effect,weapon.ItemClass);
                int mount=Array.FindIndex(skeleton.Bones,b=>b.Name.Equals(bone,StringComparison.OrdinalIgnoreCase));
                if(mount<0){notes.Add(weapon.Label+": no weapon attachment found in the skeleton.");return;}
                int previousCount=meshes.Count;
                AddAttachment(path,mount,true,skin?.DisplayName??weapon.Label,epk:epk);
                if(meshes.Count==previousCount&&path!=basePath)AddAttachment(basePath,mount,true,weapon.Label,epk:epk);
            }
        }
        else
        {
            if(selected==null)throw new InvalidOperationException("Select a catalog item.");
            string? path=selected.ClassVariants.GetValueOrDefault(character);
            path??=selected.PrimaryResources.FirstOrDefault(p=>Path.GetExtension(p).ToLowerInvariant() is ".ao" or ".aoc" or ".sm");
            if(path==null)throw new NotSupportedException("This effect has no separate supported 3D model.");
            AddAttachment(path,-1,false,selected.DisplayName);
            if(meshes.Count==0)throw new NotSupportedException(notes.FirstOrDefault()??"No separate 3D model found.");
        }

        void AddAttachment(string path,int anchor,bool localOrigin,string name,string? epk=null,PreviewMesh? parent=null,bool allowEffectsOnly=false,
            float clothFlex=0,bool placeOnGround=false,PreviewAnchor? authoredSocket=null,int depth=0,bool lockToActorOrigin=false)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var visual=Object(path);
                var offset=authoredSocket?.SocketOffset??Matrix4x4.Identity;
                bool ignoreRotation=authoredSocket?.IgnoreRotation??false;
                if(!localOrigin&&skeleton!=null&&characterBody!=null&&
                    ResolveSocket(skeleton,characterBody.Sockets,visual.AttachmentOverride??"wings_attachment") is {Bone:>=0} socket)
                {
                    anchor=socket.Bone;offset=socket.SocketOffset??Matrix4x4.Identity;localOrigin=true;ignoreRotation=socket.IgnoreRotation;
                }
                if(visual.Skin==null&&!allowEffectsOnly&&parent==null&&offset==Matrix4x4.Identity)
                    throw new NotSupportedException("no SkinMesh model");
                var mesh=visual.Skin!=null?LoadMesh(visual):new PreviewMesh(new(0,0,[],[],[]),[],path,[]);
                AnimationSkeleton? rig=null;
                AnimationClip? localIdle=null,localRun=null;
                float duration=0,runDuration=0;
                if(visual.Skeleton!=null)
                    try
                    {
                        rig=AnimationSkeleton.Read(Source.Read(visual.Skeleton),app.Game.Codec,allowSupplementalTracks:true);
                        CheckBones(mesh.Geometry,rig);
                        string animationName=CharacterAnimation(character);
                        localIdle=rig.Clips.FirstOrDefault(c=>c.Name.Equals(visual.DefaultAnimation,StringComparison.OrdinalIgnoreCase))
                            ??rig.Clips.FirstOrDefault(c=>c.Name.Equals(animationName,StringComparison.OrdinalIgnoreCase))
                            ??rig.Clips.FirstOrDefault(c=>c.Name.Equals(animationName+"_town",StringComparison.OrdinalIgnoreCase))
                            ??rig.Clips.FirstOrDefault(c=>c.Name is "idle" or "idle_01" or "anim")
                            ??rig.Clips.FirstOrDefault(c=>c.Name.StartsWith("idle",StringComparison.OrdinalIgnoreCase));
                        if(clothFlex>0||BonePhysicsSettings.HasPhysicsBones(rig))
                        {
                            localRun=rig.Clips.FirstOrDefault(c=>c.Name.Equals(animationName+"_run",StringComparison.OrdinalIgnoreCase));
                            if(localRun!=null)runDuration=rig.Duration(localRun);
                        }
                        if(localIdle!=null)
                        {
                            duration=rig.Duration(localIdle);
                            if(rig.HasSupplementalTracks(localIdle))notes.Add(name+": the effect's main animation is shown without its additional tracks.");
                        }
                    }
                    catch(Exception ex)when(ex is IOException or NotSupportedException or InvalidDataException or InvalidOperationException)
                    { localIdle=localRun=null;duration=runDuration=0;notes.Add(name+": a static model is shown. "+ex.Message); }
                var emitters=LoadAttachmentEmitters(path,visual.Skin,rig,localIdle?.Name??visual.DefaultAnimation??CharacterAnimation(character)).ToList();
                var trails=LoadAttachmentTrails(path,visual.Skin,rig,localIdle?.Name).ToList();
                string ao=ObjectText(path);
                var packChildren=new List<PackLink>();
                var packPasses=new List<PackPass>();
                foreach(string packPath in ObjectEffectPacks(ao).Append(epk).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                    try
                    {
                        var pack=ReadPack(packPath);
                        packChildren.AddRange(pack.Attached);
                        packPasses.AddRange(EffectPasses(packPath,pack));
                        if(rig!=null)
                        {
                            foreach(var effect in pack.Particles)
                                emitters.AddRange(EmitAt(visual.Skin,ao,rig,effect.Anchor,effect.Path,12));
                            foreach(var effect in pack.Trails)
                                trails.AddRange(ReadTrails(effect.Path,ResolveAnchors(visual.Skin,ao,rig,effect.Anchor)));
                        }
                        var coating=ReadCoating(pack.Materials);
                        if(coating!=null)mesh=ApplyCoating(mesh,coating);
                        if(pack.HasUnsupportedAttachments)notes.Add(name+": a nested effect has additional attachment settings.");
                    }
                    catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException or InvalidOperationException or NotSupportedException)
                    { notes.Add(name+": "+Path.GetFileName(packPath)+": "+ex.Message); }
                mesh=ApplyAppendPasses(mesh,packPasses.Distinct().ToArray());
                if(visual.Skin==null&&emitters.Count==0&&trails.Count==0&&packChildren.Count==0&&rig?.Lights is not {Length:>0})
                    throw new NotSupportedException("no model and no supported particles.");
                if(!onCharacter&&parent==null)
                {
                    skeleton=rig;
                    idle=localIdle?.Name??idle;
                }
                // phys_* chains are simulated with the object's own settings (engine defaults where unset); the old
                // procedural cape sway stays only as the fallback when PMC_NO_BONE_PHYSICS=1.
                var physics=rig!=null&&BonePhysicsSettings.HasPhysicsBones(rig)&&Environment.GetEnvironmentVariable("PMC_NO_BONE_PHYSICS")!="1"
                    ?BonePhysicsSettings.Parse(ao)??new BonePhysicsSettings():null;
                var attached=mesh with { MountBone=anchor,MountFromOrigin=localOrigin,MountOffset=offset,IgnoreMountRotation=ignoreRotation,
                    RenderTransform=Matrix4x4.CreateScale(visual.RenderScale)*Matrix4x4.CreateTranslation(visual.RenderTranslation),
                    LocalRig=rig,LocalIdle=localIdle,LocalIdleSeconds=duration,LocalRun=localRun,LocalRunSeconds=runDuration,
                    Emitters=emitters,Trails=trails,ParentMount=parent,
                    Physics=physics,
                    ClothFlex=physics==null&&rig?.Bones.Any(b=>b.Name.StartsWith("phys_",StringComparison.OrdinalIgnoreCase))==true?clothFlex:0 };
                if(placeOnGround&&meshes.Count>0&&mesh.Geometry.Vertices.Length>0)
                {
                    // Asset coordinates are Z down; align the lower edge of the authored surface with the soles.
                    float floor=meshes[0].Geometry.Vertices.Max(v=>v.Position.Z);
                    float foot=mesh.Geometry.Positions(attached.LocalPose(0)).Max(p=>p.Z);
                    float transformedFoot=Vector3.Transform(new(0,0,foot),attached.RenderTransform).Z;
                    attached=attached with { GroundOffsetZ=floor-transformedFoot-.8f };
                }
                if(lockToActorOrigin&&meshes.Count>0)
                    attached=attached with { ActorOriginZ=meshes[0].Geometry.Vertices.Max(v=>v.Position.Z) };
                meshes.Add(attached);
                if(rig!=null&&depth<4)
                    foreach(var child in packChildren.Concat(ObjectChildren(ao)).Distinct())
                        foreach(var childSocket in AttachmentAnchors(visual.Skin,ao,rig,child))
                            AddAttachment(child.Path,childSocket.Bone,true,name,parent:attached,allowEffectsOnly:true,
                                authoredSocket:childSocket,depth:depth+1);
            }
            catch(Exception ex)when(ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException or JsonException)
            {notes.Add(name+": "+ex.Message);}
        }
        if(skeleton!=null)foreach(var mesh in meshes.Where(m=>m.MountBone<0))CheckBones(mesh.Geometry,skeleton);
        return new(meshes,skeleton,$"{meshes.Sum(m=>m.Geometry.Vertices.Length):N0} vertices · {meshes.Sum(m=>m.Geometry.Indices.Length/3):N0} triangles. " +
            "Geometry, textures and animations from the game. Lighting, glow and supported particles are approximated."+
            (notes.Count==0?"":"\n"+string.Join("\n",notes))+
            (_materialIssues.Count==0?"":"\nNot all layers were drawn: "+string.Join("; ",_materialIssues.Distinct())))
            {
                PreferredIdle=idle,HasIncompleteLayers=notes.Count>0||_materialIssues.Count>0,
                ReferenceExposure=LoadReferenceExposure()
            };
    }

    private float? LoadReferenceExposure()
    {
        const string reference="Metadata/EnvironmentSettings/2_3_2_Default.env";
        if(!Source.Exists(reference))return null;
        try
        {
            using var document=JsonDocument.Parse(Text(reference));
            var environment=document.RootElement;
            return environment.ValueKind==JsonValueKind.Object&&environment.TryGetProperty("camera",out var camera)&&
                camera.ValueKind==JsonValueKind.Object&&camera.TryGetProperty("exposure",out var exposure)&&
                exposure.ValueKind==JsonValueKind.Number&&exposure.TryGetSingle(out float value)&&float.IsFinite(value)&&value>0
                ?value:null;
        }
        catch(Exception ex)when(ex is IOException or JsonException or NotSupportedException)
        {
            app.Log(nameof(ModelPreviewLoader)).LogInformation(ex,"Cannot read the reference preview environment {Path}",reference);
            return null;
        }
    }
    private CosmeticItem? FindCosmetic(string? id)=>id==null?null:app.Game.Catalog.FirstOrDefault(i=>i.Id==id);
    private static Vector3? ParseVector(string? text)
    {
        var xyz=text?.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(xyz?.Length!=3||!float.TryParse(xyz[0],CultureInfo.InvariantCulture,out float x)
            ||!float.TryParse(xyz[1],CultureInfo.InvariantCulture,out float y)||!float.TryParse(xyz[2],CultureInfo.InvariantCulture,out float z)
            ||!float.IsFinite(x)||!float.IsFinite(y)||!float.IsFinite(z))return null;
        return new(x,y,z);
    }
    private static string CharacterAnimation(string character)=>Path.GetFileNameWithoutExtension(Characters[character]).ToLowerInvariant();
    private string? BackObject(CosmeticItem item,string character)
    {
        string? direct=item.ClassVariants.GetValueOrDefault(character)??UniqueObject(item);
        if(direct!=null)return direct;
        var packs=item.PrimaryResources.Where(p=>p.EndsWith(".epk",StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        if(packs.Length!=1||!Source.Exists(packs[0]))return null;
        var attached=Regex.Matches(Text(packs[0]),@"(?im)^\s*AttachedObject\s+""wings_attachment""\s+""([^""\r\n]+\.aoc?)""")
            .Select(m=>m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return attached.Length==1?attached[0]:null;
    }
    private static string? UniqueObject(CosmeticItem item)
    {
        var paths=item.PrimaryResources.Where(p=>Path.GetExtension(p).ToLowerInvariant() is ".ao" or ".aoc" or ".sm").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return paths.Length==1?paths[0]:null;
    }
    private static bool Wearable(CosmeticItem item)=>item.Category is CosmeticCategory.Helmet or CosmeticCategory.BodyArmour or CosmeticCategory.Gloves or CosmeticCategory.Boots;
    private static void CheckBones(SkinnedMesh mesh,AnimationSkeleton skeleton)
    {if(mesh.Vertices.Any(v=>v.Bones.Where((b,i)=>v.Weights[i]>0).Any(b=>b>=skeleton.Bones.Length)))throw new NotSupportedException("This model's bone order is incompatible with the skeleton.");}

    private PreviewMesh LoadMesh(ObjectVisual visual,HashSet<string>? hidden=null)
    {
        string path=visual.Skin??throw new InvalidDataException("SkinMesh is missing.");
        hidden??=new(visual.Hidden,StringComparer.OrdinalIgnoreCase);
        string text=Text(path);var smd=Regex.Match(text,"SkinnedMeshData\\s+\"([^\"]+)\"",RegexOptions.IgnoreCase);
        if(!smd.Success)throw new NotSupportedException("SM without SkinnedMeshData.");
        var mesh=SkinnedMesh.Read(Source.Read(smd.Groups[1].Value));
        var images=new List<ImageSource?>();
        var glow=new List<ImageSource?>();
        var materials=new List<PreviewSurface>(mesh.Shapes.Length);
        var baseMaterials=new List<string>();
        foreach(Match material in Regex.Matches(text,@"(?im)^\s*""([^""\r\n]*\.mat|)""[ \t]+(\d+)[ \t\r]*$"))
        {
            int count=int.Parse(material.Groups[2].Value);
            if(count>mesh.Shapes.Length-baseMaterials.Count)throw new InvalidDataException("SM material shape count out of bounds.");
            baseMaterials.AddRange(Enumerable.Repeat(material.Groups[1].Value,count));
        }
        if(baseMaterials.Count!=mesh.Shapes.Length)throw new InvalidDataException("SM material groups do not match SMD shapes.");
        var surfaces=new Dictionary<string,PreviewSurface>(StringComparer.OrdinalIgnoreCase);
        for(int i=0;i<mesh.Shapes.Length;i++)
        {
            string name=mesh.Shapes[i].Name;
            string materialPath=visual.Materials.GetValueOrDefault(name,baseMaterials[i]);
            var surface=new PreviewSurface();
            if(materialPath.Length==0)hidden.Add(name);
            else
            {
                if(!surfaces.TryGetValue(materialPath,out var layers))
                {
                    try
                    {
                        layers=LoadMaterial(materialPath) with{MaterialPath=materialPath};
                        layers=WithGameShader(layers,materialPath);
                        if(layers.GameShader==null&&layers.Colour==null&&layers.Glow==null&&!layers.Procedural)
                            _materialIssues.Add(Path.GetFileName(materialPath)+": colour texture not found.");
                    }
                    catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException or NotSupportedException or InvalidOperationException)
                    { _materialIssues.Add(Path.GetFileName(materialPath)+": "+ex.Message);layers=new(); }
                    surfaces[materialPath]=layers;
                }
                surface=layers;
                if(layers.Procedural)hidden.Add(name);
            }
            images.Add(surface.Colour);
            glow.Add(surface.Glow);
            materials.Add(surface);
        }
        var passes=new List<PreviewRenderPass>();
        foreach(string material in visual.StandalonePasses)
        {
            if(LoadGameShader(material,false,null) is var (shader,textures)&&(shader.BlendState!=0||KeepsOpaquePass(shader)))
                passes.Add(new(material,shader,textures));
            else if(Environment.GetEnvironmentVariable("PMC_GAME_SHADERS")!="0")
                _materialIssues.Add(Path.GetFileName(material)+": the additional pass is not supported.");
        }
        return new(mesh,images.ToArray(),path,hidden){Glow=glow.ToArray(),Surfaces=materials.ToArray(),RenderPasses=passes.ToArray()};
    }

    /// <summary>Material slots the Direct3D preview binds for a game shader (t0…t15).</summary>
    public const int GameShaderSlots=16;

    /// <summary>
    /// Adds the game-accurate shader of a mesh material when it composes and compiles; the hand-made surface stays as
    /// the fallback for the WPF viewport and for materials that do not compile. PMC_GAME_SHADERS=0 turns this off.
    /// </summary>
    private PreviewSurface WithGameShader(PreviewSurface surface,string materialPath)
    {
        if(LoadGameShader(materialPath,false,null) is not var (shader,textures))return surface;
        return surface with{GameShader=shader,GameTextures=textures,Opacity=shader.Blend??surface.Opacity,Procedural=false};
    }

    /// <summary>The game shader of a material and its textures per register, or null (disabled, not composable, too many textures).</summary>
    private (PreviewGameShader Shader,ImageSource?[] Textures)? LoadGameShader(string materialPath,bool particle,string? drawBlend,
        IReadOnlyList<string>? appended=null)
    {
        if(Environment.GetEnvironmentVariable("PMC_GAME_SHADERS")=="0")return null;
        var shader=GameShaders.TryBuild(Source,materialPath,out string? issue,particle,drawBlend,appended);
        if(shader==null)
        {
            if(issue!=null)app.Log(nameof(ModelPreviewLoader)).LogInformation("Game shader not used for {Material}: {Issue}",materialPath,issue);
            return null;
        }
        if(shader.Textures.Count>GameShaderSlots)return null;
        var textures=new ImageSource?[shader.Textures.Count];
        foreach(var binding in shader.Textures)
        {
            if(binding.Path is not {Length:>0} texture)continue;
            // Raw texels: the colour space is applied by the GPU (sRGB view), exactly as the game samples them.
            try { textures[binding.Slot]=Texture(texture,false,null,1,null,false); }
            catch(Exception ex)when(ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
            { _materialIssues.Add(Path.GetFileName(texture)+": "+ex.Message); }
        }
        return (shader,textures);
    }

    private PreviewSurface LoadMaterial(string path,bool particle=false,Color? tint=null)
    {
        if(!particle)return LoadMaterialCore(path,false,tint);
        if(_particleMaterials.TryGetValue((path,tint),out var material))return material;
        return _particleMaterials[(path,tint)]=LoadMaterialCore(path,true,tint);
    }

    private MaterialGraph? GetGraph(string path)
    {
        if(_graphs.TryGetValue(path,out var cached))return cached;
        MaterialGraph? graph=null;
        try
        {
            if(Source.Exists(path))graph=new MaterialGraph(Text(path));
        }
        catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { _materialIssues.Add(Path.GetFileName(path)+": "+ex.Message); }
        return _graphs[path]=graph;
    }

    private PreviewSurface LoadMaterialCore(string path,bool particle,Color? tint)
    {
        using var json=JsonDocument.Parse(Text(path));
        var root=json.RootElement;
        string? colour=null,emission=null,particleAtlas=null,normal=null,reflectancePath=null;
        string? blendMode=root.TryGetProperty("defaultgraph",out var defaults)&&defaults.TryGetProperty("overriden_blend_mode",out var defaultBlend)
            ?defaultBlend.GetString():null;
        bool colourSrgb=true,glowSrgb=true;
        int atlasColumns=1;
        bool opaque=false,additive=false,unlit=false,glowUsesRgb=false,packedNormal=false,roughness=false;
        bool cutout=false,transparent=false,multiply=false;
        var reflectance=PreviewReflectance.None;
        var scroll=Vector2.Zero;
        var tiling=Vector2.One;
        var uvMovement=Vector2.Zero;
        float softDepth=0;
        float uvRotation=0;
        float emissiveIntensity=1;
        float albedoGain=1,glowGain=1;
        (float Low,float High)? glowContrast=null;
        var albedoPrograms=new List<MaterialColourProgram>();
        var glowPrograms=new List<MaterialColourProgram>();
        var glossPrograms=new List<MaterialColourProgram>();
        if(json.RootElement.TryGetProperty("graphinstances",out var graphs))
            foreach(var graph in graphs.EnumerateArray())
            {
                string? graphPath=graph.TryGetProperty("parent",out var parentValue)?parentValue.GetString():null;
                var parent=Path.GetFileNameWithoutExtension(graphPath)??"";
                var definition=graphPath==null?null:GetGraph(graphPath);
                blendMode=definition?.BlendMode??blendMode;
                bool compiledAlbedo=Compile(MaterialColourChannel.Albedo,albedoPrograms);
                bool compiledGlow=Compile(MaterialColourChannel.Emissive,glowPrograms);
                if(!particle)Compile(MaterialColourChannel.Glossiness,glossPrograms);
                bool Compile(MaterialColourChannel channel,List<MaterialColourProgram> list)
                {
                    if(definition==null||!definition.TryCompile(channel,graph,out var program,out _))return false;
                    list.Add(program!);return true;
                }
                additive |= parent is "ForceAdditive" or "ForceAddablend";
                multiply |= parent is "ForceMultiply" or "ForceMultiplicative";
                unlit |= parent is "DisableLighting" or "NoLighting";
                cutout |= parent.StartsWith("ForceAlphaTest",StringComparison.OrdinalIgnoreCase);
                transparent |= parent is "ForceAlphablend" or "ForceAlphaBlend" or "ForceTransparent";
                roughness |= parent is "MetalRough" or "MetalRoughBN";
                if(parent=="SoftAlphaParticle")softDepth=20;
                if(!graph.TryGetProperty("custom_parameters",out var parameters))continue;
                var instanceTiling=Vector2.One;
                var instanceMovement=Vector2.Zero;
                foreach(var parameter in parameters.EnumerateArray())
                {
                    string name=parameter.GetProperty("name").GetString()??"";
                    if(!parameter.TryGetProperty("parameters",out var values)||values.GetArrayLength()==0)continue;
                    if(values[0].TryGetProperty("value",out var setting))
                    {
                        if(!compiledAlbedo&&parent=="TWEAK_Albedo"&&name=="Albedo_MULT1"&&setting.ValueKind==JsonValueKind.Number)
                            albedoGain=Math.Clamp(setting.GetSingle(),0,16);
                        if((name is "UsesRoughness" or "UsesGloss") && (setting.ValueKind is JsonValueKind.True or JsonValueKind.False))
                            roughness=name=="UsesRoughness"?setting.GetBoolean():!setting.GetBoolean();
                        if(name=="Moving"&&(parent is "add_Scroll" or "AddScroll")&&setting.ValueKind==JsonValueKind.Array&&setting.GetArrayLength()==2)
                            scroll=new(Math.Clamp(setting[0].GetSingle(),-2,2),Math.Clamp(setting[1].GetSingle(),-2,2));
                        if(name=="params"&&parent=="RotateUVOld"&&setting.ValueKind==JsonValueKind.Array&&setting.GetArrayLength()==2)
                            uvRotation=Math.Clamp(setting[0].GetSingle(),-6,6);
                        if(name=="Distance"&&parent=="SoftAlphaParticle"&&setting.ValueKind==JsonValueKind.Number)
                            softDepth=Math.Clamp(setting.GetSingle(),0,200);
                        if(parent=="ScaleMoveUV"&&setting.ValueKind==JsonValueKind.Array&&setting.GetArrayLength()==2)
                        {
                            var adjustment=new Vector2(Math.Clamp(setting[0].GetSingle(),-32,32),Math.Clamp(setting[1].GetSingle(),-32,32));
                            if(name=="UV_tiling")instanceTiling=adjustment;
                            if(name=="UV_move")instanceMovement=adjustment;
                        }
                        if(!compiledGlow&&parent=="Emissive_Color_Tweaks")
                        {
                            if(name=="Brightness"&&setting.ValueKind==JsonValueKind.Number)glowGain=Math.Clamp(setting.GetSingle(),0,16);
                            if(name=="Contrast"&&setting.ValueKind==JsonValueKind.Array&&setting.GetArrayLength()==2)
                            {
                                float low=setting[0].GetSingle(),high=setting[1].GetSingle();
                                if(high>low)glowContrast=(low,high);
                            }
                        }
                    }
                    if(!values[0].TryGetProperty("path",out var value)||value.GetString() is not {} texture)continue;
                    bool srgb=!values[0].TryGetProperty("srgb",out var srgbFlag)||srgbFlag.ValueKind!=JsonValueKind.False;
                    if(name.StartsWith("Albedo",StringComparison.OrdinalIgnoreCase)||name.Equals("base_color_texture",StringComparison.OrdinalIgnoreCase)
                        ||name.Equals("diffuse_texture",StringComparison.OrdinalIgnoreCase)||name=="ColourSpecular_TEX")
                    {
                        colour=texture;
                        colourSrgb=srgb;
                        if(name=="ColourSpecular_TEX"||name.Equals("AlbedoSpecMask_TEX",StringComparison.OrdinalIgnoreCase))
                        {
                            opaque=name=="ColourSpecular_TEX"||parent.EndsWith("Opaque",StringComparison.OrdinalIgnoreCase);
                            reflectancePath=texture;
                            reflectance=PreviewReflectance.SpecularAlpha;
                        }
                    }
                    else if(name is "NormalGlossAO_TEX" or "normal_texture" or "Normal_TEX")
                    { normal=texture;packedNormal=name=="NormalGlossAO_TEX"; }
                    else if(name is "Metal_TEX" or "SpecularMask_TEX" or "Specular_TEX")
                    { reflectancePath=texture;reflectance=name=="Specular_TEX"?PreviewReflectance.Specular:PreviewReflectance.Metal; }
                    else if(emission==null&&(name.Contains("emissive",StringComparison.OrdinalIgnoreCase)||name.Contains("glow",StringComparison.OrdinalIgnoreCase)))
                    {
                        emission=texture;
                        glowSrgb=srgb;
                        glowUsesRgb=parent=="GlowAdd"&&name=="Glow_TEX";
                    }
                    else if(particle&&parent.Equals("LIGHTNING_Main",StringComparison.OrdinalIgnoreCase)&&name.Contains("Lightning Texture",StringComparison.OrdinalIgnoreCase))
                    {
                        particleAtlas=texture;
                        if(name.Contains("4x1",StringComparison.OrdinalIgnoreCase))atlasColumns=4;
                    }
                }
                uvMovement=uvMovement*instanceTiling+instanceMovement;
                tiling*=instanceTiling;
            }
        switch(blendMode)
        {
            case "Opaque" or "OpaqueNoShadow": additive=multiply=cutout=transparent=false;opaque=!particle;break;
            case "Additive" or "Addablend": additive=true;multiply=cutout=transparent=false;break;
            case "MultiplicitiveBlend": multiply=true;additive=cutout=transparent=false;break;
            case "AlphaTest" or "AlphaTestWithShadow" or "AlphaTestNoGI": cutout=true;additive=multiply=transparent=false;break;
            case "AlphaBlend" or "AlphaBlendNoGI" or "NoZWriteAlphaBlend" or "DownScaledAlphaBlend" or "ForegroundAlphaBlend":
                transparent=true;additive=multiply=cutout=false;break;
        }
        if(json.RootElement.TryGetProperty("textures",out var textures))
        {
            var files=textures.EnumerateArray().Select(t=>t.GetProperty("filename").GetString()).OfType<string>().ToArray();
            colour??=files.FirstOrDefault(p=>Regex.IsMatch(p,@"colour|color|diffuse|albedo",RegexOptions.IgnoreCase))
                ?? (particle || files.Length==1 && !Regex.IsMatch(files[0],@"normal|specular|roughness|metallic",RegexOptions.IgnoreCase) ? files.FirstOrDefault() : null);
            emission??=files.FirstOrDefault(p=>Regex.IsMatch(Path.GetFileName(p),@"glow|emissive",RegexOptions.IgnoreCase));
        }
        // A missing glow layer must not turn the underlying textured shape into an untextured mesh.
        ImageSource? LoadLayer(string? texture,bool solid,float gain=1,(float Low,float High)? contrast=null,bool additiveRgb=false,
            bool srgb=true,IReadOnlyList<MaterialColourProgram>? programs=null,IReadOnlyList<MaterialColourProgram>? gloss=null)
        {
            if(texture==null)return null;
            try { return Texture(texture,solid,tint,gain,contrast,additiveRgb,srgb,programs,gloss,roughness); }
            catch(Exception ex)when(ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
            { _materialIssues.Add(Path.GetFileName(texture)+": "+ex.Message);return null; }
        }
        ImageSource? albedo=LoadLayer(colour,opaque,albedoGain,srgb:colourSrgb,programs:albedoPrograms);
        ImageSource? glow=emission==colour?null:LoadLayer(emission,false,glowGain,glowContrast,glowUsesRgb,glowSrgb,glowPrograms);
        if(particleAtlas!=null&&albedo==null&&glow==null&&LoadLayer(particleAtlas,false) is BitmapSource atlas)
        {
            glow=new CroppedBitmap(atlas,new System.Windows.Int32Rect(0,0,Math.Max(1,atlas.PixelWidth/atlasColumns),atlas.PixelHeight));
            glow.Freeze();
        }
        if(additive){glow??=albedo;albedo=null;emissiveIntensity=1.35f;}
        return new PreviewSurface(albedo,glow,additive&&glow==null)
        {
            Normal=particle?null:LoadLayer(normal,false,gloss:packedNormal?glossPrograms:null),ReflectanceMap=particle?null:LoadLayer(reflectancePath,false),
            Reflectance=reflectance,NormalGlossAo=packedNormal,UsesRoughness=roughness,
            Opacity=multiply?PreviewOpacity.Multiply:additive||albedo==null&&glow!=null?PreviewOpacity.Additive:transparent?PreviewOpacity.Transparent:cutout&&!opaque?PreviewOpacity.Cutout:PreviewOpacity.Opaque,
            Unlit=unlit,ScrollSpeed=scroll,RotationSpeed=uvRotation,EmissionStrength=emissiveIntensity,
            UvTiling=tiling,UvMovement=uvMovement,SoftDepth=softDepth,ColourGraphCount=albedoPrograms.Count+glowPrograms.Count+glossPrograms.Count
        };
    }

    private BitmapSource Texture(string path,bool forceOpaque,Color? tint,float gain,(float Low,float High)? contrast,bool additiveRgb,
        bool srgb=true,IReadOnlyList<MaterialColourProgram>? programs=null,IReadOnlyList<MaterialColourProgram>? gloss=null,bool roughness=false)
    {
        var decoded=DdsDecoder.Decode(Source.Read(path),p=>Source.Exists(p)?Source.Read(p):null);
        var pixels=decoded.Bgra;
        ApplyMaterialColour(pixels,srgb,programs);
        if(gloss is { Count:>0 })
        {
            var lookup=new byte[256];
            for(int sample=0;sample<lookup.Length;sample++)
            {
                float value=sample/255f;
                Vector4 colour=new(roughness?1-value:value);
                foreach(var program in gloss)colour=program.Evaluate(colour);
                lookup[sample]=ToByte(roughness?1-colour.X:colour.X);
            }
            for(int i=3;i<pixels.Length;i+=4)pixels[i]=lookup[pixels[i]];
        }
        byte[]? corrected=null;
        if(gain!=1||contrast!=null)
        {
            corrected=new byte[256];
            for(int i=0;i<corrected.Length;i++)
            {
                float encoded=i/255f;
                float linear=encoded<=.04045f?encoded/12.92f:MathF.Pow((encoded+.055f)/1.055f,2.4f);
                if(contrast is {} edges)
                {
                    float t=Math.Clamp((linear-edges.Low)/(edges.High-edges.Low),0,1);
                    linear=t*t*(3-2*t);
                }
                linear=Math.Clamp(linear*gain,0,1);
                corrected[i]=(byte)Math.Clamp(MathF.Round(255*(linear<=.0031308f?12.92f*linear:1.055f*MathF.Pow(linear,1/2.4f)-.055f)),0,255);
            }
        }
        if(forceOpaque||tint!=null||corrected!=null||additiveRgb)
            for(int i=0;i<pixels.Length;i+=4)
            {
                if(forceOpaque)pixels[i+3]=255;
                if(corrected!=null)
                    for(int component=0;component<3;component++)pixels[i+component]=corrected[pixels[i+component]];
                if(tint is {} c)
                {
                    pixels[i]=(byte)(pixels[i]*c.B/255);
                    pixels[i+1]=(byte)(pixels[i+1]*c.G/255);
                    pixels[i+2]=(byte)(pixels[i+2]*c.R/255);
                }
                // GlowAdd samples RGB only. Carry its energy through WPF's alpha-premultiplied brush.
                if(additiveRgb)
                {
                    int peak=Math.Max(pixels[i],Math.Max(pixels[i+1],pixels[i+2]));
                    pixels[i+3]=(byte)peak;
                    if(peak>0)for(int component=0;component<3;component++)pixels[i+component]=(byte)(pixels[i+component]*255/peak);
                }
            }
        var bitmap=BitmapSource.Create(decoded.Width,decoded.Height,96,96,PixelFormats.Bgra32,null,pixels,decoded.Width*4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte ToByte(float value)=>(byte)Math.Clamp(float.IsFinite(value)?MathF.Round(value*255):0,0,255);

    private static void ApplyMaterialColour(byte[] pixels,bool srgb,IReadOnlyList<MaterialColourProgram>? programs)
    {
        if(srgb&&programs is not {Count:>0})return;
        float[] linear=Enumerable.Range(0,256).Select(i=>srgb?MaterialGraph.DecodeSrgb(i/255f):i/255f).ToArray();
        byte Convert(float value)=>ToByte(MaterialGraph.EncodeSrgb(Math.Clamp(value,0,1)));
        Vector4 Evaluate(Vector4 colour)
        {
            if(programs!=null)foreach(var program in programs)colour=program.Evaluate(colour);
            return colour;
        }
        bool independent=programs==null||programs.All(program=>Enumerable.Range(0,4).All(i=>(program.Inputs[i]&~(1<<i))==0));
        if(independent)
        {
            var lookup=new byte[256*4];
            for(int i=0;i<256;i++)
            {
                var value=Evaluate(new(linear[i],linear[i],linear[i],i/255f));
                lookup[i*4]=Convert(value.Z);lookup[i*4+1]=Convert(value.Y);lookup[i*4+2]=Convert(value.X);lookup[i*4+3]=ToByte(value.W);
            }
            for(int i=0;i<pixels.Length;i+=4)
                for(int channel=0;channel<4;channel++)pixels[i+channel]=lookup[pixels[i+channel]*4+channel];
            return;
        }
        for(int i=0;i<pixels.Length;i+=4)
        {
            var value=Evaluate(new(linear[pixels[i+2]],linear[pixels[i+1]],linear[pixels[i]],pixels[i+3]/255f));
            pixels[i]=Convert(value.Z);pixels[i+1]=Convert(value.Y);pixels[i+2]=Convert(value.X);pixels[i+3]=ToByte(value.W);
        }
    }

    private static int Bone(AnimationSkeleton rig,string name)=>Array.FindIndex(rig.Bones,b=>b.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
    private static IEnumerable<string> EffectPacks(CosmeticItem item)=>item.PrimaryResources
        .Where(p=>p.EndsWith(".epk",StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4);

    private sealed record PackLink(string Anchor,string Path,bool Multiple=false,string? Alias=null);
    private static IEnumerable<PackLink> ObjectChildren(string ao) =>
        Regex.Matches(ao,@"(?im)^[ \t]*attached_object\s*=\s*""(\S+)\s+([^""\r\n]+?\.aoc?)(?:[ \t]+alias[ \t]+([^""\s]+))?""")
            .Select(m=>new PackLink(m.Groups[1].Value,m.Groups[2].Value,Alias:m.Groups[3].Success?m.Groups[3].Value:null))
            .Distinct().Take(8);

    private static IEnumerable<string> ObjectEffectPacks(string ao)
    {
        // Match balanced lifecycle handlers; ignore calls that switch tiers during combat or target another AO.
        var handlers=Regex.Matches(ao,@"(?im)^[ \t]*on_(?:game_object_set|attachment_chain_complete|construction)\s*=\s*\{(?<body>(?>(?:[^{}]+|\{(?<nested>)|\}(?<-nested>)))*)(?(nested)(?!))\}");
        return handlers.SelectMany(handler=>Regex.Matches(handler.Groups["body"].Value,
                @"(?i)\bthis\.AddEffectPack\s*\(\s*(?<quote>['""])(?<path>[^'""\r\n]+\.epk)\k<quote>\s*\)"))
            .Select(call=>call.Groups["path"].Value).Take(16);
    }
    private sealed record Pack(IReadOnlyList<PackLink> Particles,IReadOnlyList<PackLink> Attached,IReadOnlyList<PackLink> Trails,
        IReadOnlyList<string> Materials,bool HasUnsupportedAttachments)
    {
        public IReadOnlyList<PackPass> Passes { get; init; } = [];
    }
    /// <summary>An EPK render pass: Append adds the material's graphs to the host's material; Main is the host's own draw.</summary>
    private sealed record PackPass(string Material,bool Append,bool Main);

    private IEnumerable<PreviewAnchor> AttachmentAnchors(string? meshPath,string ao,AnimationSkeleton rig,PackLink child) =>
        ResolveAnchors(meshPath,ao,rig,child.Anchor is "*" or "<lock>"?"root":child.Anchor).Where(anchor=>anchor.Bone>=0)
            .Distinct().Take(child.Multiple?8:1);
    private Pack ReadPack(string path)
    {
        string source=Text(path);
        PackLink[] Links(string command,string extension,int limit)=>Regex.Matches(source,
            "(?im)^[ \\t]*"+command+"[ \\t]+\"([^\"\\r\\n]+)\"[ \\t]+\"([^\"\\r\\n]+\\."+extension+")\"")
            .Take(limit).Select(m=>new PackLink(m.Groups[1].Value,m.Groups[2].Value)).ToArray();
        var attached=Links("AttachedObject","aoc?",8).ToList();
        bool unsupported=false;
        foreach(Match line in Regex.Matches(source,@"(?im)^[ \t]*AttachedObjectEx[ \t]+""([^""\r\n]+)""[ \t]+([\d.]+)[ \t]+""([^""\r\n]+\.aoc?)""([^\r\n]*)").Take(8))
        {
            string[] flags=line.Groups[4].Value.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if(line.Groups[2].Value!="1"||flags.Length<6||!flags.Take(6).SequenceEqual(new[]{"1","1","1","1","0","0"}))
            {unsupported=true;continue;}
            attached.Add(new(line.Groups[1].Value,line.Groups[3].Value,flags.Contains("\"multi_attach\"",StringComparer.OrdinalIgnoreCase)));
        }
        var materials=new List<string>();
        var packPasses=new List<PackPass>();
        var passes=Regex.Match(source,@"(?ims)^[ \t]*RenderPasses\s*(\{\s*""passes""[ \t]*:\s*\[.*?\]\s*\})");
        if(passes.Success)
        {
            using var json=JsonDocument.Parse(passes.Groups[1].Value);
            foreach(var pass in json.RootElement.GetProperty("passes").EnumerateArray().Take(8))
                if(pass.TryGetProperty("filename",out var field)&&field.GetString() is {} file&&Path.GetExtension(file).Equals(".mat",StringComparison.OrdinalIgnoreCase))
                {
                    materials.Add(file);
                    bool append=pass.TryGetProperty("type",out var type)&&type.ValueKind==JsonValueKind.String&&type.ValueEquals("Append");
                    bool main=pass.TryGetProperty("is_main",out var isMain)&&isMain.ValueKind==JsonValueKind.True;
                    packPasses.Add(new(file,append,main));
                }
        }
        return new(Links("ParticleEffect","pet",16),attached,Links("TrailEffect","trl",8),materials,unsupported){Passes=packPasses};
    }

    /// <summary>
    /// Render passes of an effect pack, including the packs its controller objects add with
    /// parent_ao.AddEffectPack(...) (e.g. Pyromaniac: controller EPK → others_controller.ao → cult_others.epk).
    /// </summary>
    private IReadOnlyList<PackPass> EffectPasses(string epk,Pack pack,int depth=0)
    {
        var result=new List<PackPass>();
        result.AddRange(pack.Passes);
        if(Environment.GetEnvironmentVariable("PMC_DEBUG_PASSES")=="1")
            Console.WriteLine($"[passes] {epk}: {pack.Passes.Count} passes, attached {string.Join(", ",pack.Attached.Select(a=>a.Path))}");
        if(depth>=2)return result;
        foreach(var attached in pack.Attached)
        {
            if(!Source.Exists(attached.Path))continue;
            foreach(Match added in Regex.Matches(Text(attached.Path),@"AddEffectPack\(\s*'([^'\r\n]+\.epk)'"))
            {
                string path=added.Groups[1].Value;
                try
                {
                    foreach(var pass in EffectPasses(path,ReadPack(path),depth+1))
                        if(!result.Contains(pass))result.Add(pass);
                }
                catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException)
                { _materialIssues.Add(Path.GetFileName(path)+": "+ex.Message); }
            }
        }
        return result;
    }

    /// <summary>
    /// An opaque extra pass is drawn only when it changes the vertices: an inflated outline shell kept behind the mesh
    /// (wp_outline_2pass). An unchanged opaque copy would just cover the mesh. PMC_NO_VERTEX_EFFECTS=1 skips them.
    /// </summary>
    private static bool KeepsOpaquePass(PreviewGameShader shader)=>
        shader.Vertex.NormalOffset!=0&&Environment.GetEnvironmentVariable("PMC_NO_VERTEX_EFFECTS")!="1";

    /// <summary>
    /// EPK render passes of type Append (weapon and character effects): the main pass's graphs join each shape's own
    /// material in one shader, other passes draw the shape again with its material plus their graphs.
    /// </summary>
    private PreviewMesh ApplyAppendPasses(PreviewMesh mesh,IReadOnlyList<PackPass> passes)
    {
        var main=passes.Where(p=>p.Append&&p.Main).Select(p=>p.Material).ToArray();
        var extra=passes.Where(p=>p.Append&&!p.Main).Select(p=>p.Material).ToArray();
        if(main.Length==0&&extra.Length==0)return mesh;
        var cache=new Dictionary<string,PreviewSurface>(StringComparer.OrdinalIgnoreCase);
        var surfaces=mesh.Surfaces.Select(surface=>
        {
            if(surface.MaterialPath is not {Length:>0} host||surface.GameShader==null)return surface;
            if(cache.TryGetValue(host,out var done))return done;
            var result=surface;
            if(main.Length>0&&LoadGameShader(host,false,null,main) is var (shader,textures))
                result=result with{GameShader=shader,GameTextures=textures};
            var extraPasses=new List<PreviewRenderPass>(result.ExtraPasses);
            foreach(var pass in extra)
            {
                var built=LoadGameShader(host,false,null,[pass]);
                if(Environment.GetEnvironmentVariable("PMC_DEBUG_PASSES")=="1")
                    Console.WriteLine($"[passes] {Path.GetFileName(host)} + {Path.GetFileName(pass)}: {(built is var (s,_)?"blend "+s.BlendState+" vertex "+s.Vertex.NormalOffset:"failed")}");
                if(built is var (passShader,passTextures)&&(passShader.BlendState!=0||KeepsOpaquePass(passShader)))
                    extraPasses.Add(new(pass,passShader,passTextures));
            }
            if(Environment.GetEnvironmentVariable("PMC_DEBUG_PASSES")=="1")
                Console.WriteLine($"[passes] {Path.GetFileName(host)} main +{main.Length}: {(ReferenceEquals(result.GameShader,surface.GameShader)?"unchanged":"composed")} blend {result.GameShader?.BlendState}");
            return cache[host]=result with{ExtraPasses=extraPasses.ToArray()};
        }).ToArray();
        return mesh with{Surfaces=surfaces};
    }

    private static PreviewMesh ApplyCoating(PreviewMesh mesh,PreviewCoating coating)=>mesh with
    {
        Surfaces=mesh.Surfaces.Select(surface=>surface.Opacity is PreviewOpacity.Opaque or PreviewOpacity.Cutout
            ?surface with { Coating=MergeCoating(surface.Coating,coating) }:surface).ToArray()
    };

    private static PreviewCoating MergeCoating(PreviewCoating? previous,PreviewCoating next) => previous==null?next:new(
        previous.Brightness*next.Brightness,previous.EdgeColour+next.EdgeColour,
        next.EdgeColour.LengthSquared()>0?next.EdgePower:previous.EdgePower,next.Mask??previous.Mask,
        next.Metal??previous.Metal,next.Normal??previous.Normal);

    private PreviewCoating? ReadCoating(IReadOnlyList<string> paths)
    {
        PreviewCoating coating=new();
        foreach(var path in paths)
        {
            using var json=JsonDocument.Parse(Text(path));
            if(!json.RootElement.TryGetProperty("graphinstances",out var graphs))continue;
            foreach(var graph in graphs.EnumerateArray())
            {
                if(!graph.TryGetProperty("parent",out var parent)||!graph.TryGetProperty("custom_parameters",out var parameters))continue;
                string? type=Path.GetFileNameWithoutExtension(parent.GetString());
                if(type is not ("Goldified" or "GlowAlpha" or "GlowEdge" or "HighlightEdges" or "DarkenTex" or "MultiplyTexAlpha"))continue;
                var values=parameters.EnumerateArray().Where(p=>p.TryGetProperty("name",out _)&&p.TryGetProperty("parameters",out var a)&&a.GetArrayLength()>0)
                    .GroupBy(p=>p.GetProperty("name").GetString()??"",StringComparer.Ordinal)
                    .ToDictionary(group=>group.Key,group=>group.Last().GetProperty("parameters")[0],StringComparer.Ordinal);
                JsonElement Value(string key)=>values.TryGetValue(key,out var cell)&&cell.TryGetProperty("value",out var value)?value:default;
                float Number(string key,float fallback)=>Value(key).ValueKind==JsonValueKind.Number?Value(key).GetSingle():fallback;
                Vector3 Rgb(string key)
                {
                    var array=Value(key);
                    return array.ValueKind==JsonValueKind.Array&&array.GetArrayLength()>=3?new(array[0].GetSingle(),array[1].GetSingle(),array[2].GetSingle()):Vector3.Zero;
                }
                ImageSource? ReadTexture(string key)
                {
                    if(!values.TryGetValue(key,out var cell)||!cell.TryGetProperty("path",out var file)||file.GetString() is not {} resource)return null;
                    try { return Texture(resource,false,null,1,null,false); }
                    catch(Exception ex)when(ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
                    {_materialIssues.Add(Path.GetFileName(resource)+": "+ex.Message);return null;}
                }

                coating=type switch
                {
                    "Goldified"=>coating with { Metal=ReadTexture("7. Spec_Tex"),Normal=ReadTexture("3. NormalAoGloss") },
                    "GlowAlpha"=>coating with { EdgeColour=coating.EdgeColour+Rgb("Glow_RGB")*Math.Clamp(Number("Glow_Intensity",1),0,12),EdgePower=Math.Clamp(Number("Angle",1),.15f,12) },
                    "GlowEdge"=>coating with { EdgeColour=coating.EdgeColour+Rgb("Fresnel_colour"),EdgePower=Math.Clamp(Number("Fresnel_falloff",1),.15f,12) },
                    "HighlightEdges"=>coating with { EdgeColour=coating.EdgeColour+Rgb("rgb_angle") },
                    "DarkenTex"=>coating with { Brightness=coating.Brightness*Math.Clamp(Number("scale",1),.01f,4) },
                    "MultiplyTexAlpha"=>coating with { Mask=ReadTexture("Mask_tex")??coating.Mask },
                    _=>coating
                };
            }
        }
        return coating==new PreviewCoating()?null:coating;
    }

    private string ObjectText(string path)
    {
        var inherited=new List<string>();
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while(paths.Count<12&&paths.Add(path)&&Source.Exists(path))
        {
            string text=Text(path);
            inherited.Add(text);
            var parent=Regex.Match(text,@"(?im)^[ \t]*extends[ \t]+""([^""\r\n]+)""");
            if(!parent.Success||parent.Groups[1].Value=="nothing")break;
            path=parent.Groups[1].Value;
            if(!path.EndsWith(".ao",StringComparison.OrdinalIgnoreCase))path+=".ao";
        }
        return string.Join("\n",inherited);
    }

    private IReadOnlyList<PreviewEmitter> LoadAttachmentEmitters(string path,string? meshPath,AnimationSkeleton? rig,string? selectedAnimation=null,
        bool characterAnimations=false)
    {
        if(rig==null||Path.GetExtension(path).Equals(".sm",StringComparison.OrdinalIgnoreCase))return [];
        var result=new List<PreviewEmitter>();
        try
        {
            string ao=ObjectText(path);
            foreach(Match fx in Regex.Matches(ao,"(?m)^\\s*continuous_effect\\s*=\\s*\"(\\S+)\\s+([^\"]+\\.pet)\"").Take(14))
                result.AddRange(EmitAt(meshPath,ao,rig,fx.Groups[1].Value,fx.Groups[2].Value,12));
            if(characterAnimations||selectedAnimation!=null)
            {
                var seen=new HashSet<(string Animation,float Time,string Group,string File)>();
                foreach(Match animationList in Regex.Matches(ao,@"(?s)\banimations\s*=\s*'(\[.*?\])'").Take(12))
                {
                    using var json=JsonDocument.Parse(animationList.Groups[1].Value);
                    foreach(var clip in json.RootElement.EnumerateArray())
                    {
                        if(!clip.TryGetProperty("name",out var name)||name.GetString() is not {} animation
                            ||(!characterAnimations&&!animation.Equals(selectedAnimation,StringComparison.OrdinalIgnoreCase)&&animation!="anim")
                            ||!clip.TryGetProperty("events",out var events))continue;
                        foreach(var e in events.EnumerateArray())
                        {
                            if(seen.Count>=48||result.Count>=96)break;
                            if(!e.TryGetProperty("type",out var type)||type.GetString()!="ParticleEffectEventType"
                                ||!e.TryGetProperty("time",out var when)||!when.TryGetSingle(out float time)||!float.IsFinite(time)||time<0
                                ||!e.TryGetProperty("bone_group",out var bone)||bone.GetString() is not {} group
                                ||!e.TryGetProperty("filename",out var file)||file.GetString() is not {} particle
                                ||!seen.Add((animation,time,group,particle)))continue;
                            result.AddRange(EmitAt(meshPath,ao,rig,group,particle,Math.Min(12,96-result.Count)).Select(emitter=>emitter with
                            { Animation=characterAnimations?animation:null, StartsAt=time, UsesCharacterAnimation=characterAnimations }));
                        }
                    }
                }
            }
        }
        catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
        { _materialIssues.Add(Path.GetFileName(path)+": not all particles were read."); }
        return result;
    }

    private IReadOnlyList<PreviewTrail> LoadAttachmentTrails(string path,string? meshPath,AnimationSkeleton? rig,string? animation=null)
    {
        if(rig==null||Path.GetExtension(path).Equals(".sm",StringComparison.OrdinalIgnoreCase))return [];
        var result=new List<PreviewTrail>();
        try
        {
            string ao=ObjectText(path);
            foreach(Match trail in Regex.Matches(ao,@"(?im)^[ \t]*continuous_effect[ \t]*=[ \t]*""(\S+)[ \t]+([^""\r\n]+\.trl)""").Take(8))
                result.AddRange(ReadTrails(trail.Groups[2].Value,ResolveAnchors(meshPath,ao,rig,trail.Groups[1].Value)));
            if(animation!=null)
            {
                var seen=new HashSet<(float Time,string Group,string File)>();
                foreach(Match list in Regex.Matches(ao,@"(?s)\banimations\s*=\s*'(\[.*?\])'").Take(12))
                {
                    using var json=JsonDocument.Parse(list.Groups[1].Value);
                    foreach(var clip in json.RootElement.EnumerateArray())
                    {
                        if(!clip.TryGetProperty("name",out var name)||!animation.Equals(name.GetString(),StringComparison.OrdinalIgnoreCase)
                            ||!clip.TryGetProperty("events",out var events))continue;
                        foreach(var e in events.EnumerateArray())
                        {
                            if(seen.Count>=16)break;
                            if(!e.TryGetProperty("type",out var type)||type.GetString()!="TrailsEffectEventType"
                                ||!e.TryGetProperty("time",out var when)||!when.TryGetSingle(out var time)||!float.IsFinite(time)||time<0
                                ||!e.TryGetProperty("bone_group",out var bone)||bone.GetString() is not {} group
                                ||!e.TryGetProperty("filename",out var file)||file.GetString() is not {} trailPath
                                ||!seen.Add((time,group,trailPath)))continue;
                            result.AddRange(ReadTrails(trailPath,ResolveAnchors(meshPath,ao,rig,group))
                                .Select(trail=>trail with{StartsAt=time,UsesLocalAnimation=true}));
                        }
                    }
                }
            }
        }
        catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException or NotSupportedException or InvalidOperationException)
        { _materialIssues.Add(Path.GetFileName(path)+": not all trails were read."); }
        return result;
    }

    private IReadOnlyList<PreviewTrail> ReadTrails(string path,IReadOnlyList<PreviewAnchor> anchors)
    {
        if(anchors.Count==0)return [];
        var result=new List<PreviewTrail>();
        foreach(Match block in Regex.Matches(Text(path),@"(?is)\{([^{}]*?\bmaterial[ \t]+""([^""\r\n]+\.mat)""[^{}]*)\}").Take(4))
        {
            string fields=block.Groups[1].Value;
            string Field(string name)=>Regex.Match(fields,@"(?im)^[ \t]*"+name+@"[ \t]+([^\r\n]+)").Groups[1].Value.Trim();
            if(!float.TryParse(Field("segment_duration"),CultureInfo.InvariantCulture,out float duration)||!float.IsFinite(duration)||duration<=0)continue;
            var widthValues=Field("scale").Split(' ',StringSplitOptions.RemoveEmptyEntries);
            float width=widthValues.Length>=5&&float.TryParse(widthValues[3],CultureInfo.InvariantCulture,out float initialWidth)
                &&float.IsFinite(initialWidth)?initialWidth:10;
            float distance=float.TryParse(Field("start_distance_threshold"),CultureInfo.InvariantCulture,out float threshold)
                &&float.IsFinite(threshold)?threshold:4;
            var material=LoadMaterial(block.Groups[2].Value,true);
            var game=LoadGameShader(block.Groups[2].Value,true,"Additive");
            var texture=material.Glow??material.Colour;
            if(texture==null&&game==null)continue;
            var definition=TrlTrail.Parse("{"+block.Groups[1].Value+"}").FirstOrDefault();
            var edge=anchors.Count>1&&Vector3.DistanceSquared(anchors[0].Position,anchors[^1].Position)>1?anchors[^1]:(PreviewAnchor?)null;
            result.Add(new(texture??White,anchors[0],Math.Clamp(width,1,60),Math.Clamp(duration,.1f,1.2f),Math.Clamp(distance,.3f,24))
            { Definition=definition,EdgeAnchor=edge,GameShader=game?.Shader,GameTextures=game?.Textures??[] });
        }
        return result;
    }

    private IEnumerable<PreviewEmitter> EmitAt(string? meshPath,string? ao,AnimationSkeleton rig,string anchor,string particle,int limit)
    {
        var points=ResolveAnchors(meshPath,ao,rig,anchor);
        if(points.Count==0)return [];
        var first=points[0];var last=points[^1];
        return ReadEmitters(particle,first.Position,last.Position,limit)
            .Select(emitter=>emitter with { StartBone=first.Bone,EndBone=last.Bone,Anchors=points });
    }

    private IReadOnlyList<PreviewAnchor> ResolveAnchors(string? meshPath,string? ao,AnimationSkeleton rig,string groups)
    {
        var points=new List<PreviewAnchor>();
        var names=new List<string>();
        foreach(string anchor in groups.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
        {
            string quote=Regex.Escape(anchor);
            string groupPattern=quote.Replace("\\*","[^\" \\r\\n]*");
            var group=meshPath==null?null:Regex.Match(Text(meshPath),"(?im)^[ \\t]*\""+groupPattern+"\"[ \\t]+\\d+([^\\r\\n]*)");
            if(group?.Success==true)names.AddRange(Regex.Matches(group.Groups[1].Value,"\"([^\"]+)\"").Select(m=>m.Groups[1].Value));
            if(ao!=null)
            {
                foreach(Match aoGroup in Regex.Matches(ao,"(?im)^[ \\t]*bone_group[ \\t]*=[ \\t]*\""+groupPattern+"[ \\t]+(?:true|false)[ \\t]+([^\"\\r\\n]*)\"").Take(8))
                    names.AddRange(aoGroup.Groups[1].Value.Split(' ',StringSplitOptions.RemoveEmptyEntries));
            }
            if(anchor=="*")names.Add("root");
            else if(anchor.Contains('*'))names.AddRange(rig.Bones.Select(b=>b.Name).Where(n=>Regex.IsMatch(n,"^"+groupPattern+"$",RegexOptions.IgnoreCase)).Take(24));
            else names.Add(anchor);
        }
        foreach(var name in names.Distinct(StringComparer.OrdinalIgnoreCase).Take(64))
        {
            int bone=Bone(rig,name);
            if(bone>=0){points.Add(new(rig.BindWorld[bone].Translation,bone));continue;}
            if(ao==null)continue;
            if(ResolveSocket(rig,Sockets(ao),name) is {} socket)points.Add(socket);
        }
        return points;
    }

    private static readonly ImageSource White=CreateWhite();
    private static ImageSource CreateWhite()
    {
        var image=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{255,255,255,255},4);
        image.Freeze();
        return image;
    }

    private FixedParticleMesh? LoadParticleMesh(string path)
    {
        if(_particleMeshes.TryGetValue(path,out var cached))return cached;
        try { return _particleMeshes[path]=FixedParticleMesh.Read(Source.Read(path)); }
        catch(Exception ex)when(ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            _materialIssues.Add(Path.GetFileName(path)+": "+ex.Message);
            return _particleMeshes[path]=null;
        }
    }

    private IReadOnlyList<PreviewEmitter> ReadEmitters(string path,Vector3 start,Vector3 end,int limit)
    {
        var result=new List<PreviewEmitter>();
        string text=Text(path);
        foreach(var definition in PetFile.Parse(text))
        {
            if(result.Count==limit)break;
            string materialPath=definition.Material;
            string? blend=MaterialShaderBuilder.NormaliseBlend(definition.Blend);
            float rate=definition.ParticlesPerSecond.Max();
            float size=definition.Scale.Max();
            if(rate<=0||size<=0||!materialPath.EndsWith(".mat",StringComparison.OrdinalIgnoreCase))continue;
            Color? tint=null;
            var colours=definition.Colour.Values.Where(value=>value.Length==4).ToArray();
            var colour=colours.OrderByDescending(k=>k[3]).FirstOrDefault();
            if(colour!=null)
            {
                static byte Byte(float value)=>(byte)(Math.Clamp(value,0,1)*255);
                tint=Color.FromRgb(Byte(colour[0]),Byte(colour[1]),Byte(colour[2]));
            }
            var material=LoadMaterial(materialPath,true,tint);
            var particleMesh=definition.Mesh is {} meshPath?LoadParticleMesh(meshPath):null;
            var game=definition.Mesh==null||particleMesh!=null?LoadGameShader(materialPath,true,definition.Blend):null;
            var image=material.Glow??material.Colour;
            if(image==null&&game==null)continue;
            image??=White;
            bool coverage=(blend is "AlphaTest" or "AlphaBlend")&&material.Opacity!=PreviewOpacity.Additive;
            bool multiply=blend=="MultiplicitiveBlend"||material.Opacity==PreviewOpacity.Multiply;
            bool softGlow=!coverage&&!multiply&&Regex.IsMatch(Path.GetFileNameWithoutExtension(materialPath),@"glare|gradient|glow|bloom|flare",RegexOptions.IgnoreCase);
            float sizeLimit=material.Unlit?44:55;
            int frames=definition.FramesX;
            int side=(int)MathF.Sqrt(frames);
            result.Add(new(image,start,end,Math.Clamp(size,1,sizeLimit),Math.Clamp(definition.ParticleDuration,.1f,3f),
                Math.Clamp(rate,1,36),Math.Clamp(definition.VelocityAlong.Evaluate(0),-110,110))
            {
                Blend=multiply?PreviewOpacity.Multiply:coverage?PreviewOpacity.Transparent:PreviewOpacity.Additive,
                Strength=multiply||coverage ? 1 : softGlow ? .65f : 1.55f,
                SizeOverLife=definition.Scale.Values.Select(value=>Math.Clamp(value[0]/size,0,1)).ToArray(),
                AlphaOverLife=colours.Select(c=>Math.Clamp(c[3],0,1)).ToArray(),
                OpacityOverLife=definition.Opacity.Values.Select(value=>value[0]).ToArray(),
                StretchOverLife=definition.Stretch.Values.Select(value=>value[0]).ToArray(),SoftGlow=softGlow,
                FaceGround=definition.FaceType is "FaceGround" or "FaceFloor" or "FaceUp" or "XYLock",
                UvTiling=material.UvTiling,UvMovement=material.UvMovement,SoftDepth=material.SoftDepth,
                AtlasSide=side*side==frames?side:1,AtlasSpeed=Math.Clamp(definition.AnimSpeed,.01f,60),AtlasOnce=definition.AnimPlayOnce,
                Definition=definition,ParticleMesh=particleMesh,GameShader=game?.Shader,GameTextures=game?.Textures??[]
            });
        }
        if(result.Count<limit)
        {
            foreach(var gpu in GpuParticleFile.Parse(text))
            {
                bool persistent=gpu.UpdateMaterial is {} update&&Source.Exists(update)&&GpuMaterial(update).Instances
                    .Any(instance=>instance.Parent.EndsWith("/InfiniteParticles.fxgraph",StringComparison.OrdinalIgnoreCase));
                foreach(var pass in gpu.Passes)
                {
                    if(result.Count>=limit)return result;
                    if(!Source.Exists(pass.Material))continue;
                    var geometry=pass.Mesh is {} meshPath?LoadParticleMesh(meshPath):null;
                    if(pass.Mesh!=null&&geometry==null)continue;
                    var gpuMaterial=GpuMaterial(pass.Material);
                    if(geometry!=null)geometry=GpuParticleGeometry.Apply(geometry,gpuMaterial);
                    var material=LoadMaterial(pass.Material,true);
                    var game=LoadGameShader(pass.Material,true,null);
                    var texture=material.Glow??material.Colour;
                    if(texture==null&&game==null)
                    {
                        _materialIssues.Add(Path.GetFileName(pass.Material)+": the particle material is unavailable.");
                        continue;
                    }
                    bool ground=pass.Facing is "Z_Fixed";
                    float size=GpuParticleSize(gpuMaterial)??(geometry!=null?1:Math.Clamp(Vector3.Distance(start,end),6,30));
                    var definition=new PetEmitter
                    {
                        Kind=gpu.Locked||start==end?"PointEmitter":"LineEmitter",Material=pass.Material,Mesh=pass.Mesh,
                        FaceType=ground?"FaceGround":pass.Facing,RotationLock="Fixed",LockedToBone=gpu.Locked,
                        ParticleDuration=gpu.ParticleDuration,ParticleDurationVariance=gpu.DurationVariance,
                        EmitterDuration=gpu.EmitterDuration,Infinite=gpu.Continuous,PersistentParticles=persistent,
                        InitialBurst=gpu.Burst,Scale=EffectCurve.Constant(size),ParticlesPerSecond=EffectCurve.Constant(gpu.Rate)
                    };
                    result.Add(new(texture??White,start,end,size,gpu.ParticleDuration,Math.Max(gpu.Rate,gpu.Burst/gpu.ParticleDuration),0)
                    {
                        Definition=definition,ParticleMesh=geometry,GameShader=game?.Shader,GameTextures=game?.Textures??[],
                        GpuEmitterSeed=PreviewSeed.Create(path,gpu.Index),FaceGround=ground,
                        UvTiling=material.UvTiling,UvMovement=material.UvMovement,SoftDepth=material.SoftDepth,
                        Blend=material.Opacity is PreviewOpacity.Transparent or PreviewOpacity.Multiply?material.Opacity:PreviewOpacity.Additive
                    });
                }
            }
        }
        return result;
    }

    private FxMaterial GpuMaterial(string path)
    {
        try { return FxMaterial.Parse(path,Text(path)); }
        catch(Exception ex) when(ex is IOException or JsonException or InvalidDataException or InvalidOperationException)
        {
            _materialIssues.Add(Path.GetFileName(path)+": could not read the particle settings.");
            return new(){Path=path};
        }
    }

    private static float? GpuParticleSize(FxMaterial material)
    {
        foreach(var graph in material.Instances)
        {
            if(!graph.Parent.EndsWith("/Scale.fxgraph",StringComparison.OrdinalIgnoreCase)||
                !graph.CustomParameters.TryGetValue("Scale",out var values)||values.ValueKind!=JsonValueKind.Array||values.GetArrayLength()==0||
                values[0].ValueKind!=JsonValueKind.Object||!values[0].TryGetProperty("value",out var value))continue;
            if(value.ValueKind==JsonValueKind.Array&&value.GetArrayLength()>0)value=value[0];
            if(value.ValueKind==JsonValueKind.Number&&value.TryGetSingle(out float scale)&&float.IsFinite(scale)&&scale>0)
                return Math.Clamp(scale,.01f,1000);
        }
        return null;
    }
}
