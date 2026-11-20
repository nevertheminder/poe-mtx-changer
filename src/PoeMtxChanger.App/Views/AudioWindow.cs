using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.Core.Formats.Audio;
using PoeMtxChanger.Core.Patching;

namespace PoeMtxChanger.App.Views;

public sealed class AudioWindow : Window
{
    private readonly AppServices _app;
    private readonly AudioBankEngine _engine;
    private readonly ComboBox _targetBank=new(){IsTextSearchEnabled=true,Margin=new Thickness(0,8,0,8)};
    private readonly ComboBox _sourceBank=new(){IsTextSearchEnabled=true,Margin=new Thickness(0,8,0,8)};
    private readonly ListBox _targets=new(){DisplayMemberPath="Name"};
    private readonly ListBox _sources=new(){DisplayMemberPath="Name"};
    private readonly ListBox _plan=new(){MaxHeight=120,Margin=new Thickness(0,8,0,8)};
    private readonly TextBlock _info=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)};
    private FmodBank? _target,_source;
    private readonly MediaPlayer _player=new();
    private readonly string _temporary=Path.Combine(Path.GetTempPath(),"PoeMtxChanger-AudioPreview-"+Guid.NewGuid().ToString("N"));
    private int _generation;
    private bool _busy;
    public Task Ready {get;private set;}=Task.CompletedTask;

    public AudioWindow(AppServices app)
    {
        _app=app;Title="Sounds · individual FMOD samples";Width=1100;Height=820;MinWidth=900;MinHeight=620;
        Background=new SolidColorBrush(Color.FromRgb(10,13,15));Foreground=Brushes.Bisque;
        var root=new DockPanel{Margin=new Thickness(18)};Content=root;
        var heading=new StackPanel();DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
        heading.Children.Add(new TextBlock{Text="Replace individual sounds",FontSize=24});
        heading.Children.Add(new TextBlock{Text="On the left, a sound the game already uses; on the right, a replacement with the same length, rate, channel count and loop mode. Events and slot names are kept.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)});
        var bankFilter=new TextBox{Width=360,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,10),ToolTip="Bank name filter. Clear the text to see all banks."};heading.Children.Add(bankFilter);
        var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
        bottom.Children.Add(_info);bottom.Children.Add(new TextBlock{Text="The active profile's plan for the selected bank. Sounds are applied here, separately from the visuals."});bottom.Children.Add(_plan);
        var buttons=new WrapPanel();bottom.Children.Add(buttons);
        Button Add(string text,Func<Task> action){var b=new Button{Content=text,Margin=new Thickness(0,4,8,4),Padding=new Thickness(12,7,12,7)};b.Click+=async(_,_)=>await Run(action);buttons.Children.Add(b);return b;}
        Add("Play the original",()=>Play(_target,_targets.SelectedItem as SampleInfo));
        Add("Play the replacement",()=>Play(_source,_sources.SelectedItem as SampleInfo));
        Add("Add to plan",()=>{AddPlan();return Task.CompletedTask;});
        Add("Remove from plan",()=>{RemovePlan();return Task.CompletedTask;});
        Add("Apply to this bank",Apply);
        Add("Restore this bank",Restore);
        var grid=new Grid();grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new(){Width=new GridLength(20)});grid.ColumnDefinitions.Add(new());root.Children.Add(grid);
        void Column(int column,string title,ComboBox bank,ListBox samples)
        {var panel=new DockPanel();Grid.SetColumn(panel,column);grid.Children.Add(panel);var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);panel.Children.Add(top);top.Children.Add(new TextBlock{Text=title,FontSize=18});top.Children.Add(bank);panel.Children.Add(samples);}
        Column(0,"Sound in use",_targetBank,_targets);Column(2,"Compatible replacements",_sourceBank,_sources);
        _engine=new AudioBankEngine(app.Game.Installation?.RootPath??"",app.Settings.BackupDirectory);
        string dir=Path.Combine(app.Game.Installation?.RootPath??"","FMOD","Desktop");
        var banks=Directory.Exists(dir)?Directory.EnumerateFiles(dir,"*.bank").Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray():[];
        _targetBank.ItemsSource=banks;_sourceBank.ItemsSource=banks;
        bankFilter.TextChanged+=(_,_)=>
        {
            var filtered=banks.Where(b=>b.Contains(bankFilter.Text,StringComparison.OrdinalIgnoreCase)).ToArray();
            string? target=_targetBank.SelectedItem as string,source=_sourceBank.SelectedItem as string;
            _targetBank.ItemsSource=filtered;_sourceBank.ItemsSource=filtered;
            _targetBank.SelectedItem=filtered.Contains(target)?target:filtered.FirstOrDefault();_sourceBank.SelectedItem=filtered.Contains(source)?source:filtered.FirstOrDefault();
        };
        _targetBank.SelectionChanged+=(_,_)=>Ready=LoadBanks();_sourceBank.SelectionChanged+=(_,_)=>Ready=LoadBanks();
        _targets.SelectionChanged+=(_,_)=>FilterSources();_sources.SelectionChanged+=(_,_)=>ShowInfo();
        Loaded+=(_,_)=>
        {
            bankFilter.Text="Cyclone";
            _targetBank.SelectedItem=banks.FirstOrDefault(b=>b=="AuspiciousCycloneMTX.bank")??banks.FirstOrDefault();
            _sourceBank.SelectedItem=banks.FirstOrDefault(b=>b=="CelestialCycloneMTX.bank")??banks.FirstOrDefault();
            Ready=LoadBanks();
        };
        Closed+=(_,_)=>{_generation++;_player.Close();try{if(Directory.Exists(_temporary))Directory.Delete(_temporary,true);}catch(IOException){} };
        _player.MediaFailed+=(_,e)=>_info.Text="Could not play the clip: "+e.ErrorException.Message;
    }
    private async Task Run(Func<Task> action)
    {
        if(_busy)return;_busy=true;
        try{await action();}
        catch(Exception ex){_info.Text=ex.Message;_app.Dialogs.Error("Sounds",ex.Message);}
        finally{_busy=false;}
    }
    private async Task LoadBanks()
    {
        int generation=++_generation;
        string? target=_targetBank.SelectedItem as string,source=_sourceBank.SelectedItem as string;
        if(target==null||source==null)return;
        _target=null;_source=null;_targets.ItemsSource=null;_sources.ItemsSource=null;_info.Text="Reading the audio banks…";
        try
        {
            var banks=await Task.Run(()=>(Target:new FmodBank(_engine.ReadClean(target)),Source:new FmodBank(_engine.ReadClean(source))));
            if(generation!=_generation)return;
            _target=banks.Target;_source=banks.Source;_targets.ItemsSource=_target.Fsb.Samples;_targets.SelectedIndex=0;FilterSources();RefreshPlan();
        }
        catch(Exception ex){if(generation==_generation)_info.Text="This bank format is not supported: "+ex.Message;}
    }
    private void FilterSources()
    {
        _sources.ItemsSource=_targets.SelectedItem is SampleInfo selected&&_source!=null?_source.Fsb.Samples.Where(s=>FsbSoundBank.Compatible(selected,s)).ToArray():Array.Empty<SampleInfo>();
        _sources.SelectedIndex=0;ShowInfo();
    }
    private void ShowInfo()
    {
        if(_targets.SelectedItem is not SampleInfo target)return;
        _info.Text=$"{target.Frequency:N0} Hz · {target.Channels} ch · {(double)target.Frames/target.Frequency:0.000} s · loop: {(target.Loops?"yes":"no")}. Compatible replacements: {_sources.Items.Count}.";
    }
    private void RefreshPlan()
    {
        string? target=_targetBank.SelectedItem as string;
        _plan.ItemsSource=_app.Profiles.Active?.AudioSwaps.Where(s=>s.TargetBank==target).Select(s=>$"#{s.TargetIndex} {s.TargetName} ← {s.SourceBank}: {s.SourceName}").ToArray();
    }
    private void AddPlan()
    {
        if(_app.Profiles.Active is not {} profile||_targets.SelectedItem is not SampleInfo target||_sources.SelectedItem is not SampleInfo source)return;
        var swap=_engine.Plan((string)_targetBank.SelectedItem,target.Index,(string)_sourceBank.SelectedItem,source.Index);
        profile.AudioSwaps.RemoveAll(s=>s.TargetBank==swap.TargetBank&&s.TargetIndex==swap.TargetIndex);profile.AudioSwaps.Add(swap);_app.Profiles.SaveActive();RefreshPlan();
    }
    private void RemovePlan()
    {
        string? target=_targetBank.SelectedItem as string;
        var selected=_app.Profiles.Active?.AudioSwaps.Where(s=>s.TargetBank==target).ElementAtOrDefault(_plan.SelectedIndex);
        if(selected==null)return;_app.Profiles.Active!.AudioSwaps.Remove(selected);_app.Profiles.SaveActive();RefreshPlan();
    }
    private async Task Apply()
    {
        if(_targetBank.SelectedItem is not string target||_app.Profiles.Active is not {} profile)return;
        var swaps=profile.AudioSwaps.Where(s=>s.TargetBank==target).ToArray();if(swaps.Length==0){_info.Text="Add a replacement to the plan.";return;}
        string preview=string.Join("\n",swaps.Select(s=>$"• {s.TargetName} ← {s.SourceName}"));
        if(!_app.Dialogs.Confirm("Change the audio bank?",$"{target}: {swaps.Length} replacements\n\n{preview}\n\nA verified full backup of this bank will be made. The game must be closed. Continue?"))return;
        _info.Text="Checking and building a separate bank…";
        await Task.Run(()=>_engine.Apply(target,swaps,true));_info.Text="Done. The bank's content and events were checked; check the sound in game yourself.";
    }
    private async Task Restore()
    {
        if(_targetBank.SelectedItem is not string target)return;
        if(!_app.Dialogs.Confirm("Restore the sound?","Return the original bank "+target+" from the verified backup? The game must be closed."))return;
        bool restored=await Task.Run(()=>_engine.Restore(target,true));_info.Text=restored?"The original bank was restored; SHA-256 matches.":"This bank has no active replacement.";
    }
    private async Task Play(FmodBank? bank,SampleInfo? sample)
    {
        if(bank==null||sample==null)return;
        _player.Close();_info.Text="Decoding the clip (up to 12 seconds)…";
        string dll=Path.Combine(_app.Game.Installation!.RootPath,"fmod.dll");
        string output=await Task.Run(()=>
        {
            Directory.CreateDirectory(_temporary);string fsb=Path.Combine(_temporary,"preview.fsb"),wave=Path.Combine(_temporary,Guid.NewGuid().ToString("N")+".wav");File.WriteAllBytes(fsb,bank.Fsb.Raw);
            using var decoder=new FmodPcmDecoder(dll);File.WriteAllBytes(wave,decoder.DecodeWave(fsb,sample));return wave;
        });
        if(!IsLoaded)return;_player.Volume=.35;_player.Open(new Uri(output));_player.Play();_info.Text="Playing: "+sample.Name;
    }
}
