using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.App.ViewModels;
using PoeMtxChanger.App.Views;

internal static partial class Program
{
    /// <summary>
    /// Renders every main tab at several window sizes and reports controls that end up outside the window or
    /// collapsed to nothing (clipped buttons, lists without room).
    /// </summary>
    internal static void CheckUiSizes(AppServices app, string directory)
    {
        Directory.CreateDirectory(directory);
        if (Application.Current == null)
        {
            var wpf = new PoeMtxChanger.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            wpf.InitializeComponent();
        }
        (int W, int H)[] sizes = [(1100, 700), (1280, 720), (1536, 1024), (1920, 1080), (2560, 1440)];
        int problems = 0;
        foreach (var (w, h) in sizes)
        {
            var model = new MainViewModel(app);
            var window = new MainWindow
            {
                DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual, Width = w, Height = h,
            };
            window.Show();
            // The catalog was set directly on the session; announce it like LoadArchiveAsync does.
            var changed = typeof(GameSession).GetField("CatalogChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(app.Game) as EventHandler;
            changed?.Invoke(app.Game, EventArgs.Empty);
            try
            {
                foreach (MainTab tab in Enum.GetValues<MainTab>())
                {
                    model.SelectedTab = tab;
                    Wait(Task.Delay(tab == MainTab.Wardrobe ? 2500 : 500));
                    var content = (FrameworkElement)window.Content;
                    content.UpdateLayout();
                    var shot = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    shot.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(shot));
                    using (var file = File.Create(Path.Combine(directory, $"{w}x{h}-{tab}.png"))) encoder.Save(file);
                    foreach (var button in Find<ButtonBase>(window).Where(b => b.IsVisible))
                    {
                        var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                        bool outside = bounds.Right > content.ActualWidth + 1 || bounds.Bottom > content.ActualHeight + 1 || bounds.Left < -1;
                        bool squashed = button.ActualWidth < 8 || button.ActualHeight < 8;
                        if (!outside && !squashed) continue;
                        if (IsInsideScrollViewer(button)) continue;
                        problems++;
                        Console.WriteLine($"{w}x{h} {tab}: '{Label(button)}' {(outside ? "outside the window" : "squashed")} at {bounds}");
                    }
                }
            }
            finally { window.Close(); }
        }
        // Maximised: the window must stay inside the work area (above the taskbar), with the launch button visible.
        {
            var window = new MainWindow { DataContext = new MainViewModel(app), ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            window.WindowState = WindowState.Maximized;
            Wait(Task.Delay(800));
            var work = SystemParameters.WorkArea;
            var launch = Find<Button>(window).FirstOrDefault(b => Label(b).Contains("Launch", StringComparison.Ordinal) || b.Content is not string && b.ToString().Contains("Launch"));
            var screenBottom = launch?.PointToScreen(new Point(0, launch.ActualHeight)).Y;
            var dpi = VisualTreeHelper.GetDpi(window);
            Console.WriteLine($"maximised: {window.ActualWidth:F0}x{window.ActualHeight:F0}, work area {work.Width:F0}x{work.Height:F0}, launch button bottom {screenBottom / dpi.DpiScaleY:F0}");
            if (window.ActualHeight > work.Height + 1 || screenBottom / dpi.DpiScaleY > work.Bottom + 1) { problems++; Console.WriteLine("maximised window goes past the work area"); }
            window.Close();
        }
        Console.WriteLine(problems == 0 ? "PASS all buttons fit at every size." : $"{problems} layout problems.");
    }

    private static bool IsInsideScrollViewer(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer) return true;
        return false;
    }

    private static string Label(ButtonBase button) => button.Content switch
    {
        string text => text,
        TextBlock block => block.Text,
        _ => button.ToolTip as string ?? button.Name ?? button.GetType().Name,
    };
}
