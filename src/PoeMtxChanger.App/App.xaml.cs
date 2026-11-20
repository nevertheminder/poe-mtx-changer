using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.App.ViewModels;
using PoeMtxChanger.App.Views;

namespace PoeMtxChanger.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // The program is English-only: format numbers and dates the English way whatever the Windows locale.
        var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = english;
        System.Threading.Thread.CurrentThread.CurrentCulture = System.Threading.Thread.CurrentThread.CurrentUICulture = english;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(english.IetfLanguageTag)));
        base.OnStartup(e);
        Views.IconPeek.Register();
        Services = new AppServices();
        var log = Services.Log("App");
        log.LogInformation("PoE MTX Changer starting (no telemetry, no network access)");

        DispatcherUnhandledException += (_, args) =>
        {
            log.LogError(args.Exception, "Unhandled UI exception");
            Services.Dialogs.Error("Unexpected error", args.Exception.Message);
            args.Handled = true;
        };

        var vm = new MainViewModel(Services);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();

        // Dev aids: "--demo" loads the MOCK catalog; "--render-screenshot <png> [tab]" renders the window to a file and exits.
        var args = e.Args;
        int buffShot=Array.IndexOf(args,"--render-buffs");
        if(buffShot>=0&&buffShot+1<args.Length){_ = RenderBuffsAsync(vm,args[buffShot+1]);return;}
        int audioShot = Array.IndexOf(args,"--render-audio");
        if(audioShot>=0&&audioShot+1<args.Length){_ = RenderAudioAsync(vm,args[audioShot+1]);return;}
        int modelShot = Array.IndexOf(args, "--render-model");
        if (modelShot >= 0 && modelShot + 1 < args.Length)
        {
            _ = RenderModelAsync(vm, args[modelShot + 1], modelShot + 2 < args.Length ? args[modelShot + 2] : null);
            return;
        }
        int shot = Array.IndexOf(args, "--render-screenshot");
        if (shot >= 0 && shot + 1 < args.Length)
        {
            var tab = shot + 2 < args.Length && Enum.TryParse<MainTab>(args[shot + 2], true, out var t) ? t : MainTab.Catalog;
            bool skills = shot + 2 < args.Length && args[shot + 2].Equals("SkillEffects", StringComparison.OrdinalIgnoreCase);
            bool assigned = shot + 2 < args.Length && args[shot + 2].Equals("CatalogStatus", StringComparison.OrdinalIgnoreCase);
            _ = RenderScreenshotAsync(window, vm, args[shot + 1], skills ? MainTab.MySets : tab, skills, assigned);
            return;
        }
        vm.ShowOodleHelp = true;
        _ = InitAsync(vm, args.Contains("--demo"));
    }

    private async Task RenderModelAsync(MainViewModel vm,string path,string? search)
    {
        try
        {
            await InitAsync(vm,false);
            var item=search==null?null:Services.Game.Catalog.FirstOrDefault(i=>i.DisplayName.Equals(search,StringComparison.OrdinalIgnoreCase));
            var model=new ModelPreviewWindow(Services,item);model.Show();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            await model.Ready;
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            model.DrawFrame(0);
            SaveVisual(model,path);
            model.DrawFrame(.6f);
            SaveVisual(model,Path.ChangeExtension(path,"frame2.png"));
        }
        catch(Exception ex){File.WriteAllText(path+".error.txt",ex.ToString());}
        finally{Shutdown();}
    }
    private async Task RenderAudioAsync(MainViewModel vm,string path)
    {
        try
        {
            await InitAsync(vm,false);var audio=new AudioWindow(Services);audio.Show();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await audio.Ready;
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveVisual(audio,path);
        }
        catch(Exception ex){File.WriteAllText(path+".error.txt",ex.ToString());}
        finally{Shutdown();}
    }
    private async Task RenderBuffsAsync(MainViewModel vm,string path)
    {
        try{await InitAsync(vm,false);var buffs=new BuffVisualWindow(Services);buffs.Show();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveVisual(buffs,path);}
        catch(Exception ex){File.WriteAllText(path+".error.txt",ex.ToString());}
        finally{Shutdown();}
    }
    private static void SaveVisual(Window window,string path)
    {
        window.UpdateLayout();var content=(FrameworkElement)window.Content;
        var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var bmp=new System.Windows.Media.Imaging.RenderTargetBitmap((int)(content.ActualWidth*dpi.DpiScaleX),(int)(content.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,System.Windows.Media.PixelFormats.Pbgra32);
        var drawing=new System.Windows.Media.DrawingVisual();
        var rectangle=new Rect(0,0,content.ActualWidth,content.ActualHeight);
        var offset=System.Windows.Media.VisualTreeHelper.GetOffset(content);
        var brush=new System.Windows.Media.VisualBrush(content){ViewboxUnits=System.Windows.Media.BrushMappingMode.Absolute,Viewbox=new Rect(offset.X,offset.Y,content.ActualWidth,content.ActualHeight),ViewportUnits=System.Windows.Media.BrushMappingMode.Absolute,Viewport=rectangle};
        using(var context=drawing.RenderOpen())context.DrawRectangle(brush,null,rectangle);
        bmp.Render(drawing);var enc=new System.Windows.Media.Imaging.PngBitmapEncoder();enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);using var output=File.Create(path);enc.Save(output);
    }

    private static async Task InitAsync(MainViewModel vm, bool demo)
    {
        await vm.InitializeAsync();
        if (demo && Services.Game.Catalog.Count == 0) Services.Game.LoadMockCatalog();
    }

    private async Task RenderScreenshotAsync(Window window, MainViewModel vm, string path, MainTab tab, bool skills = false, bool assigned = false)
    {
        await InitAsync(vm, demo: true);
        vm.SelectedTab = tab;
        if (skills)
        {
            vm.MySets.Section = 1;
            vm.MySets.SelectedSkill = vm.MySets.Skills.FirstOrDefault(s => s.Id == "cyclone") ?? vm.MySets.Skills.FirstOrDefault();
            vm.MySets.SelectedReplacement = vm.MySets.SkillReplacements.FirstOrDefault(v => v.Number == 5 && v.CanApply)
                ?? vm.MySets.SkillReplacements.FirstOrDefault(v => v.CanApply);
        }
        if (vm.Catalog.VisibleItems.Count > 1) vm.Catalog.SelectedItem = vm.Catalog.VisibleItems[1];
        if(assigned)
        {
            window.Width=1200;window.Height=760;
            vm.Catalog.SelectedItem=vm.Catalog.VisibleItems.FirstOrDefault(v=>v.Name=="Abyss Core Crown")??vm.Catalog.SelectedItem;
            vm.Catalog.StatusMessage="“Abyss Core Crown” was added to profile “Default”. In “My Sets”, choose which base item it is worn over.";
        }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (tab == MainTab.Wardrobe)
        {
            var view = (WardrobeView)window.FindName("WardrobePage");
            await view.PreviewReady;
        }
        await Task.Delay(400);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var content = (FrameworkElement)window.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY,
            System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(content);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var fs = File.Create(path)) enc.Save(fs);
        Shutdown();
    }
}
