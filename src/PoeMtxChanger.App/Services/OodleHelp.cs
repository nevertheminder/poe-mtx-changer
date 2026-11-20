using System.Diagnostics;
using System.Windows;

namespace PoeMtxChanger.App.Services;

/// <summary>
/// Explains where to get the Oodle library when none was found: it cannot be shipped with the program, but Unreal
/// Engine (free, through the Epic Games Launcher) includes it, and the program finds it there by itself.
/// </summary>
internal static class OodleHelp
{
    public const string DownloadPage = "https://www.unrealengine.com/download";

    private const string Text =
        "The game's files are compressed with Oodle (oo2core_*_win64.dll). Its licence does not allow shipping it with " +
        "this program, but you can get it for free:\n\n" +
        "• Install Unreal Engine with the Epic Games Launcher (free; a large download). The program then finds Oodle " +
        "by itself.\n" +
        "• Or, if a game on this computer comes with oo2core_*_win64.dll, choose that file in Settings → Browse….\n\n" +
        "After installing, open Settings and click Find next to the Oodle library.\n\n" +
        "Without Oodle the program cannot read or change the game files.\n\n" +
        "Open the Unreal Engine download page now?";

    /// <summary>Shows the help; returns true when the user chose to open the download page (it is opened).</summary>
    public static bool Show()
    {
        var owner = Application.Current?.MainWindow;
        var answer = owner != null
            ? MessageBox.Show(owner, Text, "Oodle library not found", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes)
            : MessageBox.Show(Text, "Oodle library not found", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return false;
        try { Process.Start(new ProcessStartInfo(DownloadPage) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return true;
    }
}
