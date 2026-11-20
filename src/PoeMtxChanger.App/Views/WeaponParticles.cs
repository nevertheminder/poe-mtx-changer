using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.Core.Formats.Geometry;

namespace PoeMtxChanger.App.Views;

/// <summary>Small camera-facing preview of the loaded particle textures along the named weapon anchors.</summary>
internal sealed class WeaponParticles
{
    private sealed record Sprite(PreviewEmitter Emitter, ImageBrush Brush, MeshGeometry3D Geometry, float Offset, float Along);
    private readonly PreviewMesh _mesh;
    private readonly List<Sprite> _sprites = [];
    public Model3DGroup Model { get; } = new();

    public WeaponParticles(PreviewMesh mesh)
    {
        _mesh=mesh;
        foreach(var emitter in mesh.Emitters.Where(e=>e.Blend!=PreviewOpacity.Multiply))
        {
            int count=ParticleMotion.Count(emitter);
            for(int j=0;j<count;j++)
            {
                var brush=new ImageBrush(emitter.BrushTexture);
                var geometry=new MeshGeometry3D
                {
                    TriangleIndices = [0,1,2,0,2,3],
                    TextureCoordinates = [new(0,1),new(1,1),new(1,0),new(0,0)]
                };
                var material=new EmissiveMaterial(brush);
                Model.Children.Add(new GeometryModel3D(geometry,material){BackMaterial=material});
                _sprites.Add(new(emitter,brush,geometry,(float)j/count,((j*3+1)%count+.5f)/count));
            }
        }
    }

    public void Draw(AnimationSkeleton? skeleton,Matrix4x4[]? pose,float seconds,Vector3D viewDirection,string? clip,float clipTime)
    {
        var mount=_mesh.MountTransform(skeleton,pose,seconds);
        var emitterPose=_mesh.MountBone<0?pose:_mesh.LocalPose(seconds);
        var right=Vector3D.CrossProduct(viewDirection,new Vector3D(0,1,0));right.Normalize();
        var up=Vector3D.CrossProduct(right,viewDirection);up.Normalize();
        foreach(var sprite in _sprites)
        {
            float localTime=ParticleMotion.Clock(_mesh,sprite.Emitter,seconds,clip,clipTime);
            var frame=ParticleMotion.Evaluate(sprite.Emitter,mount,emitterPose,localTime,sprite.Offset,sprite.Along);
            var center=new Point3D(frame.Center.X,-frame.Center.Z,frame.Center.Y);
            var x=right*frame.Width/2;var y=up*frame.Height/2;
            sprite.Geometry.Positions=[center-x-y,center+x-y,center+x+y,center-x+y];
            if(sprite.Emitter.AtlasSide>1)
            {
                var a=frame.Atlas;
                sprite.Geometry.TextureCoordinates=[new(a.X,a.Y+a.W),new(a.X+a.Z,a.Y+a.W),new(a.X+a.Z,a.Y),new(a.X,a.Y)];
            }
            sprite.Brush.Opacity=frame.Opacity;
        }
    }
}

internal static class ParticleMotion
{
    internal static int Count(PreviewEmitter emitter) => Math.Clamp((int)(emitter.Rate*emitter.Lifetime),2,12);

    internal static float Clock(PreviewMesh mesh,PreviewEmitter emitter,float seconds,string? clip,float clipTime)
    {
        if(emitter.UsesCharacterAnimation)
            return string.Equals(clip,emitter.Animation,StringComparison.OrdinalIgnoreCase)?clipTime-emitter.StartsAt:-1;
        if(emitter.StartsAt>0&&mesh.LocalIdleSeconds>0)return seconds%mesh.LocalIdleSeconds-emitter.StartsAt;
        return seconds;
    }

    private static float Curve(float[] values,float phase,float fallback)
    {
        if(values.Length==0)return fallback;
        float index=phase*(values.Length-1);
        int left=(int)index;
        return float.Lerp(values[left],values[Math.Min(left+1,values.Length-1)],index-left);
    }

    internal static (Vector3 Center,float Width,float Height,float Opacity,Vector4 Atlas) Evaluate(PreviewEmitter emitter,Matrix4x4 mount,
        Matrix4x4[]? pose,float seconds,float offset,float along)
    {
        if(seconds<0)return(default,0,0,0,new(0,0,1,1));
        float time=(seconds/emitter.Lifetime+offset)%1;
        float fade=MathF.Sin(MathF.PI*time);
        Vector3 Point(PreviewAnchor anchor)=>anchor.Bone>=0&&pose!=null?Vector3.Transform(anchor.Position,pose[anchor.Bone]):anchor.Position;
        var start=Point(new(emitter.Start,emitter.StartBone));
        var end=Point(new(emitter.End,emitter.EndBone));
        if(emitter.Anchors.Count>2)
        {
            float segment=along*(emitter.Anchors.Count-1);
            int index=Math.Min((int)segment,emitter.Anchors.Count-2);
            start=Point(emitter.Anchors[index]);end=Point(emitter.Anchors[index+1]);along=segment-index;
        }
        var axis=end-start;
        var local=Vector3.Lerp(start,end,along);
        if(axis.LengthSquared()>.001f)
        {
            float movement=emitter.Speed*emitter.Lifetime*time;
            float limit=Math.Max(7,emitter.Size*1.6f);
            local+=Vector3.Normalize(axis)*Math.Clamp(movement,-limit,limit);
        }
        float size=emitter.Size*Math.Clamp(Curve(emitter.SizeOverLife,time,.5f+.5f*fade),0,1);
        float stretch=Math.Clamp(Curve(emitter.StretchOverLife,time,1),.3f,3f);
        float alpha=Curve(emitter.AlphaOverLife,time,1)*Curve(emitter.OpacityOverLife,time,1)*fade;
        int side=emitter.AtlasSide;
        float atlasTime=time*emitter.AtlasSpeed;
        atlasTime=emitter.AtlasOnce?Math.Min(atlasTime,.99999f):atlasTime%1;
        int frame=(int)(atlasTime*side*side);
        var atlas=new Vector4((float)(frame%side)/side,(float)(frame/side)/side,1f/side,1f/side);
        return(Vector3.Transform(local,mount),Math.Max(.05f,size),Math.Max(.05f,size*stretch),Math.Clamp(alpha,0,1),atlas);
    }
}
