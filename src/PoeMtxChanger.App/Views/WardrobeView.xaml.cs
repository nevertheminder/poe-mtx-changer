using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.App.ViewModels;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Profiles;

namespace PoeMtxChanger.App.Views;

public partial class WardrobeView : UserControl
{
    private sealed record Gem(string Id,string Name,SkillVisualDefinition? Skill,BuffSkillDefinition? Buff)
    {
        public string? IconPath => Skill?.IconPath ?? Buff?.IconPath;
    }

    private static readonly string[][] EquipmentLayout =
    [
        ["weapon_main","helmet","back","weapon_off"],
        ["weapon_main","body","character_effect","weapon_off"],
        ["effect_main","gloves","boots","effect_off"],
        ["shield","shield","footprints","footprints"],
        ["portal","portal","pet","pet"],
        ["helmet_attachment","helmet_attachment","helmet_attachment","helmet_attachment"]
    ];

    private MainViewModel? _model;
    private bool _settingStance;
    private MainViewModel Model => _model!;
    private AppServices AppServices => Model.Services;
    internal Task PreviewReady => (PreviewHost.Content as ModelPreviewPanel)?.Ready ?? Task.CompletedTask;

    public WardrobeView()
    {
        InitializeComponent();
        StancePicker.ItemsSource=WeaponVisuals.Stances;
        Loaded += (_,_) =>
        {
            if (DataContext is not MainViewModel model) return;
            _model = model;
            model.Game.CatalogChanged += CatalogChanged;
            model.Profiles.PropertyChanged += ProfileChanged;
            Refresh();
        };
        Unloaded += (_,_) =>
        {
            if (_model is not { } model) return;
            model.Game.CatalogChanged -= CatalogChanged;
            model.Profiles.PropertyChanged -= ProfileChanged;
            _model = null;
            PreviewHost.Content = null;
        };
        IsVisibleChanged += (_,_) => Refresh();
    }

    private void CatalogChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        PreviewHost.Content = null;
        Refresh();
    });

    private void ProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileSession.Active)) Refresh(reloadPreview:true);
    }

    private void Refresh(bool reloadPreview = false)
    {
        if (_model == null || !IsVisible)
        {
            PreviewHost.Content = null;
            return;
        }
        Model.MySets.Refresh();
        bool hasArchive = Model.Game.Archive != null && !Model.Game.CatalogIsMock;
        PreviewHint.Visibility = hasArchive ? Visibility.Collapsed : Visibility.Visible;
        if (!hasArchive) PreviewHost.Content = null;
        else if (PreviewHost.Content is not ModelPreviewPanel)
        {
            var panel=new ModelPreviewPanel(AppServices,null,compact:true);
            panel.CharacterChanged+=(_,_)=>RenderSlots();
            PreviewHost.Content=panel;
        }
        else if (reloadPreview && PreviewHost.Content is ModelPreviewPanel preview) preview.RefreshScene();
        RenderSlots();
        RenderGems();
    }

    private string Character => (PreviewHost.Content as ModelPreviewPanel)?.Character ?? "Marauder";

    private (WeaponStance Stance,TargetSuggestion? Main,TargetSuggestion? Off) CurrentWeapons()
    {
        var setup=Model.Profiles.Active?.WeaponSetup;
        var stance=WeaponVisuals.GetStance(Character,setup?.StanceId);
        var main=WeaponVisuals.BaseItem(Model.Game.Suggestions,stance.MainClass,setup?.MainBasePath);
        var off=WeaponVisuals.BaseItem(Model.Game.Suggestions,stance.OffClass,setup?.OffBasePath,main?.Path);
        return(stance,main,off);
    }

    private void ChangeStance(object sender,SelectionChangedEventArgs e)
    {
        if(_settingStance||_model?.Profiles.Active is not {} profile||StancePicker.SelectedItem is not WeaponStance stance)return;
        profile.WeaponSetup.StanceId=stance.Id;
        AppServices.Profiles.SaveActive();
        Refresh(reloadPreview:true);
    }

    private void RenderSlots()
    {
        var (stance,main,off)=CurrentWeapons();
        _settingStance=true;
        StancePicker.SelectedItem=stance;
        StancePicker.IsEnabled=Model.Profiles.Active!=null;
        _settingStance=false;
        EquipmentSlots.Children.Clear();
        for (int row = 0; row < EquipmentLayout.Length; row++)
            for (int column = 0; column < EquipmentLayout[row].Length; column++)
            {
                string key = EquipmentLayout[row][column];
                if (row > 0 && EquipmentLayout[row-1][column] == key || column > 0 && EquipmentLayout[row][column-1] == key) continue;
                var button = key switch
                {
                    "weapon_main"=>WeaponTile(false,main),
                    "weapon_off"=>WeaponTile(true,off),
                    "effect_main"=>EffectTile(false,main),
                    "effect_off"=>EffectTile(true,off),
                    "helmet_attachment"=>HelmetAttachmentTile(),
                    _=>EquipmentTile(key)
                };
                button.Tag = key;
                Grid.SetRow(button,row); Grid.SetColumn(button,column);
                if (row+1 < EquipmentLayout.Length && EquipmentLayout[row+1][column] == key) Grid.SetRowSpan(button,2);
                if (key=="helmet_attachment") Grid.SetColumnSpan(button,4);
                else if (column+1 < EquipmentLayout[row].Length && EquipmentLayout[row][column+1] == key) Grid.SetColumnSpan(button,2);
                EquipmentSlots.Children.Add(button);
            }
    }

    private Button EquipmentTile(string key)
    {
        var slot=Model.MySets.Slots.Single(s=>s.Key==key);
        var saved=Model.Profiles.Active?.Entries.FirstOrDefault(e=>e.Kind==ProfileEntryKind.EquipmentSlot&&e.Key==key);
        var button=Tile(slot.Name,slot.SelectedCosmetic?.DisplayName??saved?.CosmeticName??"Choose look",slot.SelectedCosmetic?.IconPath,slot.IconKey);
        button.Click+=(_,_)=>EditSlot(slot);
        if(slot.SelectedCosmetic is {} item)AddTierSwitch(button,item);
        if(slot.SelectedCosmetic!=null||saved!=null)
            AddRemove(button,"Clear slot",()=>{slot.ClearCommand.Execute(null);Model.Profiles.Active?.Entries.RemoveAll(e=>e.Kind==ProfileEntryKind.EquipmentSlot&&e.Key==key);});
        return button;
    }

    /// <summary>
    /// Tiered microtransactions (several looks unlocked in game, e.g. Lord of Ogham's black feathers and red cloth):
    /// a small switch on the tile cycles the look shown and saved for the profile.
    /// </summary>
    private void AddTierSwitch(Button tile,CosmeticItem item)
    {
        if(Model.Game.Resources is not {} source||Model.Profiles.Active is not {} profile)return;
        int[] tiers;
        try { tiers=ModelPreviewLoader.VisualTiers(source,item); }
        catch(Exception ex)when(ex is IOException or InvalidDataException) { return; }
        if(tiers.Length<2||tile.Content is not DockPanel content)return;
        int current=profile.VisualTiers.TryGetValue(item.Id,out int saved)&&tiers.Contains(saved)?saved:tiers[^1];
        var toggle=new Button
        {
            Content=$"Variant {current}",FontSize=10,Padding=new Thickness(6,1,6,1),Margin=new Thickness(6,0,0,0),
            VerticalAlignment=VerticalAlignment.Center,
            ToolTip=$"The item has {tiers.Length} looks, as in game. Click to switch."
        };
        toggle.Click+=(_,e)=>
        {
            e.Handled=true;
            int next=tiers[(Array.IndexOf(tiers,current)+1)%tiers.Length];
            profile.VisualTiers[item.Id]=next;
            AppServices.Profiles.SaveActive();
            Refresh(reloadPreview:true);
        };
        DockPanel.SetDock(toggle,Dock.Right);
        content.Children.Insert(0,toggle);
    }

    private Button HelmetAttachmentTile()
    {
        const string slot="helmet_attachment";
        const string title="Helmet attachment";
        var profile=Model.Profiles.Active;
        var entry=profile?.Entries.FirstOrDefault(e=>e.Kind==ProfileEntryKind.EquipmentSlot&&e.Key==slot);
        var current=Model.Game.Catalog.FirstOrDefault(i=>i.Id==entry?.CosmeticId);
        var tile=Tile(title,current?.DisplayName??"Choose attachment",current?.IconPath,Ui.IconKey(CosmeticCategory.Helmet));
        tile.IsEnabled=profile!=null;
        if(profile!=null&&entry!=null)
            AddRemove(tile,"Remove attachment",()=>profile.Entries.RemoveAll(e=>e.Kind==ProfileEntryKind.EquipmentSlot&&e.Key==slot));
        tile.Click+=(_,_)=>
        {
            if(profile==null)return;
            var choices=Model.Game.Catalog.Where(i=>i.MtxType=="Helmet Attachment"&&i.PrimaryResources.Count>0)
                .Select(i=>new WardrobeChoice(i.DisplayName,i,i.IconPath,i.StatusReason)).ToArray();
            var picker=new WardrobePicker(title,AppServices.Thumbnails,choices,(_,_) => (true,"Show over the helmet in the wardrobe."),
                current:current,resetLabel:"Clear slot"){Owner=Window.GetWindow(this)};
            if(picker.ShowDialog()!=true)return;
            if(picker.ResetRequested)profile.Entries.RemoveAll(e=>e.Kind==ProfileEntryKind.EquipmentSlot&&e.Key==slot);
            else if(picker.Value is CosmeticItem item)
            {
                var selected=profile.GetOrAddEntry(ProfileEntryKind.EquipmentSlot,slot);
                selected.Enabled=true;
                selected.CosmeticId=item.Id;
                selected.CosmeticName=item.DisplayName;
            }
            AppServices.Profiles.SaveActive();
            Refresh(reloadPreview:true);
        };
        return tile;
    }

    private Button WeaponTile(bool secondary,TargetSuggestion? weapon)
    {
        var slot=Model.MySets.Slots.Single(s=>s.Key==(secondary?(weapon?.SlotKey=="shield"?"shield":"weapon_off"):"weapon_main"));
        var skin=slot.SelectedCosmetic;
        bool supported=weapon!=null&&skin!=null&&WeaponVisuals.SkinPath(skin,weapon,Character)!=null;
        string title=secondary?"Off hand · skin":"Main hand · skin";
        string sub=weapon==null?"Empty":supported?skin!.DisplayName:weapon.Label;
        var button=Tile(title,sub,supported?skin!.IconPath:weapon?.IconPath,slot.IconKey);
        button.IsEnabled=weapon!=null;
        if(weapon!=null)
        {
            button.ToolTip=$"{title}\nBase weapon: {weapon.Label}\n{sub}";
            if(slot.Mappings.Count>0&&slot.SelectedSuggestion is {} savedTarget&&!savedTarget.Path.Equals(weapon.Path,StringComparison.OrdinalIgnoreCase))
                button.ToolTip+="\nThe profile plan is saved for "+savedTarget.Label+". Open the slot and save the skin to change the target.";
            button.Click+=(_,_)=>EditWeaponSkin(slot,weapon,secondary);
            if(skin!=null)AddRemove(button,"Default weapon look",()=>slot.ClearCommand.Execute(null));
        }
        return button;
    }

    private Button EffectTile(bool secondary,TargetSuggestion? weapon)
    {
        var setup=Model.Profiles.Active?.WeaponSetup;
        string? selected=secondary?setup?.OffEffectId:setup?.MainEffectId;
        var effect=Model.Game.Catalog.FirstOrDefault(i=>i.Id==selected&&WeaponVisuals.EffectPath(i,weapon?.ItemClass)!=null);
        var button=Tile("Weapon effect",effect?.DisplayName??(weapon==null?"No weapon":"Choose effect"),
            effect?.IconPath,"I.Skill");
        button.IsEnabled=weapon!=null;
        if(weapon!=null)button.Click+=(_,_)=>EditWeaponEffect(weapon,secondary);
        if(setup!=null&&selected!=null)
            AddRemove(button,"Remove weapon effect",()=>
            {
                if(secondary){setup.OffEffectId=null;setup.OffEffectTargetId=null;}
                else {setup.MainEffectId=null;setup.MainEffectTargetId=null;}
                string slotKey=secondary?"weapon_effect_off":"weapon_effect_main";
                Model.Profiles.Active?.Entries.RemoveAll(e=>e.Kind==ProfileEntryKind.EquipmentSlot&&e.Key==slotKey);
            });
        return button;
    }

    private void EditWeaponSkin(SlotRowViewModel slot,TargetSuggestion weapon,bool secondary)
    {
        if(Model.Profiles.Active is not {} profile)return;
        var stance=CurrentWeapons().Stance;
        var targets=Model.Game.Suggestions.Where(t=>t.SlotKey==weapon.SlotKey&&WeaponVisuals.MatchesClass(t.ItemClass,secondary?stance.OffClass:stance.MainClass)
            &&Path.GetExtension(t.Path).Equals(".ao",StringComparison.OrdinalIgnoreCase)).ToArray();
        var choices=slot.Options.Where(c=>!c.IsWeaponEffect)
            .Select(c=>new WardrobeChoice(c.DisplayName,c,c.IconPath,c.StatusReason)).ToArray();
        var picker=new WardrobePicker(slot.Name,AppServices.Thumbnails,choices,
            (value,target)=>DescribeEquipment(slot,(CosmeticItem)value,target),
            targets,weapon,slot.SelectedCosmetic,resetLabel:"Default look",
            matchesTarget:(choice,target)=>target!=null&&WeaponVisuals.SkinPath((CosmeticItem)choice.Value,target,Character)!=null)
            {Owner=Window.GetWindow(this)};
        if(picker.ShowDialog()!=true||picker.Target is not {} target)return;
        if(secondary)profile.WeaponSetup.OffBasePath=target.Path;
        else profile.WeaponSetup.MainBasePath=target.Path;
        if(picker.ResetRequested)slot.ClearCommand.Execute(null);
        else if(picker.Value is CosmeticItem cosmetic)slot.SetAppearance(cosmetic,target);
        AppServices.Profiles.SaveActive();
        Refresh(reloadPreview:true);
    }

    private void EditWeaponEffect(TargetSuggestion weapon,bool secondary)
    {
        if(Model.Profiles.Active is not {} profile)return;
        string slot=secondary?"weapon_effect_off":"weapon_effect_main";
        var setup=profile.WeaponSetup;
        var effects=Model.Game.Catalog.Where(c=>WeaponVisuals.EffectPath(c,weapon.ItemClass)!=null).ToArray();
        var choices=effects.Select(c=>new WardrobeChoice(c.DisplayName,c,c.IconPath,c.StatusReason)).ToArray();
        const string preview="preview";
        var targets=new[]{new TargetSuggestion(slot,"No original effect · try-on",preview,"preview")}
            .Concat(effects.Select(c=>new TargetSuggestion(slot,c.DisplayName,c.Id,"equipped-effect"))).ToArray();
        string? targetId=secondary?setup.OffEffectTargetId:setup.MainEffectTargetId;
        string? effectId=secondary?setup.OffEffectId:setup.MainEffectId;
        CosmeticItem? Equipped(TargetSuggestion? target)=>effects.FirstOrDefault(c=>c.Id==target?.Path);
        var picker=new WardrobePicker("Weapon effect · "+weapon.Label,AppServices.Thumbnails,choices,
            (value,target)=>
            {
                if(target?.Path==preview)return (true,"The effect will appear in 3D. To add the replacement to the profile, choose the effect already equipped.");
                return WeaponVisuals.ReplaceEffect(Equipped(target),(CosmeticItem)value,weapon)!=null
                    ? (true,"The effect will be added to the replacement plan. The weapon skin is kept.")
                    : (false,"Choose another effect to replace.");
            },
            targets,targets.FirstOrDefault(t=>t.Path==targetId)??targets[0],effects.FirstOrDefault(c=>c.Id==effectId),
            resetLabel:"Remove effect",targetLabel:"Effect already equipped in game")
            {Owner=Window.GetWindow(this)};
        if(picker.ShowDialog()!=true)return;
        var selected=picker.ResetRequested?null:picker.Value as CosmeticItem;
        var equipped=Equipped(picker.Target);
        if(secondary){setup.OffEffectId=selected?.Id;setup.OffEffectTargetId=selected==null?null:equipped?.Id;}
        else {setup.MainEffectId=selected?.Id;setup.MainEffectTargetId=selected==null?null:equipped?.Id;}
        profile.Entries.RemoveAll(e=>e.Kind==ProfileEntryKind.EquipmentSlot&&e.Key==slot);
        if(selected!=null&&WeaponVisuals.ReplaceEffect(equipped,selected,weapon) is {} mapping)
        {
            var entry=profile.GetOrAddEntry(ProfileEntryKind.EquipmentSlot,slot);
            entry.CosmeticId=selected.Id;entry.CosmeticName=selected.DisplayName;
            entry.Mappings.Add(mapping);
            entry.Notes.Add("The replacement affects every use of the original effect for this weapon type.");
        }
        AppServices.Profiles.SaveActive();
        Refresh(reloadPreview:true);
    }

    private void EditSlot(SlotRowViewModel slot)
    {
        var choices = slot.Options.Where(c=>slot.Key!="helmet"||c.MtxType!="Helmet Attachment")
            .OrderByDescending(c => c.Status != CompatibilityStatus.Unsupported)
            .ThenBy(c => c.DisplayName,StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new WardrobeChoice(c.DisplayName,c,c.IconPath,c.StatusReason)).ToArray();
        var picker = new WardrobePicker(slot.Name,AppServices.Thumbnails,choices,
            (value,target) => DescribeEquipment(slot,(CosmeticItem)value,target),
            slot.Suggestions.ToArray(),slot.SelectedSuggestion,slot.SelectedCosmetic,
            resetLabel:"Clear slot", matchesTarget:(choice,target) => target?.ItemClass is not { } itemClass
                || ((CosmeticItem)choice.Value).AllowedItemClasses is { Count: 0 }
                || ((CosmeticItem)choice.Value).AllowedItemClasses.Contains(itemClass,StringComparer.OrdinalIgnoreCase))
            { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true) return;
        if (picker.ResetRequested) slot.ClearCommand.Execute(null);
        else if (picker.Value is CosmeticItem cosmetic) slot.SetAppearance(cosmetic,picker.Target);
        Refresh(reloadPreview:true);
    }

    private (bool Allowed,string Details) DescribeEquipment(SlotRowViewModel slot,CosmeticItem cosmetic,TargetSuggestion? target)
    {
        if (target == null && string.IsNullOrWhiteSpace(slot.TargetPath))
            return (true,"Can be saved for the try-on. To apply, choose a base item.");
        var plan = SkinMappingResolver.Resolve(target,target == null ? slot.TargetPath : null,cosmetic,Model.Game.Resources);
        if (plan.Mappings.Count > 0) return (true,$"Resource replacements: {plan.Mappings.Count}.");
        return (false,plan.Notes.FirstOrDefault() ?? "The selected item has no compatible replacement.");
    }

    private Dictionary<string,Gem> AvailableGems()
    {
        var skills = Model.Game.SkillVisuals.DistinctBy(s=>s.Id,StringComparer.OrdinalIgnoreCase).ToDictionary(s => s.Id,StringComparer.OrdinalIgnoreCase);
        var buffs = Model.Game.BuffSkills.DistinctBy(s=>s.Id,StringComparer.OrdinalIgnoreCase).ToDictionary(s => s.Id,StringComparer.OrdinalIgnoreCase);
        return skills.Keys.Union(buffs.Keys,StringComparer.OrdinalIgnoreCase).ToDictionary(id => id,id =>
        {
            var skill = skills.GetValueOrDefault(id);
            var buff = buffs.GetValueOrDefault(id);
            string name = skill?.Name ?? buff?.Name ?? id;
            return new Gem(id,name.Split(" · ",2)[0],skill,buff);
        },StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsEffectOf(ProfileEntry entry,string gemId) =>
        entry.Kind == ProfileEntryKind.SkillEffect && (string.Equals(entry.Key,gemId,StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.BuffSelection?.SkillId,gemId,StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> GemIds(CosmeticProfile profile) => profile.WardrobeSkills.Concat(
        profile.Entries.Where(e => e.Kind == ProfileEntryKind.SkillEffect &&
            (e.BuffSelection != null || !e.Key.StartsWith("buff-visual:",StringComparison.OrdinalIgnoreCase)))
            .Select(e => e.BuffSelection?.SkillId ?? e.Key)).Distinct(StringComparer.OrdinalIgnoreCase);

    private void RenderGems()
    {
        GemBar.Children.Clear();
        var available = AvailableGems();
        AddGemButton.IsEnabled = available.Count > 0 && Model.Profiles.Active != null;
        if (Model.Profiles.Active is not { } profile) return;
        foreach (string id in GemIds(profile))
        {
            var gem = available.GetValueOrDefault(id) ?? new Gem(id,id,null,null);
            var effects = profile.Entries.Where(e => e.Enabled && IsEffectOf(e,id)).ToArray();
            string hint = effects.Length == 0 ? "Choose effect" : "Effect in profile";
            var button = Tile(gem.Name,hint,gem.IconPath,"I.Skill");
            button.Width = 140;
            button.Tag = gem.Id;
            if (effects.Length > 0) button.ToolTip = string.Join("\n",effects.Select(e=>e.CosmeticName));
            button.Click += (_,_) => EditGem(gem);
            AddRemove(button,"Remove the gem and its replacements",() =>
            {
                profile.Entries.RemoveAll(e=>IsEffectOf(e,id));
                profile.WardrobeSkills.RemoveAll(s=>s.Equals(id,StringComparison.OrdinalIgnoreCase));
            });
            GemBar.Children.Add(button);
        }
        if (GemBar.Children.Count == 0)
            GemBar.Children.Add(new TextBlock { Text = "Add the gems you use with the button on the right.", VerticalAlignment = VerticalAlignment.Center });
    }

    private void AddGem(object sender,RoutedEventArgs e)
    {
        if (Model.Profiles.Active is not { } profile) return;
        var existing = GemIds(profile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Only player gems: entries without a displayed name or gem icon are monster, sound or test skills.
        var choices = AvailableGems().Values.Where(g => !existing.Contains(g.Id) && g.Name != g.Id && !string.IsNullOrWhiteSpace(g.IconPath))
            .OrderBy(g=>g.Name,StringComparer.CurrentCultureIgnoreCase)
            .Select(g=>new WardrobeChoice(g.Name,g,g.IconPath,g.Id)).ToArray();
        var picker = new WardrobePicker("Add gem",AppServices.Thumbnails,choices,
            (_,_) => (true,"The gem will appear in the bottom row of the active profile."),saveLabel:"Add gem")
            { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.Value is not Gem gem) return;
        profile.WardrobeSkills.Add(gem.Id);
        AppServices.Profiles.SaveActive();
        RenderGems();
    }

    private void EditGem(Gem gem)
    {
        if (Model.Profiles.Active is not { } profile) return;
        var entries = profile.Entries.Where(e=>IsEffectOf(e,gem.Id)).ToArray();
        var choices = gem.Skill?.Variants.OrderByDescending(v=>v.CanApply)
            .Select(v=>new WardrobeChoice(v.PickerName,v,v.IconPath??gem.IconPath,
                v.CosmeticName==null?v.Summary:v.Name+"\n"+v.Summary)).ToList() ?? [];
        if (gem.Buff is { } buff)
            choices.Insert(0,new WardrobeChoice("Auras and buffs",buff,gem.IconPath,"Chosen by compatibility with this gem"));
        var skillEffect = entries.FirstOrDefault(e=>e.Enabled && e.BuffSelection == null);
        var current = gem.Skill?.Variants.FirstOrDefault(v=>v.CanApply && skillEffect != null
            && v.Mappings[0].SourceCosmeticId == skillEffect.CosmeticId);
        var picker = new WardrobePicker("Gem effect · "+gem.Name,AppServices.Thumbnails,choices,
            (value,_) => value is SkillVisualVariant variant ? (variant.CanApply,variant.Summary)
                : (true,"Open the aura or buff choice for this gem."),
            current:current,resetLabel: entries.Length > 0 ? "Default look" : null,extraLabel:"Remove the gem and its replacements")
            { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true) return;
        if (picker.ResetRequested || picker.ExtraRequested)
        {
            profile.Entries.RemoveAll(e=>IsEffectOf(e,gem.Id));
            if (picker.ExtraRequested) profile.WardrobeSkills.RemoveAll(id=>id.Equals(gem.Id,StringComparison.OrdinalIgnoreCase));
            else profile.WardrobeSkills.Add(gem.Id);
            AppServices.Profiles.SaveActive();
        }
        else if (picker.Value is SkillVisualVariant variant && gem.Skill != null)
            Model.MySets.SaveSkillVariant(gem.Skill,variant);
        else if (picker.Value is BuffSkillDefinition buffSkill)
            new BuffVisualWindow(AppServices,buffSkill.Id) { Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
        Refresh(reloadPreview:true);
    }

    /// <summary>A small cross in the tile's corner that clears what the tile holds, saves the profile and redraws.</summary>
    private void AddRemove(Button tile,string tooltip,Action remove)
    {
        if(tile.Content is not DockPanel content)return;
        var muted=(Brush)FindResource("B.TextMuted");var gold=(Brush)FindResource("B.Gold");
        var cross=new TextBlock
        {
            Text="✕",FontSize=11,Foreground=muted,Padding=new Thickness(5,0,1,4),Margin=new Thickness(4,-4,-4,0),
            VerticalAlignment=VerticalAlignment.Top,Cursor=System.Windows.Input.Cursors.Hand,ToolTip=tooltip,Background=Brushes.Transparent
        };
        cross.MouseEnter+=(_,_)=>cross.Foreground=gold;
        cross.MouseLeave+=(_,_)=>cross.Foreground=muted;
        // Preview handler: the tile button must not open its picker for this click.
        cross.PreviewMouseLeftButtonDown+=(_,e)=>
        {
            e.Handled=true;
            remove();
            AppServices.Profiles.SaveActive();
            Refresh(reloadPreview:true);
        };
        DockPanel.SetDock(cross,Dock.Right);
        content.Children.Insert(0,cross);
    }

    private Button Tile(string title,string subtitle,string? iconPath,string glyph)
    {
        var icon = new Image { Width = 40, Height = 42, Stretch=Stretch.Uniform };
        var fallback = new System.Windows.Shapes.Path
        {
            Width=24, Height=24, Stretch=Stretch.Uniform, Fill=(Brush)FindResource("B.Gold"),
            Data=TryFindResource(glyph) as Geometry
        };
        var art = new Grid { Width=43, Margin=new Thickness(0,0,7,0) };
        art.Children.Add(fallback); art.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment=VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text=title, FontSize=13, TextTrimming=TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text=subtitle, FontSize=11, TextTrimming=TextTrimming.CharacterEllipsis,
            Foreground=(Brush)FindResource("B.TextMuted"), Margin=new Thickness(0,3,0,0) });
        var content = new DockPanel(); DockPanel.SetDock(art,Dock.Left); content.Children.Add(art); content.Children.Add(text);
        var button = new Button { Style=(Style)FindResource("WardrobeTile"), Content=content, ToolTip=title+" · "+subtitle };
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            var thumbnails = AppServices.Thumbnails;
            button.Loaded += async (_,_) =>
            {
                icon.Source = await thumbnails.GetAsync(iconPath);
                if (icon.Source != null) fallback.Visibility = Visibility.Collapsed;
            };
        }
        return button;
    }
}
