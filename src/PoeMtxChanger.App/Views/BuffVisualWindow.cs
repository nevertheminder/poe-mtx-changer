using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.App.ViewModels;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Profiles;

namespace PoeMtxChanger.App.Views;

public sealed partial class BuffVisualWindow : Window
{
    private readonly AppServices _app;
    private readonly ComboBox _skill = new() { Name = "BuffSkillPicker", DisplayMemberPath = "Name", MinWidth = 340, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ListBox _original = new() { Name = "CurrentBuffVisuals" };
    private readonly ListBox _donor = new() { Name = "CompatibleBuffVisuals" };
    private readonly TextBox _originalSearch = new() { Margin = new Thickness(0,8,0,8), ToolTip = "Search the effect in use" };
    private readonly TextBox _donorSearch = new() { Margin = new Thickness(0,8,0,8), ToolTip = "Search the selected skill's variants" };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,12) };
    private readonly Button _save = new() { Content = "Add to the active set", Padding = new Thickness(12,8,12,8), Margin = new Thickness(0,0,8,0), IsEnabled = false };
    private SkillVisualVariant? _plan;
    private bool _filtering;

    public BuffVisualWindow(AppServices app, string? skillId = null)
    {
        _app = app;
        InitializeComponent();
        _original.ItemTemplate = _donor.ItemTemplate = (DataTemplate)Resources["BuffVisualRow"];
        Title = "Auras and heralds · choose by skill";
        Width = 1120; Height = 820; MinWidth = 850; MinHeight = 600;
        Background = new SolidColorBrush(Color.FromRgb(10,13,15)); Foreground = Brushes.Bisque;
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header,Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Auras and skill effects", FontSize = 24 });
        header.Children.Add(new TextBlock {
            Text = "Choose a gem / skill, the look it uses now and a replacement. Links are checked against the game tables. Set the look in use yourself: the program does not read equipped gems from the game.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,10) });
        header.Children.Add(new TextBlock { Text = "Gem / skill", Margin = new Thickness(0,0,0,5) });
        header.Children.Add(_skill); _skill.Margin = new Thickness(0,0,0,14);

        var bottom = new StackPanel(); DockPanel.SetDock(bottom,Dock.Bottom); root.Children.Add(bottom);
        var detailsScroll = new ScrollViewer { MaxHeight = 185, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _details };
        bottom.Children.Add(detailsScroll);
        var buttons = new WrapPanel(); bottom.Children.Add(buttons); buttons.Children.Add(_save); _save.Click += (_,_) => Save();
        var remove = new Button { Content = "Remove this replacement", Padding = new Thickness(12,8,12,8), Margin = new Thickness(0,0,8,0) }; buttons.Children.Add(remove);
        remove.Click += (_,_) => {
            if (_original.SelectedItem is not BuffVisualDefinition visual) return;
            app.Profiles.Active?.Entries.RemoveAll(e => e.Key == "buff-visual:" + visual.Id);
            app.Profiles.SaveActive();
            _details.Text = "The replacement was removed from the plan. Files already applied change after applying the set or restoring.";
        };
        var apply = new Button { Content = "Apply the active set", Padding = new Thickness(12,8,12,8) }; buttons.Children.Add(apply);
        apply.Click += async (_,_) => await new ProfileActions(app).ApplyActiveAsync();

        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(20) }); grid.ColumnDefinitions.Add(new()); root.Children.Add(grid);
        Column(0,"Used by the selected skill now",_original,_originalSearch);
        Column(2,"Variants for this skill",_donor,_donorSearch);
        void Column(int column,string title,ListBox list,TextBox search)
        {
            var panel = new DockPanel(); Grid.SetColumn(panel,column); grid.Children.Add(panel);
            var top = new StackPanel(); DockPanel.SetDock(top,Dock.Top); panel.Children.Add(top);
            top.Children.Add(new TextBlock { Text = title, FontSize = 18 }); top.Children.Add(search); panel.Children.Add(list);
        }
        _skill.ItemsSource = app.Game.BuffSkills;
        _skill.SelectionChanged += (_,_) => { _originalSearch.Text = ""; _donorSearch.Text = ""; FilterOriginals(); };
        _originalSearch.TextChanged += (_,_) => FilterOriginals();
        _donorSearch.TextChanged += (_,_) => FilterDonors();
        _original.SelectionChanged += (_,_) => { if (!_filtering) FilterDonors(); };
        _donor.SelectionChanged += (_,_) => { if (!_filtering) Plan(); };
        _skill.SelectedItem = app.Game.BuffSkills.FirstOrDefault(s => s.Id == (skillId ?? "hatred")) ?? app.Game.BuffSkills.FirstOrDefault();
        _skill.IsEnabled = skillId is null;
        if (_skill.SelectedItem == null) Plan();
        Closed += (_,_) => { if (Owner?.DataContext is MainViewModel main) main.MySets.Refresh(); };
    }

    private async void IconLoaded(object sender, RoutedEventArgs e) => await LoadIconAsync((Image)sender);

    private async void IconContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var image = (Image)sender;
        if (image.IsLoaded) await LoadIconAsync(image);
    }

    private async Task LoadIconAsync(Image image)
    {
        image.Source = null;
        if (image.DataContext is not BuffVisualDefinition visual || string.IsNullOrWhiteSpace(visual.IconPath)) return;
        var icon = await _app.Thumbnails.GetAsync(visual.IconPath);
        if (ReferenceEquals(image.DataContext, visual)) image.Source = icon;
    }

    private static bool MatchesSearch(BuffVisualDefinition visual, string query) =>
        visual.PickerName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        visual.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<BuffVisualSelection> SavedAuras(BuffSkillDefinition skill) =>
        (_app.Profiles.Active?.Entries ?? []).Where(e => e.Enabled)
            .Select(e => e.BuffSelection).OfType<BuffVisualSelection>().Where(e => e.SkillId == skill.Id);

    private void FilterOriginals()
    {
        if (_skill.SelectedItem is not BuffSkillDefinition skill) return;
        string? previous = (_original.SelectedItem as BuffVisualDefinition)?.Id;
        _filtering = true;
        var choices = _app.Game.BuffVisuals.Where(skill.Accepts)
            .Where(v => MatchesSearch(v,_originalSearch.Text)).ToArray();
        var saved = SavedAuras(skill).Select(v => v.OriginalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _original.ItemsSource = choices;
        _original.SelectedItem = choices.FirstOrDefault(v => v.Id == previous)
            ?? choices.FirstOrDefault(v => saved.Contains(v.Id))
            ?? (skill.Id == "hatred" ? choices.FirstOrDefault(v => v.Id == "player_aura_cold_damage") : null)
            ?? choices.FirstOrDefault(v => v.SkillGroups.Intersect(skill.VisualGroups,StringComparer.OrdinalIgnoreCase).Any())
            ?? choices.FirstOrDefault();
        if(_original.SelectedItem is {} selected)_original.ScrollIntoView(selected);
        _filtering = false; FilterDonors();
    }

    private void FilterDonors()
    {
        _filtering = true;
        string? previous = (_donor.SelectedItem as BuffVisualDefinition)?.Id;
        var skill = _skill.SelectedItem as BuffSkillDefinition;
        var original = _original.SelectedItem as BuffVisualDefinition;
        var choices = skill != null && original != null
            ? _app.Game.BuffVisuals.Where(v => v.Id != original.Id && BuffVisualCatalogBuilder.Compatible(original,v,skill))
                .Where(v => MatchesSearch(v,_donorSearch.Text)).ToArray()
            : [];
        string? saved = skill != null && original != null
            ? SavedAuras(skill).FirstOrDefault(v => v.OriginalId == original.Id)?.DonorId : null;
        _donor.ItemsSource = choices;
        _donor.SelectedItem = choices.FirstOrDefault(v => v.Id == previous)
            ?? choices.FirstOrDefault(v => v.Id == saved)
            ?? choices.FirstOrDefault(v => v.Id == "player_aura_cold_damage_alternate") ?? choices.FirstOrDefault();
        if(_donor.SelectedItem is {} selected)_donor.ScrollIntoView(selected);
        _filtering = false; Plan();
    }

    private void Plan()
    {
        _plan = null; _save.IsEnabled = false;
        if (_skill.SelectedItem is not BuffSkillDefinition skill || _original.SelectedItem is not BuffVisualDefinition original
            || _donor.SelectedItem is not BuffVisualDefinition donor || _app.Game.Resources is not { } source)
        {
            _details.Text = "Choose a skill and two effects. If the list is empty, the current tables have no confirmed link or no variants for this search.";
            return;
        }
        _plan = BuffVisualCatalogBuilder.Plan(original,donor,source,skill);
        _save.IsEnabled = _plan.CanApply;
        _details.Text = "Skill: " + skill.Name + "\n" + _plan.Summary
            + "\nThis is a shared file replacement: two copies of the same gem cannot be set up separately."
            + (_plan.Mappings.Count > 0 ? "\n" + string.Join("\n",_plan.Mappings.Take(3).Select(m => m.TargetPath + " ← " + m.SourcePath)) : "");
    }

    private void Save()
    {
        if (_plan is not { CanApply: true } || _skill.SelectedItem is not BuffSkillDefinition skill
            || _original.SelectedItem is not BuffVisualDefinition original || _donor.SelectedItem is not BuffVisualDefinition donor
            || _app.Profiles.Active is not { } profile || _app.Game.Resources is not { } source) return;
        // Recheck the association at the action boundary, not only when filtering the list.
        var checkedPlan = BuffVisualCatalogBuilder.Plan(original,donor,source,skill);
        if (!checkedPlan.CanApply) { _details.Text = checkedPlan.Summary; return; }
        var report = new MappingValidator(ResourcePolicy.Default).Validate(checkedPlan.Mappings,source,_app.Settings.ValidateResources);
        if (report.HasErrors) { _details.Text = string.Join("\n",report.Issues.Where(i => i.Severity == IssueSeverity.Error).Take(8).Select(i => i.Message)); return; }
        var entry = profile.GetOrAddEntry(ProfileEntryKind.SkillEffect,"buff-visual:" + original.Id);
        entry.BuffSelection = new(skill.Id,original.Id,donor.Id);
        entry.CosmeticId = "buff-visual:" + donor.Id;
        entry.CosmeticName = skill.Name + ": " + donor.PickerName;
        entry.Mappings = checkedPlan.Mappings.ToList(); entry.Notes = checkedPlan.Limitations.Prepend("Skill: " + skill.Name).ToList(); entry.Enabled = true;
        _app.Profiles.SaveActive();
        _details.Text = "Added to the active set for " + skill.Name + ". The game files have not been changed yet.\n" + checkedPlan.Summary;
    }
}
