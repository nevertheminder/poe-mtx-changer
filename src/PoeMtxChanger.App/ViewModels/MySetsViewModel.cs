using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Profiles;

namespace PoeMtxChanger.App.ViewModels;

/// <summary>One equipment slot of the active profile.</summary>
public sealed partial class SlotRowViewModel : ObservableObject
{
    private readonly MySetsViewModel _owner;
    public string Key { get; }
    public string Name { get; }
    public CosmeticCategory Category { get; }
    public string IconKey => Ui.IconKey(Category);
    public string TargetHelp => Category is CosmeticCategory.Pet or CosmeticCategory.WingsAndBack or CosmeticCategory.CharacterEffect or CosmeticCategory.FootprintsAndTrails
        ? "Cosmetic already equipped (the object must exist)" : "What you wear now: a base item or a manual path";
    public ObservableCollection<CosmeticItem> Options { get; } = [];
    public ObservableCollection<string> SourceOptions { get; } = [];
    /// <summary>Base appearances from the game tables (e.g. the default portal, base armour pieces).</summary>
    public ObservableCollection<TargetSuggestion> Suggestions { get; } = [];
    public bool HasSuggestions => Suggestions.Count > 0;

    [ObservableProperty] private TargetSuggestion? _selectedSuggestion;
    [ObservableProperty] private CosmeticItem? _selectedCosmetic;
    [ObservableProperty] private string _targetPath = "";
    [ObservableProperty] private string? _selectedSource;
    [ObservableProperty] private string _statusText = "—";
    [ObservableProperty] private Brush _statusBrush = Ui.StatusBrush(CompatibilityStatus.Unknown);
    [ObservableProperty] private string? _statusDetail;
    [ObservableProperty] private string _mappingSummary = "";
    private bool _loading;

    /// <summary>Concrete replacements for this slot (one per character class when the data has per-class files).</summary>
    public IReadOnlyList<ReplacementMapping> Mappings { get; private set; } = [];
    public IReadOnlyList<string> Notes { get; private set; } = [];
    /// <summary>Manual mode: no base item picked, the user typed a target path.</summary>
    public bool IsManual => SelectedSuggestion is null;

    public SlotRowViewModel(MySetsViewModel owner, string key, string name, CosmeticCategory category)
    {
        _owner = owner;
        Key = key;
        Name = name;
        Category = category;
    }

    public void Load(ProfileEntry? entry, IEnumerable<CosmeticItem> catalog, IEnumerable<TargetSuggestion> suggestions)
    {
        _loading = true;
        Suggestions.Clear();
        var slotKey = Key == "weapon_off" ? "weapon_main" : Key;
        foreach (var s in suggestions.Where(s => s.SlotKey == slotKey || (Key == "weapon_off" && s.SlotKey == "shield"))) Suggestions.Add(s);
        OnPropertyChanged(nameof(HasSuggestions));
        SelectedSuggestion = null;
        Options.Clear();
        foreach (var c in catalog.Where(c => c.Category == Category && !c.IsWeaponEffect)) Options.Add(c);
        SelectedCosmetic = entry?.CosmeticId is { } id ? Options.FirstOrDefault(o => o.Id == id) : null;
        var m = entry?.Mappings.FirstOrDefault();
        SelectedSuggestion = m is null ? null : Suggestions.FirstOrDefault(s =>
            m.VisualIdentityEdit is { } visual && s.VisualIdentityRow == visual.Row
            || string.Equals(s.Path, m.TargetPath, StringComparison.OrdinalIgnoreCase)
            || s.ClassVariants.Values.Contains(m.TargetPath, StringComparer.OrdinalIgnoreCase)
            || s.ResourceVariants.Values.Contains(m.TargetPath, StringComparer.OrdinalIgnoreCase));
        TargetPath = SelectedSuggestion is null ? m?.TargetPath ?? "" : "";
        RefreshSources();
        SelectedSource = SelectedSuggestion is null ? m?.SourcePath : null;
        Mappings = entry?.Mappings.ToList() ?? [];
        Notes = entry?.Notes.ToList() ?? [];
        UpdateSummary();
        SetStatus(entry?.CosmeticName is { } n && SelectedCosmetic is null ? $"“{n}” is not in the current catalog" : null);
        _loading = false;
        OnPropertyChanged(nameof(IsManual));
    }

    /// <summary>Rebuilds <see cref="Mappings"/> from the current choices and saves them into the profile.</summary>
    private void Recompute()
    {
        if (_loading) return;
        if (SelectedCosmetic is null) { Mappings = []; Notes = []; }
        else if (SelectedSuggestion != null)
        {
            var r = SkinMappingResolver.Resolve(SelectedSuggestion, null, SelectedCosmetic, _owner.Resources);
            (Mappings, Notes) = (r.Mappings, r.Notes);
        }
        else if (!string.IsNullOrWhiteSpace(TargetPath) && SelectedSource != null)
        {
            Mappings = [new ReplacementMapping { TargetPath = TargetPath.Trim(), SourcePath = SelectedSource, SourceCosmeticId = SelectedCosmetic.Id }];
            Notes = [];
        }
        else if (!string.IsNullOrWhiteSpace(TargetPath))
        {
            var r = SkinMappingResolver.Resolve(null, TargetPath, SelectedCosmetic, _owner.Resources);
            (Mappings, Notes) = (r.Mappings, r.Notes);
        }
        else { Mappings = []; Notes = []; }
        UpdateSummary();
        _owner.Commit(this);
    }

    private void UpdateSummary()
    {
        MappingSummary = Mappings.Count switch
        {
            0 => "",
            1 => "1 replacement",
            var n when Mappings.All(x => x.Note != null) => $"{n} replacements (by class / variant)",
            var n => $"{n} replacements",
        };
    }

    private void RefreshSources()
    {
        SourceOptions.Clear();
        if (SelectedCosmetic is null) return;
        var ext = Path.GetExtension(TargetPath);
        foreach (var r in SelectedCosmetic.AllResources.Where(r => string.IsNullOrEmpty(ext) || string.Equals(Path.GetExtension(r), ext, StringComparison.OrdinalIgnoreCase)))
            SourceOptions.Add(r);
    }

    partial void OnSelectedCosmeticChanged(CosmeticItem? value)
    {
        RefreshSources();
        if (_loading) return;
        _loading = true;
        SelectedSource = value is null || string.IsNullOrEmpty(TargetPath) ? null : ProfileActions.MatchSource(value, TargetPath);
        _loading = false;
        Recompute();
    }

    partial void OnTargetPathChanged(string value)
    {
        if (_loading) return;
        _loading = true;
        if (!string.IsNullOrWhiteSpace(value)) SelectedSuggestion = null; // typing a path switches to manual mode
        RefreshSources();
        SelectedSource = SelectedCosmetic is null ? null : ProfileActions.MatchSource(SelectedCosmetic, value);
        _loading = false;
        OnPropertyChanged(nameof(IsManual));
        Recompute();
    }

    partial void OnSelectedSuggestionChanged(TargetSuggestion? value)
    {
        OnPropertyChanged(nameof(IsManual));
        if (_loading) return;
        if (value != null)
        {
            _loading = true;
            TargetPath = "";
            SelectedSource = null;
            _loading = false;
        }
        Recompute();
    }

    partial void OnSelectedSourceChanged(string? value) => Recompute();

    public void SetAppearance(CosmeticItem cosmetic, TargetSuggestion? target)
    {
        if (SelectedCosmetic == cosmetic && SelectedSuggestion == target) return;
        _loading = true;
        SelectedCosmetic = cosmetic;
        SelectedSuggestion = target;
        if (target != null) TargetPath = "";
        RefreshSources();
        SelectedSource = target == null ? ProfileActions.MatchSource(cosmetic, TargetPath) : null;
        _loading = false;
        Recompute();
    }

    public void SetStatus(string? note, ValidationReport? report = null)
    {
        if (report != null)
        {
            StatusText = Ui.Status(report.Status);
            StatusBrush = Ui.StatusBrush(report.Status);
            StatusDetail = string.Join("\n", report.Issues.Where(i => i.Severity != IssueSeverity.Info).Select(i => i.Message).Distinct().Take(6));
            if (string.IsNullOrEmpty(StatusDetail)) StatusDetail = "Static checks passed. Not checked in game.";
            return;
        }
        StatusBrush = Ui.StatusBrush(CompatibilityStatus.Unknown);
        StatusText = SelectedCosmetic is null ? "Empty"
            : SelectedSuggestion is null && string.IsNullOrEmpty(TargetPath) ? "Choose a base item"
            : Mappings.Count == 0 ? "No compatible files"
            : "Not checked";
        StatusDetail = string.Join("\n", new[] { note }.Concat(Notes).Where(s => !string.IsNullOrEmpty(s)));
        if (StatusDetail.Length == 0) StatusDetail = null;
    }

    [RelayCommand]
    private void Clear()
    {
        _loading = true;
        SelectedCosmetic = null;
        SelectedSuggestion = null;
        TargetPath = "";
        SelectedSource = null;
        _loading = false;
        Recompute();
    }
}


public sealed partial class MySetsViewModel : ObservableObject
{
    private readonly AppServices _app;
    private readonly ProfileActions _actions;

    public ObservableCollection<SlotRowViewModel> Slots { get; } = [];
    public ObservableCollection<SkillVisualDefinition> Skills { get; } = [];
    public ObservableCollection<SkillVisualVariant> SkillReplacements { get; } = [];
    public ObservableCollection<ProfileEntry> SkillEntries { get; } = [];

    [ObservableProperty] private int _section; // 0 = equipment, 1 = skill effects
    [ObservableProperty] private string _skillSearch = "";
    [ObservableProperty] private SkillVisualDefinition? _selectedSkill;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SkillDetails))]
    [NotifyPropertyChangedFor(nameof(SkillMappingLines))]
    private SkillVisualVariant? _selectedReplacement;
    [ObservableProperty] private string? _message;

    public string SkillDetails => SelectedReplacement?.Summary ?? "Choose a skill and a variant from the game tables.";
    public IEnumerable<string> SkillMappingLines => SelectedReplacement?.Mappings.Select(m => m.TargetPath + " ← " + m.SourcePath) ?? [];

    public ProfileSession Profiles => _app.Profiles;
    public string ProfileName => _app.Profiles.Active?.Name ?? "—";
    public bool HasArchive => _app.Game.Archive != null;
    /// <summary>Game resources used to resolve .ao → .sm when kinds differ.</summary>
    public Core.Storage.IResourceSource? Resources => _app.Game.Resources;

    public MySetsViewModel(AppServices app, ProfileActions actions)
    {
        _app = app;
        _actions = actions;
        foreach (var (key, _, cat) in CosmeticSlots.All) Slots.Add(new SlotRowViewModel(this, key, Ui.SlotName(key), cat));
        _app.Profiles.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProfileSession.Active)) Refresh(); };
        _app.Game.CatalogChanged += (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(Refresh);
    }

    public void Refresh()
    {
        var p = _app.Profiles.Active;
        foreach (var s in Slots)
            s.Load(p?.Entries.FirstOrDefault(e => e.Kind == ProfileEntryKind.EquipmentSlot && e.Key == s.Key), _app.Game.Catalog, _app.Game.Suggestions);
        SkillEntries.Clear();
        foreach (var e in p?.Entries.Where(e => e.Kind == ProfileEntryKind.SkillEffect) ?? []) SkillEntries.Add(e);
        RebuildSkills();
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(HasArchive));
    }

    /// <summary>Writes a slot row back into the active profile and saves it.</summary>
    public void Commit(SlotRowViewModel row)
    {
        var p = _app.Profiles.Active;
        if (p is null) return;
        var entry = p.GetOrAddEntry(ProfileEntryKind.EquipmentSlot, row.Key);
        entry.CosmeticId = row.SelectedCosmetic?.Id;
        entry.CosmeticName = row.SelectedCosmetic?.DisplayName;
        entry.Mappings.Clear();
        entry.Mappings.AddRange(row.Mappings);
        entry.Notes = row.Notes.ToList();
        if (entry.CosmeticId is null && entry.Mappings.Count == 0) p.Entries.Remove(entry);
        try { _app.Profiles.SaveActive(); }
        catch (ProfileImportException ex) { Message = ex.Message; }
        row.SetStatus(null);
    }

    private void RebuildSkills()
    {
        var selectedId = SelectedSkill?.Id;
        Skills.Clear();
        foreach (var skill in _app.Game.SkillVisuals.Where(skill => string.IsNullOrWhiteSpace(SkillSearch)
                     || skill.Name.Contains(SkillSearch, StringComparison.OrdinalIgnoreCase))) Skills.Add(skill);
        SelectedSkill = Skills.FirstOrDefault(skill => skill.Id == selectedId) ?? Skills.FirstOrDefault();
    }

    partial void OnSkillSearchChanged(string value) => RebuildSkills();

    partial void OnSelectedSkillChanged(SkillVisualDefinition? value)
    {
        SkillReplacements.Clear();
        foreach (var variant in value?.Variants ?? []) SkillReplacements.Add(variant);
        SelectedReplacement = SkillReplacements.FirstOrDefault(variant => variant.CanApply) ?? SkillReplacements.FirstOrDefault();
    }

    [RelayCommand]
    private void AddSkillMapping()
    {
        var p = _app.Profiles.Active;
        if (p is null || SelectedSkill is null || SelectedReplacement is not { CanApply: true } variant)
        {
            Message = "Choose a variant with unambiguous visual replacements. The reason it is unavailable is shown above the button.";
            return;
        }
        SaveSkillVariant(SelectedSkill, variant);
    }

    public void SaveSkillVariant(SkillVisualDefinition skill, SkillVisualVariant variant)
    {
        var p = _app.Profiles.Active;
        if (p is null || !variant.CanApply || !skill.Variants.Contains(variant)) return;
        var entry = p.GetOrAddEntry(ProfileEntryKind.SkillEffect, skill.Id);
        entry.CosmeticId = variant.Mappings[0].SourceCosmeticId;
        entry.CosmeticName = skill.Name + " — " + variant.PickerName;
        entry.Enabled = true;
        entry.Mappings.Clear();
        entry.Mappings.AddRange(variant.Mappings);
        entry.Notes = variant.Limitations.ToList();
        _app.Profiles.SaveActive();
        Message = $"Added: {entry.CosmeticName} ({variant.Mappings.Count} replacements). Not checked in game.";
        Refresh();
    }

    [RelayCommand]
    private void RemoveSkillEntry(ProfileEntry? entry)
    {
        if (entry is null || _app.Profiles.Active is not { } p) return;
        p.Entries.Remove(entry);
        _app.Profiles.SaveActive();
        Refresh();
    }

    [RelayCommand]
    private async Task Validate()
    {
        var p = _app.Profiles.Active;
        if (p is null) return;
        if (_app.Game.Resources is null) { Message = "The check needs a loaded index (Oodle is required)."; return; }
        await _app.Game.RunBusy("Checking dependencies…", async () =>
        {
            foreach (var row in Slots)
            {
                var entry = p.Entries.FirstOrDefault(e => e.Kind == ProfileEntryKind.EquipmentSlot && e.Key == row.Key);
                if (entry is null || entry.Mappings.Count == 0) { row.SetStatus(null); continue; }
                var report = await Task.Run(() => new MappingValidator(ResourcePolicy.Default).Validate(entry.Mappings, _app.Game.Resources!, _app.Settings.ValidateResources));
                row.SetStatus(null, report);
            }
            var all = await Task.Run(() => _actions.Validate(p));
            Message = all is { HasErrors: true }
                ? "The set is not ready:\n" + string.Join("\n", all.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message).Distinct().Take(5))
                : "All enabled replacements passed the static checks. Not checked in game yet.";
        });
    }

    [RelayCommand] private Task Apply() => _actions.ApplyActiveAsync();
    [RelayCommand] private Task Restore() => _actions.RestoreAsync();
}
