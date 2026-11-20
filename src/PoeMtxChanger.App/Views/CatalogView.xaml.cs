using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PoeMtxChanger.App.ViewModels;

namespace PoeMtxChanger.App.Views;

public partial class CatalogView : UserControl
{
    public CatalogView() => InitializeComponent();
    private void OpenAudio(object sender,RoutedEventArgs e) => new AudioWindow(App.Services){Owner=Window.GetWindow(this)}.Show();
    private void OpenBuffs(object sender,RoutedEventArgs e) => new BuffVisualWindow(App.Services){Owner=Window.GetWindow(this)}.Show();

    private void OpenModelPreview(object sender, RoutedEventArgs e)
    {
        if (DataContext is CatalogViewModel vm)
            new ModelPreviewWindow(App.Services,vm.SelectedItem?.Item) {Owner=Window.GetWindow(this)}.Show();
    }

    /// <summary>Clicking the selected category again clears the filter (shows every category).</summary>
    private void CategoryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(CategoryList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (item is { IsSelected: true } && DataContext is CatalogViewModel vm)
        {
            vm.SelectedCategory = null;
            e.Handled = true;
        }
    }
}
