using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PoeMtxChanger.App.Views;

/// <summary>
/// Hold Alt over a card, list row or button that shows an icon to see that icon enlarged next to the cursor.
/// Registered once for every window of the app. Alt pressed alone no longer opens the window's system menu.
/// </summary>
internal static class IconPeek
{
    private const int WmSysCommand = 0x0112, ScKeyMenu = 0xF100;
    private static readonly Image Preview = new() { Stretch = Stretch.Uniform };
    private static readonly Popup Popup = new()
    {
        AllowsTransparency = true, Placement = PlacementMode.Relative, IsHitTestVisible = false, Focusable = false,
        Child = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(240, 10, 13, 15)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(200, 160, 90)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(8), Child = Preview,
        },
    };
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        RenderOptions.SetBitmapScalingMode(Preview, BitmapScalingMode.HighQuality);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler(KeyDown));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyUpEvent, new KeyEventHandler(KeyUp));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseMoveEvent, new MouseEventHandler(MouseMoved));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(WindowLoaded));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) Hide();
        }));
    }

    private static bool IsAlt(KeyEventArgs e) => e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt
        || e.Key is Key.LeftAlt or Key.RightAlt;

    private static void KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsAlt(e)) return;
        e.Handled = true; // Keep focus where it is instead of moving it to access keys.
        Update((Window)sender);
    }

    private static void KeyUp(object sender, KeyEventArgs e)
    {
        if (!IsAlt(e)) return;
        e.Handled = true;
        Hide();
    }

    private static void MouseMoved(object sender, MouseEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) Update((Window)sender);
        else if (Popup.IsOpen) Hide();
    }

    private static void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender || PresentationSource.FromVisual((Window)sender) is not HwndSource source) return;
        // Alt released alone sends SC_KEYMENU with lParam 0; swallow it so the peek does not open the system menu.
        source.AddHook((nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) =>
        {
            if (msg == WmSysCommand && ((int)wParam & 0xFFF0) == ScKeyMenu && lParam == 0) handled = true;
            return 0;
        });
    }

    private static void Hide()
    {
        Popup.IsOpen = false;
        Preview.Source = null;
    }

    private static void Update(Window window)
    {
        if (FindIcon(Mouse.DirectlyOver as DependencyObject) is not { } icon) { Hide(); return; }
        // About 360 DIP on the longer side: small 1×1 icons grow up to 4×, tall armour icons fit without cropping.
        double w = icon is BitmapSource b ? b.PixelWidth : icon.Width, h = icon is BitmapSource p ? p.PixelHeight : icon.Height;
        double scale = Math.Clamp(360 / Math.Max(1, Math.Max(w, h)), 1, 4);
        Preview.Width = w * scale; Preview.Height = h * scale;
        Preview.Source = icon;
        var at = Mouse.GetPosition(window);
        Popup.PlacementTarget = window;
        Popup.HorizontalOffset = at.X + 22;
        Popup.VerticalOffset = at.Y + 22;
        if (!Popup.IsOpen) Popup.IsOpen = true;
    }

    /// <summary>The icon of the card or row under the cursor: the first image inside its item container or button.</summary>
    private static ImageSource? FindIcon(DependencyObject? hovered)
    {
        for (var node = hovered; node != null; node = Parent(node))
        {
            if (node is Image { Source: { } own } image && IsIcon(image)) return own;
            if (node is ListBoxItem or ComboBoxItem or ButtonBase or TreeViewItem) return FirstIcon(node);
            if (node is Window or ScrollViewer) return null;
        }
        return null;
    }

    private static ImageSource? FirstIcon(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Image { Source: { } source } image && IsIcon(image)) return source;
            if (FirstIcon(child) is { } found) return found;
        }
        return null;
    }

    // Small visible pictures only: skips the 3D frame and other large images.
    private static bool IsIcon(Image image) => image.IsVisible && image.ActualWidth is > 8 and <= 160 && image.ActualHeight is > 8 and <= 160;

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
