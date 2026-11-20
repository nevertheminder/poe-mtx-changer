using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PoeMtxChanger.App.ViewModels;

namespace PoeMtxChanger.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += OnVmChanged;
                UpdateDot(vm.IsGameRunning);
            }
        };
        // The maximised size comes from WM_GETMINMAXINFO (the monitor's work area), so no margin is needed.
        StateChanged += (_, _) => Root.Margin = new Thickness(0);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsGameRunning) && sender is MainViewModel vm) UpdateDot(vm.IsGameRunning);
    }

    private void UpdateDot(bool running)
    {
        var c = running ? Color.FromRgb(0xD0, 0x91, 0x3A) : Color.FromRgb(0x00, 0xBC, 0x65);
        StatusDot.Fill = new SolidColorBrush(c);
        if (StatusDot.Effect is System.Windows.Media.Effects.DropShadowEffect fx) fx.Color = c;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Dark system frame on Windows 10/11 (DWMWA_USE_IMMERSIVE_DARK_MODE = 20). Purely cosmetic; failures are ignored.
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowProc);
    }

    /// <summary>
    /// A borderless window (WindowStyle None) maximises over the whole monitor, under the taskbar, hiding the
    /// status bar and its buttons. WM_GETMINMAXINFO limits the maximised window to the monitor's work area.
    /// </summary>
    private static nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (message != WM_GETMINMAXINFO) return 0;
        var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return 0;
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.MaxPosition = new NativePoint { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
        mmi.MaxSize = new NativePoint { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
        Marshal.StructureToPtr(mmi, lParam, false);
        // Not handled: WPF still adds the MinWidth/MinHeight track sizes to the same structure.
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
