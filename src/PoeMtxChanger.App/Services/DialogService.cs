using System.Windows;
using Microsoft.Win32;

namespace PoeMtxChanger.App.Services;

public interface IDialogService
{
    bool Confirm(string title, string message);
    void Info(string title, string message);
    void Error(string title, string message);
    string? PickFile(string title, string filter, string? initial = null);
    string? SaveFile(string title, string filter, string defaultName);
    string? PickFolder(string title, string? initial = null);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    public bool Confirm(string title, string message)
        => MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void Info(string title, string message) => MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    public void Error(string title, string message) => MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public string? PickFile(string title, string filter, string? initial = null)
    {
        var d = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(Path.GetDirectoryName(initial))) d.InitialDirectory = Path.GetDirectoryName(initial);
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public string? SaveFile(string title, string filter, string defaultName)
    {
        var d = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName, OverwritePrompt = true };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public string? PickFolder(string title, string? initial = null)
    {
        var d = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) d.InitialDirectory = initial;
        return d.ShowDialog(Owner) == true ? d.FolderName : null;
    }
}
