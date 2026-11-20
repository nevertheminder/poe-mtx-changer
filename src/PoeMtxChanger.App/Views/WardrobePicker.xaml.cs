using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.Core.Catalog;

namespace PoeMtxChanger.App.Views;

internal sealed partial class WardrobeChoice(string name, object value, string? iconPath = null, string? detail = null)
    : ObservableObject
{
    public string Name { get; } = name;
    public object Value { get; } = value;
    public string? Detail { get; } = detail;
    public string Mark => Icon is null ? "\uE734" : "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Mark))]
    private ImageSource? _icon;
    private bool _requested;

    public async Task LoadIconAsync(ThumbnailService thumbnails)
    {
        if (_requested || string.IsNullOrWhiteSpace(iconPath)) return;
        _requested = true;
        Icon = await thumbnails.GetAsync(iconPath);
    }
}

public partial class WardrobePicker : Window
{
    private const int PageSize = 72;
    private readonly ThumbnailService _thumbnails;
    private readonly IReadOnlyList<WardrobeChoice> _choices;
    private readonly Func<object,TargetSuggestion?,(bool Allowed,string Details)> _describe;
    private readonly Func<WardrobeChoice,TargetSuggestion?,bool> _matchesTarget;
    private string? _listNote;

    internal object? Value { get; private set; }
    internal TargetSuggestion? Target => Targets.SelectedItem as TargetSuggestion;
    internal bool ResetRequested { get; private set; }
    internal bool ExtraRequested { get; private set; }

    internal WardrobePicker(string title, ThumbnailService thumbnails, IReadOnlyList<WardrobeChoice> choices,
        Func<object,TargetSuggestion?,(bool Allowed,string Details)> describe,
        IReadOnlyList<TargetSuggestion>? targets = null, TargetSuggestion? target = null,
        object? current = null, string? resetLabel = null, string? extraLabel = null,
        string saveLabel = "Save to profile", Func<WardrobeChoice,TargetSuggestion?,bool>? matchesTarget = null,
        string? targetLabel = null)
    {
        _thumbnails = thumbnails;
        _choices = choices;
        _describe = describe;
        _matchesTarget = matchesTarget ?? ((_,_) => true);
        InitializeComponent();
        Title = title;
        Commit.Content = saveLabel;
        if(targetLabel!=null)TargetLabel.Text=targetLabel;
        Targets.ItemsSource = targets;
        Targets.SelectedItem = target;
        TargetLabel.Visibility = Targets.Visibility = targets is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        Reset.Content = resetLabel;
        Reset.Visibility = resetLabel is null ? Visibility.Collapsed : Visibility.Visible;
        Extra.Content = extraLabel;
        Extra.Visibility = extraLabel is null ? Visibility.Collapsed : Visibility.Visible;
        Filter(current);
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) => Filter();
    private void TargetChanged(object sender, SelectionChangedEventArgs e) => Filter();
    private void ChoiceChanged(object sender, SelectionChangedEventArgs e) => Describe();

    private void Filter(object? selected = null)
    {
        if (Choices is null || Search is null || Info is null) return;
        selected ??= (Choices.SelectedItem as WardrobeChoice)?.Value;
        string query = Search.Text.Trim();
        var matched = _choices.Where(c => (c.Name.Contains(query,StringComparison.CurrentCultureIgnoreCase)
                || c.Detail?.Contains(query,StringComparison.CurrentCultureIgnoreCase) == true)
            && _matchesTarget(c,Target)).ToArray();
        var visible = matched.Take(PageSize).ToList();
        var current = matched.FirstOrDefault(c => Equals(c.Value,selected));
        if (current != null && !visible.Contains(current)) visible.Insert(0,current);
        _listNote = matched.Length > visible.Count ? $"Showing {visible.Count} of {matched.Length}. Refine the search." : null;
        Choices.ItemsSource = visible;
        Choices.SelectedItem = current;
        if (current != null) Choices.ScrollIntoView(current);
        foreach (var choice in visible) _ = choice.LoadIconAsync(_thumbnails);
        Describe();
    }

    private void Describe()
    {
        if (Commit is null || Info is null) return;
        if (Choices.SelectedItem is WardrobeChoice choice)
        {
            var (allowed,details) = _describe(choice.Value,Target);
            Commit.IsEnabled = allowed;
            Info.Text = JoinNote(details);
        }
        else
        {
            Commit.IsEnabled = false;
            Info.Text = JoinNote(Choices.Items.Count == 0 ? "No matching variants." : "Select a card.");
        }
    }

    private string JoinNote(string details) => _listNote is null ? details : details + "\n" + _listNote;

    private void SaveClicked(object sender, RoutedEventArgs e) => Save();
    private void ChoiceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject origin
            && ItemsControl.ContainerFromElement(Choices,origin) is ListBoxItem)
            Save();
    }
    private void Save()
    {
        if (!Commit.IsEnabled || Choices.SelectedItem is not WardrobeChoice choice) return;
        Value = choice.Value;
        DialogResult = true;
    }
    private void ResetClicked(object sender, RoutedEventArgs e)
    {
        ResetRequested = true;
        DialogResult = true;
    }
    private void ExtraClicked(object sender, RoutedEventArgs e)
    {
        ExtraRequested = true;
        DialogResult = true;
    }
}
