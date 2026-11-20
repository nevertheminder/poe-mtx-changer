using System.Windows;
using System.Windows.Controls;

namespace PoeMtxChanger.App.Views;

public partial class MySetsView : UserControl
{
    public MySetsView() => InitializeComponent();
    private void OpenPreview(object sender,RoutedEventArgs e)=>new ModelPreviewWindow(App.Services,null){Owner=Window.GetWindow(this)}.Show();
    private void OpenAudio(object sender,RoutedEventArgs e)=>new AudioWindow(App.Services){Owner=Window.GetWindow(this)}.Show();

    private void SkillList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is { } selected) list.ScrollIntoView(selected);
    }

    private void Equip_Checked(object sender, RoutedEventArgs e)
    {
        if (EquipPanel is null) return;
        EquipPanel.Visibility = Visibility.Visible;
        SkillPanel.Visibility = Visibility.Collapsed;
    }

    private void Skills_Checked(object sender, RoutedEventArgs e)
    {
        EquipPanel.Visibility = Visibility.Collapsed;
        SkillPanel.Visibility = Visibility.Visible;
    }
}
