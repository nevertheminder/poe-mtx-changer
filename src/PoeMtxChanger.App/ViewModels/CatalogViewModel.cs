using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Profiles;
using PoeMtxChanger.Core.Resources;

namespace PoeMtxChanger.App.ViewModels;

public sealed record NavCategory(string Title, string IconKey, Func<CosmeticItem, bool> Match);

public sealed record ChecklistRow(string Text, bool Ok);

public sealed partial class CatalogItemViewModel(CosmeticItem item, bool favorite) : ObservableObject
{
    public CosmeticItem Item { get; } = item;
    public string Name => Item.DisplayName;
    public string Subtitle => Ui.Category(Item.Category);
    public string StatusText => Ui.Status(Item.Status);
    public Brush StatusBrush => Ui.StatusBrush(Item.Status);
    public string IconKey => Ui.IconKey(Item.Category);
    public Color GlowColor => Ui.Tint(Item.Category).Glow;
    public Color CoreColor => Ui.Tint(Item.Category).Core;
    public bool IsMock => Item.IsMock;

    [ObservableProperty] private bool _isFavorite = favorite;
    [ObservableProperty] private bool _isAssigned;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGlyph))]
    private ImageSource? _thumbnail;
    private bool _thumbRequested;

    /// <summary>Category glyph is drawn when there is no real icon (mock icons are abstract, so the glyph stays on top).</summary>
    public bool ShowGlyph => Thumbnail is null || IsMock;

    /// <summary>Loads the icon from the installation (or the procedural mock icons) once, in the background.</summary>
    public async void EnsureThumbnail(ThumbnailService service)
    {
        if (_thumbRequested || Item.IconPath is null) return;
        _thumbRequested = true;
        try { Thumbnail = await service.GetAsync(Item.IconPath); }
        catch (Exception) { Thumbnail = null; } // never let a broken icon take the UI down
    }
}

public sealed partial class CatalogViewModel : ObservableObject
{
    private readonly AppServices _app;
    private readonly ProfileActions _actions;
    private readonly ObservableCollection<CatalogItemViewModel> _items = [];

    /// <summary>
    /// The sections of the official shop (pathofexile.com/shop) that hold visual items, with the game's MTX types
    /// sorted as the shop sorts them. Account-only sections (stash tabs, guild, account features, bundles) and
    /// hideout decorations (no swappable resources) are left out.
    /// </summary>
    public IReadOnlyList<NavCategory> Categories { get; } =
    [
        new("Armour", "I.Armour", i => i.Category is CosmeticCategory.BodyArmour or CosmeticCategory.Helmet or CosmeticCategory.Gloves
            or CosmeticCategory.Boots or CosmeticCategory.Shield && i.MtxType != "Boots Modifier"),
        new("Back", "I.Wings", i => i.Category == CosmeticCategory.WingsAndBack),
        new("Weapons", "I.Weapon", i => i.Category == CosmeticCategory.Weapon || i.MtxType is "Quiver Skin" or "Staff Skin" or "Item Skin"),
        new("Finishers", "I.Emote", i => i.MtxType == "Finisher Effect"),
        new("Portals", "I.Portal", i => i.Category == CosmeticCategory.Portal),
        new("Skill effects", "I.Skill", i => i.Category is CosmeticCategory.SkillEffect or CosmeticCategory.AuraAndHerald
            && !IsCharacterType(i.MtxType) && i.MtxType is not ("Quiver Skin" or "Staff Skin" or "Item Skin" or "Cursor Skin")
            && !(i.MtxType ?? "").Contains("Portrait", StringComparison.Ordinal)),
        new("Character", "I.Aura", i => i.Category == CosmeticCategory.CharacterEffect && i.MtxType != "Finisher Effect" || IsCharacterType(i.MtxType)
            || i.Category == CosmeticCategory.Other && i.RootPath.Contains("emote", StringComparison.OrdinalIgnoreCase)),
        new("Footprints", "I.Trail", i => i.Category == CosmeticCategory.FootprintsAndTrails || i.MtxType == "Boots Modifier"),
        new("Pets", "I.Pet", i => i.Category == CosmeticCategory.Pet || i.MtxType == "Rhoa Mount Effect"),
        new("Sounds (β)", "I.Sound", i => i.Category == CosmeticCategory.SkillAudio || i.HasAudio),
    ];

    /// <summary>Types the shop lists under Character: jewellery, charges, flasks, transforms, level-up and chest extras.</summary>
    private static bool IsCharacterType(string? mtxType) => mtxType is "Ring Skin" or "Amulet Skin" or "Amulet Effect" or "Hip Attachment"
        or "Chest Extra Effect" or "Dodge Roll Effect" or "Charge Skin" or "Character Skin" or "Polymorph"
        || mtxType?.EndsWith("Flask Skin", StringComparison.Ordinal) == true;

    public IReadOnlyList<string> Filters { get; } = ["All categories", "Favourites", "Supported", "Experimental", "Not supported", "Other (no category)"];

    /// <summary>Filtered items shown in the grid (capped: WPF wrap panels do not virtualize).</summary>
    public ObservableCollection<CatalogItemViewModel> VisibleItems { get; } = [];
    public const int MaxVisible = 400;
    public string CountText => VisibleCount > MaxVisible ? $"Showing {MaxVisible} of {VisibleCount}, refine the search" : $"{VisibleCount} items";

    [ObservableProperty] private NavCategory? _selectedCategory;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _selectedFilter = "All categories";
    [ObservableProperty] private CatalogItemViewModel? _selectedItem;
    [ObservableProperty] private IReadOnlyList<ChecklistRow> _checklist = [];
    [ObservableProperty] private string? _dependencySummary;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private int _visibleCount;

    public GameSession Game => _app.Game;
    public bool HasItems => _items.Count > 0;

    public string EmptyText => _app.Game.Installation is null
        ? "Path of Exile installation not found. Set the path in “Settings”."
        : !_app.Game.Codec.IsAvailable
            ? "To build the catalog from your installation, the Oodle library (oo2core_*_win64.dll) is required. Set it in “Settings”. The program does not include Oodle."
            : _app.Game.ArchiveError ?? "No cosmetics found in the installation with the current rules.";

    public CatalogViewModel(AppServices app, ProfileActions actions)
    {
        _app = app;
        _actions = actions;
        _app.Game.CatalogChanged += (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(Rebuild);
        _app.Game.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GameSession.Installation) or nameof(GameSession.Codec) or nameof(GameSession.ArchiveError))
                System.Windows.Application.Current.Dispatcher.Invoke(() => OnPropertyChanged(nameof(EmptyText)));
        };
        _app.Profiles.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProfileSession.Active)) RefreshAssigned(); };
        _selectedCategory = null;
    }

    private void Rebuild()
    {
        _items.Clear();
        foreach (var item in _app.Game.Catalog)
            _items.Add(new CatalogItemViewModel(item, _app.Settings.FavoriteCosmeticIds.Contains(item.Id)));
        RefreshAssigned();
        Refilter();
        SelectedItem = VisibleItems.FirstOrDefault(v => v.IsAssigned) ?? VisibleItems.FirstOrDefault();
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(EmptyText));
    }

    public void RefreshAssigned()
    {
        var ids = _app.Profiles.Active?.Entries.Where(e => e.Enabled).Select(e => e.CosmeticId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var vm in _items) vm.IsAssigned = ids.Contains(vm.Item.Id);
    }

    private bool Matches(CatalogItemViewModel vm)
    {
        var i = vm.Item;
        if (SelectedCategory != null && !SelectedCategory.Match(i)) return false;
        if (!string.IsNullOrWhiteSpace(SearchText)
            && !i.DisplayName.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            && !i.RootPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase)) return false;
        return SelectedFilter switch
        {
            "Favourites" => vm.IsFavorite,
            "Supported" => i.Status == CompatibilityStatus.Supported,
            "Experimental" => i.Status == CompatibilityStatus.Experimental,
            "Not supported" => i.Status == CompatibilityStatus.Unsupported,
            "Other (no category)" => i.Category == CosmeticCategory.Other,
            // Default view: wearable cosmetics; uncategorised entries (consumables, frames, hideout items) only via "Other".
            _ => i.Category != CosmeticCategory.Other || i.IsMock,
        };
    }

    private void Refilter()
    {
        var matches = _items.Where(Matches)
            .OrderBy(v => v.Item.Status == CompatibilityStatus.Unsupported) // items with resolved files first
            .ThenBy(v => v.Item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        VisibleItems.Clear();
        foreach (var vm in matches.Take(MaxVisible))
        {
            VisibleItems.Add(vm);
            vm.EnsureThumbnail(_app.Thumbnails);
        }
        VisibleCount = matches.Count;
        OnPropertyChanged(nameof(CountText));
    }

    partial void OnSelectedCategoryChanged(NavCategory? value) => Refilter();
    partial void OnSearchTextChanged(string value) => Refilter();
    partial void OnSelectedFilterChanged(string value) => Refilter();

    partial void OnSelectedItemChanged(CatalogItemViewModel? value)
    {
        DependencySummary = null;
        StatusMessage = null;
        Checklist = value is null ? [] : BuildChecklist(value.Item);
    }

    private static IReadOnlyList<ChecklistRow> BuildChecklist(CosmeticItem item)
    {
        var kinds = item.AllResources.Select(r => ResourceClassifier.Classify(r).Kind).ToHashSet();
        return
        [
            new("Entry point (.aoc/.ao/.epk)", item.PrimaryResources.Count > 0),
            new(item.ClassVariants.Count > 0 ? $"Class variants ({item.ClassVariants.Count})" : "Class variants", item.ClassVariants.Count > 0),
            new("Models", kinds.Contains(ResourceKind.SkinnedMesh) || kinds.Contains(ResourceKind.MeshData) || kinds.Contains(ResourceKind.FixedMesh) || item.ClassVariants.Count > 0),
            new("Animations", kinds.Contains(ResourceKind.Animation)),
            new("Materials", kinds.Contains(ResourceKind.Material)),
            new("Textures", kinds.Contains(ResourceKind.Texture)),
            new("Particles and effects", item.HasVisualEffects),
            new("Sounds (info only)", item.HasAudio),
        ];
    }

    [RelayCommand]
    private void SelectCategory(NavCategory? category) => SelectedCategory = SelectedCategory == category ? null : category;

    [RelayCommand]
    private void ToggleFavorite(CatalogItemViewModel? vm)
    {
        if (vm is null) return;
        vm.IsFavorite = !vm.IsFavorite;
        if (vm.IsFavorite) _app.Settings.FavoriteCosmeticIds.Add(vm.Item.Id);
        else _app.Settings.FavoriteCosmeticIds.Remove(vm.Item.Id);
        _app.SaveSettings();
        if (SelectedFilter == "Favourites") Refilter();
    }

    [RelayCommand]
    private void AddToSet()
    {
        if (SelectedItem is null) return;
        StatusMessage = _actions.AddToActiveProfile(SelectedItem.Item);
        RefreshAssigned();
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (SelectedItem is null) return;
        if (SelectedItem.IsMock)
        {
            _app.Dialogs.Info("Demo data", "This is a MOCK item for testing the interface. It does not exist in your installation and cannot be applied.");
            return;
        }
        StatusMessage = _actions.AddToActiveProfile(SelectedItem.Item);
        RefreshAssigned();
        if (SelectedItem.Item.IsWeaponEffect) return;
        var slot = Ui.SlotFor(SelectedItem.Item.Category);
        var entry = slot is null ? null : _app.Profiles.Active?.Entries.FirstOrDefault(e => e.Kind == ProfileEntryKind.EquipmentSlot && e.Key == slot);
        if (entry is null || entry.Mappings.Count == 0) return; // message already explains what is missing
        await _actions.ApplyActiveAsync();
    }

    [RelayCommand]
    private async Task AnalyzeDependencies()
    {
        if (SelectedItem is null || _app.Game.Resources is not { } src || SelectedItem.Item.PrimaryResources.Count == 0)
        {
            DependencySummary = SelectedItem?.IsMock == true ? "MOCK: dependency analysis is unavailable." : "A loaded index (Oodle) is required.";
            return;
        }
        var root = SelectedItem.Item.PrimaryResources[0];
        var report = await Task.Run(() => new DependencyAnalyzer(src).Analyze(root));
        var missing = report.Missing.ToList();
        DependencySummary = report.ReadErrors.Count > 0 ? "Could not read dependencies: " + string.Join("; ", report.ReadErrors.Take(2))
            : missing.Count == 0
            ? $"Dependencies resolved: {report.Nodes.Count} resources{(report.Truncated ? " (analysis truncated)" : "")}."
            : $"Not found {missing.Count} of {report.Nodes.Count}: {string.Join(", ", missing.Take(3))}{(missing.Count > 3 ? "…" : "")}";
    }

    [RelayCommand]
    private void LoadMock() => _app.Game.LoadMockCatalog();

    [RelayCommand]
    private async Task Reload() => await _app.Game.RunBusy("Reading the resource index…", _app.Game.LoadArchiveAsync);
}
