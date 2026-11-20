using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PoeMtxChanger.App;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.App.ViewModels;
using PoeMtxChanger.App.Views;
using PoeMtxChanger.Core.Util;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Profiles;

internal static class Program
{
    static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T target) yield return target;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    [STAThread]
    static int Main()
    {

        var app = new App();
        app.InitializeComponent();
        AppPaths.DataRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PoeMtxChanger-UiSmoke-"+Guid.NewGuid().ToString("N"));
        var services = new AppServices();
        var vm = new MainViewModel(services);
        var window = new MainWindow { DataContext = vm, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        window.Show();
        window.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle);
        var tabs = Find<RadioButton>(window).Where(r => r.GroupName == "tabs").ToList();
        if (tabs.Count != 6) throw new Exception($"Expected 6 tabs, got {tabs.Count}");
        foreach (var (tab, index) in tabs.Select((tab,index)=>(tab,index)))
        {
            tab.IsChecked = true;
            window.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle);
            if ((int)vm.SelectedTab != index) throw new Exception($"Tab {index} did not update view model: {vm.SelectedTab}");
        }
        tabs[0].IsChecked = true;
        services.Game.LoadMockCatalog();
        // Verify the rendered controls, not just whether the view model has an icon.
        // Assign the same frozen image to two items so selection changes exercise both preview bindings.
        var icon = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 180, 240, 255, 0, 180, 240, 255, 0, 180, 240, 255, 0, 180, 240, 255 }, 8);
        icon.Freeze();
        foreach (var item in vm.Catalog.VisibleItems.Take(2))
        {
            vm.Catalog.SelectedItem = item;
            window.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle);
            item.Thumbnail = icon;
            window.UpdateLayout();
            var previews = Find<Image>(window).Count(image => ReferenceEquals(image.DataContext, item)
                && ReferenceEquals(image.Source, icon) && image.IsVisible && image.ActualWidth > 0 && image.ActualHeight > 0);
            if (previews < 3) throw new Exception($"Missing artwork: expected card, large preview and selected thumbnail; found {previews}.");
        }
        Console.WriteLine("PASS: item images appear in all three locations and follow selection changes.");
        var effect = new SkillVisualVariant(5, "Celestial · variant 5",
            [new ReplacementMapping { TargetPath = "Art/default.ao", SourcePath = "Art/celestial.ao", SourceCosmeticId = "skill-variant:test-skill:5" }],
            ["Test partial role caveat"], []);
        services.Game.SkillVisuals = [new SkillVisualDefinition("test-skill", "Test skill", [effect]),
            new SkillVisualDefinition("second-skill", "Second skill", [effect])];
        vm.MySets.Refresh();
        tabs[1].IsChecked = true;
        window.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle);
        var sections = Find<RadioButton>(window).Where(r => r.GroupName == "sec").ToList();
        sections[1].IsChecked = true;
        window.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle);
        if (vm.MySets.Section != 1 || !Find<Grid>(window).Single(grid => grid.Name == "SkillPanel").IsVisible)
            throw new Exception("Skill effects section did not become visible.");
        vm.MySets.SelectedSkill = vm.MySets.Skills.Single(s=>s.Id=="test-skill");
        vm.MySets.AddSkillMappingCommand.Execute(null);
        var skillEntry = vm.Profiles.Active!.Entries.Single(entry => entry.Key == "test-skill");
        if (skillEntry.Mappings.Count != 1 || !skillEntry.Notes.Contains("Test partial role caveat"))
            throw new Exception("Skill plan/caveats were not persisted in the isolated profile.");
        sections[0].IsChecked = true;
        if (vm.MySets.Section != 0) throw new Exception("Equipment section did not become active.");
        tabs[0].IsChecked = true;
        window.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle);
        Console.WriteLine("PASS: skill/equipment section bindings, variant assignment and caveat persistence.");
        vm.Catalog.StatusMessage = "“Abyss Core Crown” was added to profile “Default”. In “My Sets”, choose which base item it is worn over. "
            + string.Concat(Enumerable.Repeat("An additional long message for checking line wrapping. ",5));
        foreach (var (width,height) in new[] {(1536,1024),(1200,800),(1200,760)})
        {
            window.Width=width; window.Height=height; window.UpdateLayout();
            var search=Find<TextBox>(window).First(t=>t.Name=="Search");
            if (search.ActualWidth < 120) throw new Exception($"Search too narrow at {width}: {search.ActualWidth}");
            var frame=Find<ContentControl>(window).Single(c=>c.Name=="SelectedDetailsFrame");
            var actions=Find<Grid>(frame).Single(g=>g.Name=="CatalogActions");
            foreach(var button in Find<Button>(actions))
            {
                var bounds=button.TransformToAncestor(frame).TransformBounds(new Rect(button.RenderSize));
                if(button.ActualHeight<50||bounds.Top<0||bounds.Bottom>frame.ActualHeight+.5||bounds.Right>frame.ActualWidth+.5)
                    throw new Exception($"Clipped catalog action at {width}x{height}: {bounds}, panel height={frame.ActualHeight}");
                var windowBounds=button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                if(windowBounds.Bottom>window.ActualHeight-86+.5)throw new Exception("Catalog actions overlap the status bar.");
            }
            Console.WriteLine($"PASS: {width}x{height}, search width={search.ActualWidth:0}, all six tabs work.");
        }
        Console.WriteLine("PASS: both catalog actions remain inside the details frame and above the status bar with a long assignment message.");
        tabs[2].IsChecked = true;
        window.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle);
        var wardrobe = Find<WardrobeView>(window).Single();
        if (!wardrobe.IsVisible) throw new Exception("Wardrobe tab did not open.");
        Button FindSlot(string key) => Find<Button>(wardrobe).Single(b=>(b.Tag as string)==key);
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void UsePicker(Button opener,Action<WardrobePicker> interact)
        {
            Exception? error = null;
            var pending = window.Dispatcher.InvokeAsync(() =>
            {
                var picker = app.Windows.OfType<WardrobePicker>().SingleOrDefault(w=>w.IsVisible);
                try { interact(picker ?? throw new Exception("Slot selection did not open.")); }
                catch(Exception ex) { error=ex; picker?.Close(); }
            },DispatcherPriority.ApplicationIdle);
            Click(opener);
            if(pending.Status!=DispatcherOperationStatus.Completed) { pending.Abort(); throw new Exception("The picker did not open a modal window."); }
            if(error!=null) throw error;
        }
        void ChooseFirst(WardrobePicker picker)
        {
            Find<ListBox>(picker).Single(b=>b.Name=="Choices").SelectedIndex=0;
            var save = Find<Button>(picker).Single(b=>b.Name=="Commit");
            if (!save.IsEnabled) throw new Exception("First choice is unexpectedly disabled.");
            Click(save);
        }
        UsePicker(FindSlot("helmet"),ChooseFirst);
        var helmetEntry = vm.Profiles.Active.Entries.Single(e=>e.Kind==ProfileEntryKind.EquipmentSlot&&e.Key=="helmet");
        if(helmetEntry.CosmeticId==null)throw new Exception("Helmet card was not saved.");
        UsePicker(FindSlot("helmet"),picker=>picker.Close());
        if(vm.Profiles.Active.Entries.Single(e=>e.Key=="helmet").CosmeticId!=helmetEntry.CosmeticId)
            throw new Exception("Closing a slot picker changed the selection.");
        UsePicker(Find<Button>(wardrobe).Single(b=>b.Name=="AddGemButton"),ChooseFirst);
        UsePicker(FindSlot("second-skill"),ChooseFirst);
        var persisted = services.Profiles.Manager.LoadAll().Single();
        if(!persisted.WardrobeSkills.Contains("second-skill") || !persisted.Entries.Any(e=>e.Key=="second-skill"&&e.Mappings.Count==1))
            throw new Exception("Pinned gem or chosen effect did not survive profile reload.");
        Console.WriteLine("PASS: separate Wardrobe tab, helmet card, cancel, add gem, click gem and save its effect in the profile.");

        const string swordClass = "One Hand Sword";
        var baseSword = new TargetSuggestion("weapon_main","Rusted Sword","Art/test_base_sword.ao","test") { ItemClass=swordClass };
        var skin = new CosmeticItem { Id="mock:test-sword",DisplayName="Test Sword Skin",Category=CosmeticCategory.Weapon,
            MtxType="Weapon Skin",HeldWeaponPath="Art/test_sword_skin.ao",AllowedItemClasses=[swordClass] };
        CosmeticItem Effect(string id,string label,string path) => new()
        {
            Id=id, DisplayName=label, Category=CosmeticCategory.Weapon, MtxType="Weapon Effect",
            ResourceVariants=new Dictionary<string,string>{{"OneHandedSwordEPKFile",path}}
        };
        var oldEffect=Effect("mock:old-effect","A Current Effect","Art/test_previous_effect.epk");
        var donorEffect=Effect("mock:donor-effect","B New Effect","Art/test_new_effect.epk");
        services.Game.Catalog=[..services.Game.Catalog,skin,oldEffect,donorEffect];
        services.Game.Suggestions=[..services.Game.Suggestions,baseSword];
        var stancePicker=Find<ComboBox>(wardrobe).Single(box=>box.Name=="StancePicker");
        stancePicker.SelectedItem=WeaponVisuals.Stances.Single(s=>s.Id=="sword");
        UsePicker(FindSlot("weapon_main"),ChooseFirst);
        UsePicker(FindSlot("effect_main"),picker=>
        {
            Find<ComboBox>(picker).Single(c=>c.Name=="Targets").SelectedIndex=1;
            Find<ListBox>(picker).Single(b=>b.Name=="Choices").SelectedIndex=1;
            Click(Find<Button>(picker).Single(b=>b.Name=="Commit"&&b.IsEnabled));
        });
        var weapons=services.Profiles.Manager.LoadAll().Single();
        if(weapons.WeaponSetup.StanceId!="sword"||weapons.WeaponSetup.MainBasePath!=baseSword.Path||weapons.WeaponSetup.MainEffectId!=donorEffect.Id
            ||!weapons.Entries.Any(e=>e.Key=="weapon_main"&&e.Mappings.Single().SourcePath==skin.HeldWeaponPath)
            ||!weapons.Entries.Any(e=>e.Key=="weapon_effect_main"&&e.Mappings.Single().TargetPath=="Art/test_previous_effect.epk"))
            throw new Exception("Weapon stance, skin and effect did not save independently.");
        Console.WriteLine("PASS: base weapon, held skin and already-equipped effect are independently selectable and persist in the same profile.");
        window.Close();
        return 0;
    }
}

