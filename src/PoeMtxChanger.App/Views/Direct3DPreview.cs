using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Microsoft.Win32.SafeHandles;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.Core.Formats.Geometry;
using PoeMtxChanger.Core.Rendering;

namespace PoeMtxChanger.App.Views;

internal sealed class Direct3DPreview : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Vertex(Vector3 Position,Vector3 Normal,Vector2 UV,Vector4 Colour=default);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Frame(Matrix4x4 ViewProjection,Vector4 Eye,Vector4 Settings,
        Vector4 Position0,Vector4 Position1,Vector4 Position2,Vector4 Color0,Vector4 Color1,Vector4 Color2,Vector4 Depth,Vector4 PostProcess);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Draw(uint First,uint Count,uint Base,uint Glow,uint Normal,uint Reflectance,uint Blend,
        Vector4 Maps,Vector4 Tint,Vector4 Motion,Vector4 Edge=default,Vector4 Modulation=default,Vector4 UvTransform=default,uint Mask=0,
        uint Material=0,MaterialSlots Slots=default);
    /// <summary>Texture ids for t0…t15 of a game material shader, then the normal offset as float bits and two words of
    /// padding (native Draw is 208 bytes).</summary>
    [System.Runtime.CompilerServices.InlineArray(ModelPreviewLoader.GameShaderSlots+3)]
    private struct MaterialSlots { private uint _first; }
    private sealed record Mesh(PreviewMesh Source,int VertexOffset,Vector3[]? ClothNormals,EffectLight[] Lights,BoneChainPhysics? Physics);
    /// <summary>
    /// World velocity of the wearer while a run clip plays: the character runs in place, but its cloth should move
    /// as if it went forward (asset space, the character faces −Y; about a run's pace in game units per second).
    /// </summary>
    private static readonly Vector3 RunVelocity=new(0,-450,0);
    /// <summary>Simulate cloth on phys_* bones; off shows the items' animated pose (the wardrobe's "Cloth physics").</summary>
    internal bool BonePhysics { get; set; }=true;
    private sealed record EffectLight(SkeletonLight Source,int Bone);
    private sealed record Particle(PreviewMesh Mesh,PreviewEmitter Emitter,uint Texture,int VertexOffset,int IndexOffset,float Phase,float Along,int Light,float Coverage);
    private sealed record Trail(PreviewMesh Mesh,PreviewTrail Source,uint Texture,int VertexOffset,int IndexOffset)
    {
        public List<(Vector3 Position,float Time)> Samples { get; }=[];
    }
    private const int TrailPoints=16;
    /// <summary>Emitter simulated from its .pet definition and drawn with the game's particle material.</summary>
    private sealed record GameEmitter(PreviewMesh Mesh,PreviewEmitter Source,ParticleSimulator Simulator,Draw Template,int VertexOffset,int IndexOffset,int Capacity);
    /// <summary>Trail simulated from its .trl definition and drawn with the game's material.</summary>
    private sealed record GameTrail(PreviewMesh Mesh,PreviewTrail Source,TrailSimulator Simulator,Draw Template,int VertexOffset,int IndexOffset);
    private readonly List<GameEmitter> _gameEmitters=[];
    private readonly List<GameTrail> _gameTrails=[];
    private readonly List<ParticleSprite> _sprites=[];
    private readonly List<TrailPoint> _ribbon=[];
    private readonly List<float> _effectLights=[];

    private readonly Renderer _renderer;
    private readonly Dictionary<(BitmapSource,bool),uint> _textures=[];
    private readonly Dictionary<byte[],uint> _materials=new(ReferenceEqualityComparer.Instance);
    private readonly List<Mesh> _meshes=[];
    private readonly List<Particle> _particles=[];
    private readonly List<Trail> _trails=[];
    private readonly List<Vector3> _lightColours=[];
    private Vertex[] _vertices=[];
    private int[] _indices=[];
    private Draw[] _draws=[];
    private int _shapeCount;
    private int _shadowVertex,_floorVertex;
    private float? _floorY;
    private byte[] _pixels=[];
    private WriteableBitmap? _bitmap;
    /// <summary>Time of the last frame's native draw (GPU work and readback) and of the copy into the WPF bitmap.</summary>
    internal double LastNativeMs { get; private set; }
    internal double LastCopyMs { get; private set; }
    internal bool IsSoftware { get; }

    internal Direct3DPreview(bool forceSoftware=false)
    {
        using var shader=typeof(Direct3DPreview).Assembly.GetManifestResourceStream("PoeMtxChanger.Preview.hlsl")!;
        byte[] source=new byte[checked((int)shader.Length)];
        shader.ReadExactly(source);
        byte[] error=new byte[4096];
        _renderer=Native.preview_create(source,(uint)source.Length,error,(uint)error.Length,forceSoftware?1:0);
        if(_renderer.IsInvalid)
        {
            _renderer.Dispose();
            int length=Array.IndexOf(error,(byte)0);
            throw new InvalidOperationException(System.Text.Encoding.UTF8.GetString(error,0,length<0?error.Length:length));
        }
        IsSoftware=Native.preview_is_software(_renderer)!=0;
    }

    internal void SetScene(PreviewScene scene)
    {
        Native.preview_clear_textures(_renderer);
        _textures.Clear();_materials.Clear();_meshes.Clear();_particles.Clear();_trails.Clear();_lightColours.Clear();
        _gameEmitters.Clear();_gameTrails.Clear();
        var indices=new List<int>();
        var draws=new List<Draw>();
        var fallback=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{116,124,128,255},4);
        uint fallbackId=Texture(fallback,true);
        int vertexCount=0;
        foreach(var mesh in scene.Meshes)
        {
            var lights=mesh.LocalRig?.Lights.Select(light=>new EffectLight(light,LightBone(mesh,light))).ToArray()??[];
            var physics=mesh.Physics is {} settings&&mesh.LocalRig is {} physicsRig?new BoneChainPhysics(physicsRig,settings):null;
            _meshes.Add(new(mesh,vertexCount,mesh.ClothFlex>0?new Vector3[mesh.Geometry.Vertices.Length]:null,lights,physics));
            var additionalPasses=mesh.RenderPasses.Select(pass=>GameDraw(pass.Shader,pass.Textures)).OfType<Draw>().ToArray();
            for(int i=0;i<mesh.Geometry.Shapes.Length;i++)
            {
                var shape=mesh.Geometry.Shapes[i];
                if(shape.TriangleCount==0||mesh.HiddenShapes.Contains(shape.Name))continue;
                int indexStart=indices.Count;
                foreach(int index in mesh.Geometry.Indices.AsSpan(shape.FirstTriangle*3,shape.TriangleCount*3))indices.Add(index+vertexCount);
                foreach(var pass in additionalPasses)
                    draws.Add(pass with{First=(uint)indexStart,Count=(uint)shape.TriangleCount*3});
                var surface=mesh.Surfaces.ElementAtOrDefault(i)??new PreviewSurface(mesh.Textures[i],mesh.Glow.ElementAtOrDefault(i));
                var coating=surface.Coating;
                uint normal=Texture(coating?.Normal??surface.Normal,false);
                bool coatedMetal=coating?.Metal!=null;
                uint reflectance=Texture(coating?.Metal??surface.ReflectanceMap,coatedMetal);
                uint albedo=Texture(surface.Colour,true);
                uint blend=surface.Opacity switch{PreviewOpacity.Transparent=>1,PreviewOpacity.Additive=>2,PreviewOpacity.Multiply=>3,_=>0};
                if(surface.GameShader is {} meshShader&&GameDraw(meshShader,surface.GameTextures) is {} game)
                {
                    draws.Add(game with{First=(uint)indexStart,Count=(uint)shape.TriangleCount*3});
                    foreach(var extra in surface.ExtraPasses)
                        if(GameDraw(extra.Shader,extra.Textures) is {} extraDraw)
                            draws.Add(extraDraw with{First=(uint)indexStart,Count=(uint)shape.TriangleCount*3});
                    continue;
                }
                draws.Add(new((uint)indexStart,(uint)shape.TriangleCount*3,albedo==0?fallbackId:albedo,Texture(surface.Glow,true),normal,reflectance,blend,
                    new(normal==0?0:surface.NormalGlossAo||coating?.Normal!=null?2:1,
                        reflectance==0?0:coatedMetal?(float)PreviewReflectance.MetallicCoating:(float)surface.Reflectance,
                        surface.UsesRoughness&&!coatedMetal?-2:-1,(float)surface.Opacity),
                    Vector4.One,new(surface.ScrollSpeed,surface.EmissionStrength,blend>=2?surface.RotationSpeed:surface.Unlit?0:1),
                    new(coating?.EdgeColour??Vector3.Zero,coating?.EdgePower??1),new(coating?.Brightness??1,0,1,blend!=0?surface.SoftDepth:0),
                    new(surface.UvTiling,surface.UvMovement.X,surface.UvMovement.Y),Texture(coating?.Mask,false)));
            }
            vertexCount+=mesh.Geometry.Vertices.Length;
        }
        // Opaque and cutout shapes write depth before translucent materials and particles.
        // Blend codes: 0 opaque, 1 alpha, 2 additive, 3 multiply; 4 additive, 5 premultiplied, 6 subtractive (game shaders).
        static uint Layer(Draw draw)=>draw.Blend switch{0=>0,3=>1,2 or 4 or 6=>2,_=>3};
        draws.Sort(static (a,b)=>Layer(a).CompareTo(Layer(b)));
        int shadowIndices=indices.Count;
        _shadowVertex=vertexCount;
        AddQuadIndices(indices,vertexCount);
        vertexCount+=4;
        draws.Insert(0,new((uint)shadowIndices,6,0,0,0,0,1,new(0,0,0,5),new(0,0,0,.77f),default));
        _floorY=scene.Meshes[0].MountBone<0&&scene.Skeleton!=null&&scene.Meshes[0].Geometry.Vertices.Length>0
            ? -scene.Meshes[0].Geometry.Vertices.Max(v=>v.Position.Z)-1f : null;
        if(_floorY.HasValue)
        {
            int floorIndices=indices.Count;
            _floorVertex=vertexCount;
            AddQuadIndices(indices,vertexCount);
            vertexCount+=4;
            draws.Insert(0,new((uint)floorIndices,6,0,0,0,0,1,new(0,0,0,6),Vector4.One,default));
        }
        _shapeCount=draws.Count;
        foreach(var mesh in scene.Meshes)
        {
            bool meshHasLight=false;
            foreach(var emitter in mesh.Emitters)
            {
                Draw? particleDraw=emitter.GameShader is {} particleShader?GameDraw(particleShader,emitter.GameTextures):null;
                if(particleDraw==null&&emitter.GpuEmitterSeed.HasValue)
                    particleDraw=GpuParticleDraw(emitter);
                if(emitter.Definition is {} definition&&particleDraw is {} template)
                {
                    // Sprite coverage (including the PET lifetime fade) scales additive RGB.
                    if(template.Blend==4)template=template with{Blend=2};
                    var simulator=new ParticleSimulator(definition,PreviewSeed.Create(mesh.Path,emitter.GpuEmitterSeed??_gameEmitters.Count));
                    var geometry=emitter.ParticleMesh;
                    int capacity=geometry==null?simulator.Capacity:Math.Min(simulator.Capacity,
                        Math.Min(65536/geometry.Vertices.Length,98304/geometry.Indices.Length));
                    _gameEmitters.Add(new(mesh,emitter,simulator,template,vertexCount,indices.Count,capacity));
                    for(int q=0;q<capacity;q++)
                    {
                        if(geometry==null){AddQuadIndices(indices,vertexCount);vertexCount+=4;}
                        else
                        {
                            foreach(int index in geometry.Indices)indices.Add(vertexCount+index);
                            vertexCount+=geometry.Vertices.Length;
                        }
                    }
                    continue;
                }
                uint texture=Texture(emitter.BrushTexture,true);
                if(texture==0)continue;
                int light=-1;
                if(_lightColours.Count<3&&!meshHasLight&&emitter.Blend==PreviewOpacity.Additive&&!emitter.SoftGlow&&SpriteColour(emitter.BrushTexture) is {} colour)
                {
                    light=_lightColours.Count;_lightColours.Add(colour);meshHasLight=true;
                }
                int count=ParticleMotion.Count(emitter);
                // Wide additive halos can overlap at most attachment sockets. Keep their accumulated energy bounded.
                float coverage=emitter.SoftGlow ? .25f/MathF.Sqrt(Math.Max(1,count/2f))
                    : emitter.Blend==PreviewOpacity.Additive ? .53f/MathF.Sqrt(Math.Max(1,count/6f)) : .66f;
                for(int i=0;i<count;i++)
                {
                    _particles.Add(new(mesh,emitter,texture,vertexCount,indices.Count,(float)i/count,((i*3+1)%count+.5f)/count,i==0?light:-1,coverage));
                    AddQuadIndices(indices,vertexCount);
                    vertexCount+=4;
                    draws.Add(default);
                }
            }
            foreach(var trail in mesh.Trails)
            {
                if(trail.Definition is {} trailDefinition&&trail.GameShader is {} trailShader&&GameDraw(trailShader,trail.GameTextures) is {} trailTemplate)
                {
                    _gameTrails.Add(new(mesh,trail,new TrailSimulator(trailDefinition,trail.Width),trailTemplate,vertexCount,indices.Count));
                    for(int j=0;j<TrailSimulator.MaxSegments;j++)
                    {
                        int start=vertexCount+j*2;
                        indices.AddRange([start,start+1,start+2,start+1,start+3,start+2]);
                    }
                    vertexCount+=(TrailSimulator.MaxSegments+1)*2;
                    continue;
                }
                uint texture=Texture(trail.Texture,true);
                if(texture==0)continue;
                _trails.Add(new(mesh,trail,texture,vertexCount,indices.Count));
                for(int i=0;i<TrailPoints-1;i++)
                {
                    int start=vertexCount+i*2;
                    indices.AddRange([start,start+1,start+2,start+1,start+3,start+2]);
                }
                vertexCount+=TrailPoints*2;
                draws.Add(default);
            }
        }
        _particles.Sort(static (a,b)=>(a.Emitter.Blend==PreviewOpacity.Multiply?0:1).CompareTo(b.Emitter.Blend==PreviewOpacity.Multiply?0:1));
        for(int i=0;i<_gameEmitters.Count+_gameTrails.Count;i++)draws.Add(default);
        _vertices=new Vertex[vertexCount];
        _indices=indices.ToArray();_draws=draws.ToArray();
    }

    internal WriteableBitmap Render(PreviewScene scene,Matrix4x4[]? bodyPose,float seconds,PerspectiveCamera camera,
        int width,int height,int lightingPreset,string? clip,float clipTime,Matrix4x4[]? trailingPose=null,float referenceExposure=0)
    {
        if(_bitmap==null||_bitmap.PixelWidth!=width||_bitmap.PixelHeight!=height)
        {
            _bitmap=new WriteableBitmap(width,height,96,96,PixelFormats.Bgra32,null);
            _pixels=new byte[checked(width*height*4)];
        }
        var eye=Vector(camera.Position);
        var forward=Vector3.Normalize(new((float)camera.LookDirection.X,(float)camera.LookDirection.Y,(float)camera.LookDirection.Z));
        var right=Vector3.Normalize(Vector3.Cross(forward,Vector3.UnitY));
        var up=Vector3.Cross(right,forward);
        float aspect=(float)width/height;
        float verticalFieldOfView=2*MathF.Atan(MathF.Tan((float)(camera.FieldOfView*Math.PI/360))/aspect);
        var view=Matrix4x4.CreateLookAt(eye,eye+forward,up);
        float distance=(float)camera.LookDirection.Length;
        float near=Math.Max(.05f,distance*.008f),far=Math.Max(1000,2*distance);
        var projection=Matrix4x4.CreatePerspectiveFieldOfView(verticalFieldOfView,aspect,near,far);
        Span<Vector4> lightPositions=stackalloc Vector4[3];
        Span<Vector4> lightColours=stackalloc Vector4[3];
        lightPositions.Clear();lightColours.Clear();
        bool running=clip?.StartsWith("run",StringComparison.OrdinalIgnoreCase)==true;

        _effectLights.Clear();
        bool effectLightsEnabled=Environment.GetEnvironmentVariable("PMC_NO_EFFECT_LIGHTS")!="1";
        foreach(var entry in _meshes)
        {
            var mesh=entry.Source;
            var localPose=mesh.MountBone<0?bodyPose:mesh.LocalPose(seconds,running);
            var mount=mesh.MountTransform(scene.Skeleton,bodyPose,seconds,running);
            if(entry.Physics is {} physics)
            {
                if(BonePhysics)localPose=physics.Step(localPose,mount,seconds,running?RunVelocity:default);
                else physics.Reset();
            }
            if(effectLightsEnabled&&entry.Lights.Length>0)
            {
                float scale=Vector3.TransformNormal(Vector3.UnitX,mount).Length();
                foreach(var binding in entry.Lights)
                {
                    var light=binding.Source;
                    var position=light.Position;
                    if(localPose!=null&&binding.Bone>=0)
                        position=Vector3.Transform(position,localPose[binding.Bone]);
                    position=ToWorld(Vector3.Transform(position,mount));
                    _effectLights.AddRange([position.X,position.Y,position.Z,0,light.Colour.X,light.Colour.Y,light.Colour.Z,light.Radius*scale]);
                }
            }
            var clothNormals=entry.ClothNormals;
            if(clothNormals!=null)
                for(int i=0;i<clothNormals.Length;i++)
                    clothNormals[i]=SkinNormal(mesh.Geometry.Vertices[i],localPose);
            Matrix4x4? trailing=clothNormals!=null&&trailingPose!=null
                ?mesh.MountTransform(scene.Skeleton,trailingPose,Math.Max(0,seconds-.10f),running):null;
            var positions=mesh.WorldPositions(localPose,mount,seconds,clothNormals,trailing);
            for(int i=0;i<positions.Length;i++)
            {
                var vertex=mesh.Geometry.Vertices[i];
                var normal=Vector3.TransformNormal(clothNormals?[i]??SkinNormal(vertex,localPose),mount);
                _vertices[entry.VertexOffset+i]=new(ToWorld(positions[i]),ToWorld(normal),vertex.Uv,Vector4.One);
            }
        }
        if(_meshes.Count>0&&_meshes[0].Source.Geometry.Vertices.Length>0)
        {
            var body=_vertices.AsSpan(0,_meshes[0].Source.Geometry.Vertices.Length);
            var minimum=body[0].Position;var maximum=minimum;
            foreach(var vertex in body){minimum=Vector3.Min(minimum,vertex.Position);maximum=Vector3.Max(maximum,vertex.Position);}
            var center=(minimum+maximum)*.5f;center.Y=_floorY??(minimum.Y-.7f);
            float radius=Math.Clamp((maximum.Y-minimum.Y)*.28f,9,105);
            Quad(_shadowVertex,center,Vector3.UnitX*radius,Vector3.UnitZ*radius*.82f,Vector3.UnitY);
            if(_floorY.HasValue)
                Quad(_floorVertex,center,Vector3.UnitX*330,Vector3.UnitZ*330,Vector3.UnitY);
        }

        PreviewMesh? particleMesh=null;
        Matrix4x4 particleMount=default;
        Matrix4x4[]? particlePose=null;
        for(int i=0;i<_particles.Count;i++)
        {
            var particle=_particles[i];
            if(!ReferenceEquals(particleMesh,particle.Mesh))
            {
                particleMesh=particle.Mesh;
                particleMount=particleMesh.MountTransform(scene.Skeleton,bodyPose,seconds,running);
                particlePose=particleMesh.MountBone<0?bodyPose:particleMesh.LocalPose(seconds,running);
            }
            float localTime=ParticleMotion.Clock(particle.Mesh,particle.Emitter,seconds,clip,clipTime);
            var sprite=ParticleMotion.Evaluate(particle.Emitter,particleMount,particlePose,localTime,particle.Phase,particle.Along);
            if(sprite.Opacity<=0){_draws[_shapeCount+i]=default;continue;}
            var center=ToWorld(sprite.Center);
            if(particle.Light>=0)
            {
                lightPositions[particle.Light]=new(center,Math.Clamp(sprite.Width*1.8f,30,76));
                lightColours[particle.Light]=new(_lightColours[particle.Light]*(.30f+.45f*sprite.Opacity),0);
            }
            var x=right*(sprite.Width*.5f);
            var y=(particle.Emitter.FaceGround?Vector3.Cross(Vector3.UnitY,right):up)*(sprite.Height*.5f);
            Quad(particle.VertexOffset,center,x,y,particle.Emitter.FaceGround?Vector3.UnitY:-forward,sprite.Atlas);
            bool multiply=particle.Emitter.Blend==PreviewOpacity.Multiply;
            uint blend=multiply?3u:particle.Emitter.Blend==PreviewOpacity.Additive?2u:1u;
            _draws[_shapeCount+i]=new((uint)particle.IndexOffset,6,multiply?particle.Texture:0,multiply?0:particle.Texture,0,0,blend,
                new(0,0,0,multiply?4:3),new(1,1,1,particle.Coverage*sprite.Opacity),new(0,0,particle.Emitter.Strength,0),
                Modulation:new(1,0,1,particle.Emitter.SoftDepth),
                UvTransform:new(particle.Emitter.UvTiling,particle.Emitter.UvMovement.X,particle.Emitter.UvMovement.Y));
        }

        for(int i=0;i<_trails.Count;i++)
        {
            var trail=_trails[i];
            var pose=trail.Mesh.MountBone<0?bodyPose:trail.Mesh.LocalPose(seconds,running);
            var anchor=trail.Source.Anchor;
            var point=anchor.Bone>=0&&pose!=null?Vector3.Transform(anchor.Position,pose[anchor.Bone]):anchor.Position;
            var world=ToWorld(Vector3.Transform(point,trail.Mesh.MountTransform(scene.Skeleton,bodyPose,seconds,running)));
            int draw=_shapeCount+_particles.Count+i;
            _draws[draw]=default;
            var samples=trail.Samples;
            if(samples.Count>0&&seconds<samples[^1].Time)samples.Clear();
            while(samples.Count>0&&(samples.Count>=TrailPoints||seconds-samples[0].Time>trail.Source.Duration))samples.RemoveAt(0);
            if(samples.Count==0||Vector3.DistanceSquared(samples[^1].Position,world)>.08f)samples.Add((world,seconds));
            if(samples.Count<2||Vector3.Distance(samples[0].Position,samples[^1].Position)<trail.Source.MinimumDistance)continue;
            for(int j=0;j<samples.Count;j++)
            {
                var direction=samples[Math.Min(j+1,samples.Count-1)].Position-samples[Math.Max(0,j-1)].Position;
                var side=Vector3.Cross(direction,forward);
                side=side.LengthSquared()>.0001f?Vector3.Normalize(side):right;
                float along=(float)j/(samples.Count-1);
                side*=trail.Source.Width*.5f*(.18f+.82f*along);
                int vertex=trail.VertexOffset+j*2;
                _vertices[vertex]=new(samples[j].Position-side,-forward,new(0,along));
                _vertices[vertex+1]=new(samples[j].Position+side,-forward,new(1,along));
            }
            _draws[draw]=new((uint)trail.IndexOffset,(uint)(samples.Count-1)*6,0,trail.Texture,0,0,2,
                new(0,0,0,3),new(1,1,1,.64f),new(0,0,1.75f,0));
        }

        int gameDraw=_shapeCount+_particles.Count+_trails.Count;
        foreach(var game in _gameEmitters)
            _draws[gameDraw++]=DrawEmitter(game,scene,bodyPose,seconds,clip,clipTime,eye,right,up,forward);
        foreach(var game in _gameTrails)
            _draws[gameDraw++]=DrawTrail(game,scene,bodyPose,seconds,right,forward,running);

        Native.preview_set_lights(_renderer,_effectLights.ToArray(),(uint)Math.Min(_effectLights.Count/8,8));

        var frame=new Frame(view*projection,new(eye,1),new(seconds,1f/width,1f/height,lightingPreset),
            lightPositions[0],lightPositions[1],lightPositions[2],lightColours[0],lightColours[1],lightColours[2],new(near,far,0,0),
            new(referenceExposure,0,0,0));

        long nativeStart=System.Diagnostics.Stopwatch.GetTimestamp();
        int result=Native.preview_draw(_renderer,in frame,_vertices,(uint)_vertices.Length,_indices,(uint)_indices.Length,
            _draws,(uint)_draws.Length,(uint)width,(uint)height,_pixels,(uint)width*4);
        if(result<0)throw new InvalidOperationException($"Direct3D rendering ended with code 0x{result:X8}.");
        long copyStart=System.Diagnostics.Stopwatch.GetTimestamp();
        _bitmap.WritePixels(new Int32Rect(0,0,width,height),_pixels,width*4,0);
        LastNativeMs=System.Diagnostics.Stopwatch.GetElapsedTime(nativeStart,copyStart).TotalMilliseconds;
        LastCopyMs=System.Diagnostics.Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
        return _bitmap;
    }

    private static Vector3 Point(PreviewAnchor anchor,Matrix4x4[]? pose)=>anchor.Bone>=0&&pose!=null&&anchor.Bone<pose.Length?Vector3.Transform(anchor.Position,pose[anchor.Bone]):anchor.Position;

    private static int LightBone(PreviewMesh mesh,SkeletonLight light)
    {
        if(mesh.LocalRig is not {} rig||mesh.LocalIdle==null&&mesh.LocalRun==null)return -1;
        int named=Array.FindIndex(rig.Bones,bone=>bone.Name.Equals(light.Name,StringComparison.OrdinalIgnoreCase));
        if(named>=0)return named;
        // A light on a nearby rigidly skinned part can follow the part even when the AST record has no bone name.
        var vertex=mesh.Geometry.Vertices.MinBy(v=>Vector3.DistanceSquared(v.Position,light.Position));
        if(vertex==null||Vector3.DistanceSquared(vertex.Position,light.Position)>.36f*light.Radius*light.Radius)return -1;
        int rigid=Array.IndexOf(vertex.Weights,(byte)255);
        return rigid>=0&&vertex.Bones[rigid]<rig.Bones.Length?vertex.Bones[rigid]:-1;
    }

    private Draw? GpuParticleDraw(PreviewEmitter emitter)
    {
        uint texture=Texture(emitter.BrushTexture,true);
        if(texture==0)return null;
        bool multiply=emitter.Blend==PreviewOpacity.Multiply;
        uint blend=multiply?3u:emitter.Blend==PreviewOpacity.Additive?2u:1u;
        return new(0,0,multiply?texture:0,multiply?0:texture,0,0,blend,
            new(0,0,0,multiply?4:3),Vector4.One,new(0,0,emitter.Strength,0),
            Modulation:new(1,1,1,emitter.SoftDepth),
            UvTransform:new(emitter.UvTiling,emitter.UvMovement.X,emitter.UvMovement.Y));
    }

    private Draw DrawEmitter(GameEmitter game,PreviewScene scene,Matrix4x4[]? bodyPose,float seconds,string? clip,float clipTime,
        Vector3 eye,Vector3 right,Vector3 up,Vector3 forward)
    {
        var emitter=game.Source;
        float time=ParticleMotion.Clock(game.Mesh,emitter,seconds,clip,clipTime);
        if(time<0)return default;
        bool running=clip?.StartsWith("run",StringComparison.OrdinalIgnoreCase)==true;
        var mount=game.Mesh.MountTransform(scene.Skeleton,bodyPose,seconds,running);
        var pose=game.Mesh.MountBone<0?bodyPose:game.Mesh.LocalPose(seconds,running);
        var anchors=emitter.Anchors.Count>0?emitter.Anchors.Select(a=>Point(a,pose)).ToArray()
            :[Point(new(emitter.Start,emitter.StartBone),pose),Point(new(emitter.End,emitter.EndBone),pose)];
        var shape=EmitterShape.For(emitter.Definition!.Kind,anchors);
        Vector3 Spawn(float u)=>shape.Spawn(u);
        _sprites.Clear();
        game.Simulator.Evaluate(time+(emitter.UsesCharacterAnimation?0:game.Simulator.Warmup),Spawn,_sprites);
        if(_sprites.Count==0)return default;
        var definition=emitter.Definition;
        bool ground=definition.FaceType.Contains("Ground",StringComparison.OrdinalIgnoreCase)||definition.FaceType.Contains("Floor",StringComparison.OrdinalIgnoreCase)
            ||definition.FaceType.Contains("XY",StringComparison.OrdinalIgnoreCase)||definition.FaceType.Equals("FaceUp",StringComparison.OrdinalIgnoreCase);
        bool fixedFacing=definition.FaceType=="XZLock"||(emitter.ParticleMesh!=null&&definition.FaceType=="None");
        bool upright=definition.FaceType.Equals("ZLock",StringComparison.OrdinalIgnoreCase);
        var orientation=mount;
        if(fixedFacing&&definition.LockedToBone&&pose!=null&&emitter.StartBone>=0&&emitter.StartBone<pose.Length)
            orientation=pose[emitter.StartBone]*mount;
        Vector3 authoredX=ToWorld(Vector3.TransformNormal(Vector3.UnitX,orientation));
        Vector3 authoredUp=ToWorld(Vector3.TransformNormal(-Vector3.UnitZ,orientation));
        Vector3 authoredNormal=ToWorld(Vector3.TransformNormal(-Vector3.UnitY,orientation));
        bool alongVelocity=definition.RotationLock.Equals("Velocity",StringComparison.OrdinalIgnoreCase);
        for(int i=0;i<_sprites.Count;i++)
        {
            var sprite=_sprites[i];
            var centre=ToWorld(Vector3.Transform(sprite.Position,mount));
            _sprites[i]=sprite with{Position=centre,Velocity=ToWorld(Vector3.TransformNormal(sprite.Velocity,mount)),Depth=Vector3.DistanceSquared(centre,eye)};
        }
        // Blended particles are drawn back to front; additive ones do not depend on order.
        if(game.Template.Blend is 1 or 5)_sprites.Sort(static (a,b)=>b.Depth.CompareTo(a.Depth));
        int count=Math.Min(_sprites.Count,game.Capacity);
        var geometry=emitter.ParticleMesh;
        for(int i=0;i<count;i++)
        {
            var sprite=_sprites[i];
            Vector3 x,y;
            var normal=fixedFacing?authoredNormal:ground?Vector3.UnitY:-forward;
            if(fixedFacing)
            {
                float c=MathF.Cos(sprite.Rotation),s=MathF.Sin(sprite.Rotation);
                x=authoredX*c+authoredUp*s;y=authoredUp*c-authoredX*s;
            }
            else if(ground)
            {
                float c=MathF.Cos(sprite.Rotation),s=MathF.Sin(sprite.Rotation);
                x=new(c,0,s);y=new(-s,0,c);
            }
            else if(upright)
            {
                x=right;
                y=Vector3.UnitY;
                normal=Vector3.Normalize(Vector3.Cross(x,y));
            }
            else if(alongVelocity&&sprite.Velocity-forward*Vector3.Dot(sprite.Velocity,forward) is var planar&&planar.LengthSquared()>1e-4f)
            {
                y=Vector3.Normalize(planar);
                x=Vector3.Normalize(Vector3.Cross(y,forward));
            }
            else
            {
                float c=MathF.Cos(sprite.Rotation),s=MathF.Sin(sprite.Rotation);
                x=right*c+up*s;y=up*c-right*s;
            }
            var a=sprite.Atlas;
            float u0=a.X,u1=a.X+a.Z,v0=a.Y,v1=a.Y+a.W;
            if(sprite.FlipX)(u0,u1)=(u1,u0);
            if(sprite.FlipY)(v0,v1)=(v1,v0);
            if(geometry!=null)
            {
                int meshStart=game.VertexOffset+i*geometry.Vertices.Length;
                var angles=sprite.LocalRotation;
                var rotation=angles==Vector3.Zero?Matrix4x4.Identity:Matrix4x4.CreateRotationX(angles.X)*
                    Matrix4x4.CreateRotationY(angles.Y)*Matrix4x4.CreateRotationZ(angles.Z);
                for(int vertexIndex=0;vertexIndex<geometry.Vertices.Length;vertexIndex++)
                {
                    var vertex=geometry.Vertices[vertexIndex];
                    var p=Vector3.TransformNormal(vertex.Position,rotation);
                    var position=sprite.Position+(x*p.X*sprite.Width-y*p.Z*sprite.Height-normal*p.Y*sprite.Width)*.5f;
                    var localNormal=Vector3.TransformNormal(vertex.Normal,rotation);
                    var n=x*localNormal.X-y*localNormal.Z-normal*localNormal.Y;
                    var uv=new Vector2(float.Lerp(u0,u1,vertex.Uv.X),float.Lerp(v0,v1,vertex.Uv.Y));
                    _vertices[meshStart+vertexIndex]=new(position,n,uv,sprite.Colour);
                }
                continue;
            }
            x*=sprite.Width*.5f;y*=sprite.Height*.5f;
            int start=game.VertexOffset+i*4;
            _vertices[start]=new(sprite.Position-x-y,normal,new(u0,v1),sprite.Colour);
            _vertices[start+1]=new(sprite.Position+x-y,normal,new(u1,v1),sprite.Colour);
            _vertices[start+2]=new(sprite.Position+x+y,normal,new(u1,v0),sprite.Colour);
            _vertices[start+3]=new(sprite.Position-x+y,normal,new(u0,v0),sprite.Colour);
        }
        return game.Template with{First=(uint)game.IndexOffset,Count=(uint)(count*(geometry?.Indices.Length??6))};
    }

    private Draw DrawTrail(GameTrail game,PreviewScene scene,Matrix4x4[]? bodyPose,float seconds,Vector3 right,Vector3 forward,bool running)
    {
        float time=game.Source.UsesLocalAnimation?seconds-game.Source.StartsAt:seconds;
        if(time<0)
        {
            game.Simulator.Reset();
            return default;
        }
        var pose=game.Mesh.MountBone<0?bodyPose:game.Mesh.LocalPose(seconds,running);
        var mount=game.Mesh.MountTransform(scene.Skeleton,bodyPose,seconds,running);
        var trail=game.Simulator.Trail;
        Matrix4x4 inverse=default;
        bool inParent=trail.LockedToParent&&Matrix4x4.Invert(mount,out inverse);
        var recordingSpace=inParent?Matrix4x4.Identity:mount;
        var a=Vector3.Transform(Point(game.Source.Anchor,pose),recordingSpace);
        var b=game.Source.EdgeAnchor is {} edge?Vector3.Transform(Point(edge,pose),recordingSpace):a;
        var side=new Vector3(right.X,right.Z,-right.Y);
        var view=new Vector3(forward.X,forward.Z,-forward.Y);
        if(inParent)
        {
            side=Vector3.Normalize(Vector3.TransformNormal(side,inverse));
            view=Vector3.Normalize(Vector3.TransformNormal(view,inverse));
        }
        game.Simulator.Update(time,a,b,side,_ribbon,view);
        if(_ribbon.Count<2)return default;
        int points=Math.Min(_ribbon.Count,TrailSimulator.MaxSegments+1);
        float length=Math.Max(1e-3f,_ribbon[points-1].Distance);
        var drawSpace=inParent?mount:Matrix4x4.Identity;
        for(int j=0;j<points;j++)
        {
            var point=_ribbon[j];
            float v=trail.UvMode==0?point.Distance/length:point.Distance*Math.Max(.01f,trail.TextureStretch)/100;
            var left=ToWorld(Vector3.Transform(point.Left,drawSpace));
            var rightEdge=ToWorld(Vector3.Transform(point.Right,drawSpace));
            var normal=trail.CameraFacing?-forward:Vector3.UnitY;
            _vertices[game.VertexOffset+j*2]=new(left,normal,new(0,v),point.Colour);
            _vertices[game.VertexOffset+j*2+1]=new(rightEdge,normal,new(1,v),point.Colour);
        }
        return game.Template with{First=(uint)game.IndexOffset,Count=(uint)(points-1)*6};
    }

    /// <summary>A draw with the shader composed from the game's fragments, or null to use the built-in surface shader.</summary>
    private Draw? GameDraw(PreviewGameShader shader,ImageSource?[] textures)
    {
        if(!_materials.TryGetValue(shader.Bytecode,out uint id))
        {
            id=Native.preview_material(_renderer,shader.Bytecode,(uint)shader.Bytecode.Length);
            _materials[shader.Bytecode]=id;
        }
        if(id==0)return null;
        var slots=new MaterialSlots();
        foreach(var binding in shader.Textures)
            if(binding.Slot<ModelPreviewLoader.GameShaderSlots)slots[binding.Slot]=Texture(textures.ElementAtOrDefault(binding.Slot),binding.Srgb);
        // The word after the texture slots carries the vertex offset along the normal (native Draw.normalOffset).
        slots[ModelPreviewLoader.GameShaderSlots]=BitConverter.SingleToUInt32Bits(shader.Vertex.NormalOffset);
        // The game's own blend state for the material; opaque and alpha-tested shapes keep the depth-writing pass.
        return new Draw(0,0,0,0,0,0,shader.BlendState,default,Vector4.One,default,Material:id,Slots:slots);
    }

    private void Quad(int start,Vector3 center,Vector3 x,Vector3 y,Vector3 normal,Vector4? textureArea=null)
    {
        var uv=textureArea??new Vector4(0,0,1,1);
        _vertices[start]=new(center-x-y,normal,new(uv.X,uv.Y+uv.W));
        _vertices[start+1]=new(center+x-y,normal,new(uv.X+uv.Z,uv.Y+uv.W));
        _vertices[start+2]=new(center+x+y,normal,new(uv.X+uv.Z,uv.Y));
        _vertices[start+3]=new(center-x+y,normal,new(uv.X,uv.Y));
    }

    private static void AddQuadIndices(List<int> indices,int vertex)
        =>indices.AddRange([vertex,vertex+1,vertex+2,vertex,vertex+2,vertex+3]);

    private static Vector3? SpriteColour(ImageSource texture)
    {
        if(texture is not BitmapSource bitmap)return null;
        var format=bitmap.Format==PixelFormats.Bgra32?bitmap:new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);
        int width=format.PixelWidth,height=format.PixelHeight;
        var sample=new TransformedBitmap(format,new ScaleTransform(12d/width,12d/height));
        var bytes=new byte[sample.PixelWidth*sample.PixelHeight*4];
        sample.CopyPixels(bytes,sample.PixelWidth*4,0);
        Vector3 weighted=default;
        for(int i=0;i<bytes.Length;i+=4)
        {
            var colour=new Vector3(bytes[i+2],bytes[i+1],bytes[i])/255;
            float strength=colour.LengthSquared()*bytes[i+3]/255;
            weighted+=colour*strength;
        }
        return weighted.LengthSquared()<.05f?null:Vector3.Normalize(weighted)*1.8f;
    }

    private uint Texture(ImageSource? image,bool srgb)
    {
        if(image is not BitmapSource bitmap)return 0;
        if(_textures.TryGetValue((bitmap,srgb),out uint id))return id;
        var data=bitmap.Format==PixelFormats.Bgra32?bitmap:new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);
        const int maxDimension=4096;
        if(Math.Max(data.PixelWidth,data.PixelHeight)>maxDimension)
        {
            double scale=(double)maxDimension/Math.Max(data.PixelWidth,data.PixelHeight);
            data=new TransformedBitmap(data,new ScaleTransform(scale,scale));
        }
        var pixels=new byte[checked(data.PixelWidth*data.PixelHeight*4)];
        data.CopyPixels(pixels,data.PixelWidth*4,0);
        id=Native.preview_texture(_renderer,pixels,(uint)data.PixelWidth,(uint)data.PixelHeight,srgb?1:0);
        if(id==0)throw new InvalidOperationException("Direct3D did not load a wardrobe texture.");
        _textures.Add((bitmap,srgb),id);
        return id;
    }

    private static Vector3 SkinNormal(SkinVertex vertex,Matrix4x4[]? skinning)
    {
        if(skinning==null)return vertex.Normal;
        Vector3 normal=default;
        for(int weight=0;weight<4;weight++)
            if(vertex.Weights[weight]>0)normal+=Vector3.TransformNormal(vertex.Normal,skinning[vertex.Bones[weight]])*vertex.Weights[weight];
        return normal.LengthSquared()>.0001f?Vector3.Normalize(normal):vertex.Normal;
    }

    private static Vector3 ToWorld(Vector3 value)=>new(value.X,-value.Z,value.Y);
    private static Vector3 Vector(Point3D value)=>new((float)value.X,(float)value.Y,(float)value.Z);
    public void Dispose()=>_renderer.Dispose();

    private sealed class Renderer : SafeHandleZeroOrMinusOneIsInvalid
    {
        private Renderer():base(true){}
        protected override bool ReleaseHandle(){Native.preview_destroy(handle);return true;}
    }

    private static class Native
    {
        private const string Library="PoeMtxPreview.dll";
        [DllImport(Library,ExactSpelling=true)] internal static extern Renderer preview_create(byte[] shader,uint length,[Out] byte[] error,uint errorLength,int forceWarp);
        [DllImport(Library,ExactSpelling=true)] internal static extern void preview_destroy(nint renderer);
        [DllImport(Library,ExactSpelling=true)] internal static extern int preview_is_software(Renderer renderer);
        [DllImport(Library,ExactSpelling=true)] internal static extern void preview_clear_textures(Renderer renderer);
        [DllImport(Library,ExactSpelling=true)] internal static extern uint preview_material(Renderer renderer,byte[] bytecode,uint length);
        [DllImport(Library,ExactSpelling=true)] internal static extern void preview_set_lights(Renderer renderer,[In] float[] lights,uint count);
        [DllImport(Library,ExactSpelling=true)] internal static extern uint preview_texture(Renderer renderer,byte[] pixels,uint width,uint height,int srgb);
        [DllImport(Library,ExactSpelling=true)] internal static extern int preview_draw(Renderer renderer,in Frame frame,
            [In] Vertex[] points,uint pointCount,[In] int[] indices,uint indexCount,[In] Draw[] draws,uint drawCount,
            uint width,uint height,[Out] byte[] pixels,uint stride);
    }
}
