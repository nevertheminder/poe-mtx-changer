using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Formats.Geometry;

namespace PoeMtxChanger.App.Views;

/// <summary>Local character dressing room with a separate Direct3D renderer.</summary>
public sealed class ModelPreviewWindow : Window
{
    private readonly ModelPreviewPanel _preview;
    public Task Ready => _preview.Ready;

    public ModelPreviewWindow(AppServices app, CosmeticItem? item)
    {
        Title = "3D try-on · " + (item?.DisplayName ?? "Character");
        Width = 1120; Height = 860; MinWidth = 800; MinHeight = 600;
        Background = new SolidColorBrush(Color.FromRgb(10,13,15)); Foreground = Brushes.Bisque;
        Content = _preview = new ModelPreviewPanel(app,item);
    }

    public void DrawFrame(float seconds) => _preview.DrawFrame(seconds);
}

internal sealed class ModelPreviewPanel : UserControl
{
    private readonly AppServices _app;
    private readonly CosmeticItem? _item;
    private readonly bool _compact;
    private const string ReferencePostProcess="PoE · post-processing";
    private static readonly string[] StudioLighting=["Neutral","Warm","Muted"];
    private readonly Viewport3D _viewport=new();
    private readonly Image _renderedPreview=new(){Name="Direct3DFrame",Stretch=Stretch.Fill,IsHitTestVisible=false};
    private readonly ComboBox _lighting=new(){Width=132,Margin=new Thickness(6,0,0,0),ItemsSource=StudioLighting,SelectedIndex=0,ToolTip="Wardrobe lighting"};
    private readonly ComboBox _character=new(){Width=115,Margin=new Thickness(5,0,12,0)};
    private readonly ComboBox _animation=new(){Width=240,Margin=new Thickness(5,0,12,0),DisplayMemberPath="Name"};
    private readonly CheckBox _onCharacter=new(){Content="On character",IsChecked=true,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6)};
    private readonly CheckBox _profile=new(){Content="Wear the active set",IsChecked=true,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6)};
    private readonly CheckBox _animate=new(){Content="Animation",IsChecked=true,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6)};
    private readonly CheckBox _clothPhysics=new(){Content="Cloth physics",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6),
        ToolTip="Capes, ribbons and skirts move with the physics from the item files. Turn it off to see their animated pose."};
    private readonly Button _motion=new(){Content="Run",Margin=new Thickness(6,0,0,0),Padding=new Thickness(9,3,9,3),IsEnabled=false,ToolTip="Check the cape and trails in motion"};
    private readonly Button _gameAngle=new(){Content="Top view",Margin=new Thickness(6,0,0,0),Padding=new Thickness(9,3,9,3),ToolTip="Check effects near the ground from the game's camera angle"};
    private readonly TextBlock _message=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12),Foreground=Brushes.LightGray};
    private readonly PerspectiveCamera _camera=new(){FieldOfView=40,NearPlaneDistance=0.01,FarPlaneDistance=100000};
    private readonly FrameLoop _timer;
    private readonly Stopwatch _clock=new();
    private readonly List<(PreviewMesh Source,MeshGeometry3D[] Geometry)> _meshes=[];
    private readonly List<WeaponParticles> _particles=[];
    private Direct3DPreview? _direct;
    private bool _directDisabled;
    private float _lastFrame;
    private PreviewScene? _scene;
    private AnimationClip? _clip;
    private AnimationClip? _idleClip,_runClip;
    private float _duration;
    private double _yaw=Math.PI,_pitch=0.08,_distance=350,_radius=100;
    private Point3D _center;
    private bool _framed;
    private Point? _drag;
    private int _loadVersion;
    private CancellationTokenSource? _load;
    public Task Ready {get;private set;}=Task.CompletedTask;
    internal string Character => (string)(_character.SelectedItem ?? "Marauder");
    internal event EventHandler? CharacterChanged;

    public ModelPreviewPanel(AppServices app,CosmeticItem? item,bool compact=false)
    {
        _app=app;_item=item;_compact=compact;Foreground=Brushes.Bisque;
        var layout=new DockPanel();Content=layout;
        var top=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(12)};DockPanel.SetDock(top,Dock.Top);layout.Children.Add(top);
        top.Children.Add(new TextBlock{Text="Class",VerticalAlignment=VerticalAlignment.Center});top.Children.Add(_character);top.Children.Add(_onCharacter);top.Children.Add(_profile);
        var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);layout.Children.Add(bottom);
        var controls=new WrapPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(12,6,12,0)};
        controls.Children.Add(_animate);controls.Children.Add(_animation);controls.Children.Add(_clothPhysics);
        var reset=new Button{Content="Reset camera",Padding=new Thickness(12,5,12,5)};reset.Click+=(_,_)=>FitCamera();controls.Children.Add(reset);
        controls.Children.Add(new TextBlock{Text="Light",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,0,0),
            ToolTip="PoE · post-processing uses the exposure from 2_3_2_Default.env and an experimental bloom over the neutral light."});
        controls.Children.Add(_lighting);
        controls.Children.Add(_motion);
        controls.Children.Add(_gameAngle);
        bottom.Children.Add(controls);bottom.Children.Add(_message);
        var preview=new Grid();preview.Children.Add(_viewport);preview.Children.Add(_renderedPreview);
        var dragSurface=new Border { Background=Brushes.Transparent, Child=preview };
        layout.Children.Add(dragSurface);_viewport.Camera=_camera;
        if(compact)
        {
            _onCharacter.Visibility=_profile.Visibility=Visibility.Collapsed;
            _animation.Width=130;
            reset.Content="Camera";
            _message.FontSize=11;
            _message.TextWrapping=TextWrapping.NoWrap;
            _message.TextTrimming=TextTrimming.CharacterEllipsis;
            _message.Margin=new Thickness(12,6,12,8);
        }
        _character.ItemsSource=CharacterClasses.All.Select(c=>c.Key);_character.SelectedItem="Marauder";
        _onCharacter.IsChecked=item==null||item.MtxType=="Character Effect"||item.Category is CosmeticCategory.Helmet or CosmeticCategory.BodyArmour or CosmeticCategory.Gloves or CosmeticCategory.Boots or CosmeticCategory.WingsAndBack;
        _character.SelectionChanged+=(_,_)=>{ CharacterChanged?.Invoke(this,EventArgs.Empty);Ready=Reload(); };
        _onCharacter.Click+=(_,_)=>Ready=Reload();_profile.Click+=(_,_)=>Ready=Reload();
        _animation.SelectionChanged+=(_,_)=>ChooseAnimation();_animate.Click+=(_,_)=>_clock.Restart();
        _clothPhysics.IsChecked=app.Settings.ClothPhysics;
        _clothPhysics.Click+=(_,_)=>
        {
            app.Settings.ClothPhysics=_clothPhysics.IsChecked==true;
            app.SaveSettings();
            if(_direct!=null)_direct.BonePhysics=app.Settings.ClothPhysics;
            DrawFrame(_lastFrame);
        };
        _motion.Click+=(_,_)=>
        {
            _animate.IsChecked=true;
            _animation.SelectedItem=_clip==_runClip?_idleClip:_runClip;
        };
        _gameAngle.Click+=(_,_)=>FitCamera(gameAngle:true);
        _lighting.SelectionChanged+=(_,_)=>DrawFrame(_lastFrame);
        _viewport.SizeChanged+=(_,_)=>DrawFrame(_lastFrame);
        dragSurface.MouseLeftButtonDown+=(_,e)=>{_drag=e.GetPosition(dragSurface);dragSurface.CaptureMouse();};
        dragSurface.MouseLeftButtonUp+=(_,_)=>dragSurface.ReleaseMouseCapture();
        dragSurface.LostMouseCapture+=(_,_)=>_drag=null;
        dragSurface.MouseMove+=(_,e)=>
        {
            if(_drag is not {} old)return;
            var next=e.GetPosition(dragSurface);
            _yaw-=(next.X-old.X)*.008;
            _pitch=Math.Clamp(_pitch+(next.Y-old.Y)*.008,-1.2,1.2);
            _drag=next;
            UpdateCamera();
        };
        dragSurface.MouseWheel+=(_,e)=>{_distance=Math.Clamp(_distance*Math.Exp(-e.Delta*.001),_radius*.5,_radius*20);UpdateCamera();};
        _timer=new FrameLoop(AnimateFrame);
        Loaded+=(_,_)=>Ready=Reload();
        Unloaded+=(_,_)=>{_load?.Cancel();_loadVersion++;_timer.Stop();_direct?.Dispose();_direct=null;_renderedPreview.Source=null;};
    }
    internal void RefreshScene() { if(IsLoaded) Ready=Reload(); }
    private async Task Reload()
    {
        int version=++_loadVersion;_load?.Cancel();_load=new();var token=_load.Token;_timer.Stop();_meshes.Clear();_particles.Clear();_viewport.Children.Clear();
        _scene=null;_renderedPreview.Source=null;_lastFrame=0;_motion.IsEnabled=false;
        _message.Text="Reading models and animations from the local game…";
        string character=(string)(_character.SelectedItem??"Marauder");bool onCharacter=_onCharacter.IsChecked==true,profile=_profile.IsChecked==true;
        try
        {
            var scene=await Task.Run(()=>new ModelPreviewLoader(_app).Load(_item,character,onCharacter,profile,token,showWeapons:_compact),token);
            if(version!=_loadVersion)return;
            if(!_directDisabled)
                try { _direct??=new Direct3DPreview();_direct.BonePhysics=_clothPhysics.IsChecked==true;_direct.SetScene(scene); }
                catch(Exception ex)when(ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
                { DisableDirect3D(ex); }
            int selectedLight=Math.Max(0,_lighting.SelectedIndex);
            if(scene.ReferenceExposure.HasValue!=(_lighting.Items.Count>StudioLighting.Length))
            {
                _lighting.ItemsSource=scene.ReferenceExposure.HasValue?(string[])[..StudioLighting,ReferencePostProcess]:StudioLighting;
                _lighting.SelectedIndex=selectedLight<_lighting.Items.Count?selectedLight:0;
            }
            _scene=scene;
            var group=new Model3DGroup();group.Children.Add(new AmbientLight(Color.FromRgb(125,125,125)));group.Children.Add(new DirectionalLight(Colors.White,new Vector3D(-1,-1,-2)));group.Children.Add(new DirectionalLight(Color.FromRgb(100,120,150),new Vector3D(1,.3,1)));
            foreach(var mesh in scene.Meshes)
            {
                var positions=Points(mesh.Pose(scene.Skeleton,null,0)); var uv=new PointCollection(mesh.Geometry.Vertices.Select(v=>new Point(v.Uv.X,v.Uv.Y)));uv.Freeze();
                var visible=new List<MeshGeometry3D>();
                for(int i=0;i<mesh.Geometry.Shapes.Length;i++)
                {
                    var shape=mesh.Geometry.Shapes[i];if(mesh.HiddenShapes.Contains(shape.Name)||mesh.Surfaces.ElementAtOrDefault(i)?.Opacity==PreviewOpacity.Multiply)continue;
                    var indices=new Int32Collection(mesh.Geometry.Indices.Skip(shape.FirstTriangle*3).Take(shape.TriangleCount*3));indices.Freeze();
                    var geometry=new MeshGeometry3D{Positions=positions,TextureCoordinates=uv,TriangleIndices=indices};visible.Add(geometry);
                    var material=new MaterialGroup();
                    var glow=mesh.Glow.ElementAtOrDefault(i);
                    if(mesh.Textures[i] is {} image) material.Children.Add(new DiffuseMaterial(TextureBrush(image)));
                    else if(glow==null)material.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(152,143,124))));
                    if(glow!=null)material.Children.Add(new EmissiveMaterial(TextureBrush(glow)));
                    material.Freeze();
                    group.Children.Add(new GeometryModel3D(geometry,material){BackMaterial=material});
                }
                _meshes.Add((mesh,visible.ToArray()));
                if(mesh.Emitters.Count>0)_particles.Add(new WeaponParticles(mesh));
            }
            _viewport.Children.Add(new ModelVisual3D{Content=group});
            const string cameraHelp = "Drag to rotate. The wheel zooms.";
            _message.Text=_compact
                ? scene.HasIncompleteLayers ? "There are 3D limitations. Hover for details." : cameraHelp
                : scene.Description+"\n"+cameraHelp;
            _message.ToolTip=scene.Description+(_directDisabled?"\nThe compatible WPF renderer is used.":"\nDirect3D · normal and reflection maps, HDR glow.");
            _animation.ItemsSource=scene.Skeleton?.Clips;
            _idleClip=scene.Skeleton?.Clips.FirstOrDefault(c=>c.Name.Equals(scene.PreferredIdle,StringComparison.OrdinalIgnoreCase))
                ??scene.Skeleton?.Clips.FirstOrDefault(c=>c.Name.Contains("idle",StringComparison.OrdinalIgnoreCase))??scene.Skeleton?.Clips.FirstOrDefault();
            string runName="run_"+scene.PreferredIdle.Replace("idle_","",StringComparison.OrdinalIgnoreCase);
            _runClip=scene.Skeleton?.Clips.FirstOrDefault(c=>c.Name.Equals(runName,StringComparison.OrdinalIgnoreCase))
                ??scene.Skeleton?.Clips.FirstOrDefault(c=>c.Name.StartsWith(runName+"_",StringComparison.OrdinalIgnoreCase))
                ??scene.Skeleton?.Clips.FirstOrDefault(c=>c.Name is "run" or "run_unarmed");
            _motion.IsEnabled=_idleClip!=null&&_runClip!=null;
            _animation.SelectedItem=_idleClip;
            var initialPose=scene.Skeleton?.Skinning(_clip,0);
            Rect3D bound=Rect3D.Empty;
            foreach(var mesh in scene.Meshes)
                foreach(var point in Points(mesh.Pose(scene.Skeleton,initialPose,0)))bound.Union(point);
            if(bound.IsEmpty)bound=group.Bounds;
            _center=new Point3D(bound.X+bound.SizeX/2,bound.Y+bound.SizeY/2,bound.Z+bound.SizeZ/2);
            double radius=Math.Max(1,Math.Max(bound.SizeX,Math.Max(bound.SizeY,bound.SizeZ))*.6);
            _distance*=radius/_radius;
            _radius=radius;
            if(_framed)UpdateCamera();
            else{FitCamera(gameAngle:scene.Meshes.Any(mesh=>mesh.GroundOffsetZ!=null));_framed=true;}
            _clock.Restart();
            foreach(var particles in _particles)group.Children.Add(particles.Model);
            DrawFrame(0);
            _timer.Start();
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(version==_loadVersion)_message.Text="3D is unavailable for this resource: "+ex.Message;_app.Log("3D").LogErrorForPreview(ex);}
    }
    private void DisableDirect3D(Exception error)
    {
        _direct?.Dispose();_direct=null;_directDisabled=true;_renderedPreview.Source=null;_viewport.Opacity=1;
        _lighting.IsEnabled=false;
        _app.Log("3D").LogErrorForPreview(error);
    }
    private void ChooseAnimation()
    {
        _clip=_animation.SelectedItem as AnimationClip;_clock.Restart();
        _motion.Content=_clip!=null&&_clip==_runClip?"Stance":"Run";
        try{_duration=_clip==null?0:_scene!.Skeleton!.Duration(_clip);}
        catch(Exception ex){_clip=null;_duration=0;_message.Text="The animation is not supported: "+ex.Message+"\nThe original pose is shown.";}
        DrawFrame(0);
    }
    private void AnimateFrame()
    {
        if(_animate.IsChecked!=true||_scene==null)return;
        try{DrawFrame((float)_clock.Elapsed.TotalSeconds);}
        catch(Exception ex){_timer.Stop();_message.Text="Animation stopped: "+ex.Message;}
    }
    public void DrawFrame(float seconds)
    {
        if(_scene is not {} scene||!_framed)return;
        _lastFrame=seconds;
        var body=scene.Skeleton;
        float clipTime=_duration>0?seconds%_duration:0;
        var skinning=body?.Skinning(_clip,clipTime);
        if(_direct!=null&&_viewport.ActualWidth>=10&&_viewport.ActualHeight>=10)
        {
            try
            {
                var dpi=VisualTreeHelper.GetDpi(_viewport);
                double width=_viewport.ActualWidth*dpi.DpiScaleX,height=_viewport.ActualHeight*dpi.DpiScaleY;
                double scale=Math.Min(1,(_direct.IsSoftware?690:1300)/Math.Max(width,height));
                var trailingPose=_duration>0&&scene.Meshes.Any(m=>m.ClothFlex>0)
                    ?body?.Skinning(_clip,Math.Max(0,seconds-.10f)%_duration):null;
                bool reference=_lighting.SelectedItem as string==ReferencePostProcess;
                var bitmap=_direct.Render(scene,skinning,seconds,_camera,Math.Max(1,(int)(width*scale)),Math.Max(1,(int)(height*scale)),
                    reference?0:_lighting.SelectedIndex,_clip?.Name,clipTime,trailingPose,reference?scene.ReferenceExposure??0:0);
                if(!ReferenceEquals(_renderedPreview.Source,bitmap))_renderedPreview.Source=bitmap;
                _viewport.Opacity=0;
                return;
            }
            catch(InvalidOperationException ex){DisableDirect3D(ex);}
        }
        bool running=_clip?.Name.StartsWith("run",StringComparison.OrdinalIgnoreCase)==true;
        foreach(var mesh in _meshes){var points=Points(mesh.Source.Pose(body,skinning,seconds,running));foreach(var geometry in mesh.Geometry)geometry.Positions=points;}
        foreach(var particles in _particles)particles.Draw(body,skinning,seconds,_camera.LookDirection,_clip?.Name,clipTime);
    }
    private static Point3DCollection Points(Vector3[] vertices)
    {var points=new Point3DCollection(vertices.Length);foreach(var v in vertices)points.Add(new(v.X,-v.Z,v.Y));points.Freeze();return points;}
    private static ImageBrush TextureBrush(ImageSource image) => new(image)
    { ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,1,1),TileMode=TileMode.Tile };
    private void UpdateCamera()
    {
        double x=Math.Sin(_yaw)*Math.Cos(_pitch)*_distance,z=Math.Cos(_yaw)*Math.Cos(_pitch)*_distance,y=Math.Sin(_pitch)*_distance;
        _camera.Position=new(_center.X+x,_center.Y+y,_center.Z+z);_camera.LookDirection=new(-x,-y,-z);_camera.UpDirection=new(0,1,0);
        if(_animate.IsChecked!=true)DrawFrame(_lastFrame);
    }
    private void FitCamera(bool gameAngle=false)
    {
        _yaw=Math.PI+(gameAngle ? .36 : 0);
        _pitch=gameAngle ? .73 : .08;
        _distance=_radius*(_compact?3.0:3.4)*Math.Max(1,_viewport.ActualWidth/Math.Max(1,_viewport.ActualHeight));
        UpdateCamera();
    }
}

/// <summary>
/// Animation loop on WPF's own frames (CompositionTarget.Rendering) instead of a coarse DispatcherTimer. Draws about
/// 60 times a second in step with the display: every frame at 60 Hz, every second frame at 120–144 Hz, so a fast
/// monitor does not take the whole UI thread.
/// </summary>
internal sealed class FrameLoop(Action tick)
{
    private const double TargetMs = 1000.0 / 60;
    private bool _running;
    private TimeSpan _lastEvent, _lastDraw;
    private double _periodMs = TargetMs;

    public void Start()
    {
        if (_running) return;
        _running = true; _lastEvent = _lastDraw = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.FromTicks(Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
        if (now == _lastEvent) return; // Rendering can fire more than once per composed frame.
        if (_lastEvent != TimeSpan.Zero)
            _periodMs += (Math.Clamp((now - _lastEvent).TotalMilliseconds, 2, 50) - _periodMs) * .1;
        _lastEvent = now;
        if (_lastDraw != TimeSpan.Zero && (now - _lastDraw).TotalMilliseconds < TargetMs - _periodMs * .5) return;
        _lastDraw = now;
        tick();
    }
}

internal static class PreviewLogExtensions
{
    public static void LogErrorForPreview(this Microsoft.Extensions.Logging.ILogger logger,Exception ex)
        =>Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger,ex,"3D preview could not load this resource");
}
