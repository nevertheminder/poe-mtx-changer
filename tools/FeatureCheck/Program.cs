using System.Numerics;
using System.IO;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.App.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PoeMtxChanger.App.ViewModels;
using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Compression;
using PoeMtxChanger.Core.Formats.Audio;
using PoeMtxChanger.Core.Formats.Dat;
using PoeMtxChanger.Core.Formats.Geometry;
using PoeMtxChanger.Core.Formats.Ggpk;
using PoeMtxChanger.Core.Installation;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Patching;
using PoeMtxChanger.Core.Profiles;
using PoeMtxChanger.Core.Storage;
using PoeMtxChanger.Core.Util;

internal static partial class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string game=args[0],dll=args[1];
        string temporary=Path.Combine(Path.GetTempPath(),"PoeMtxChanger-FeatureCheck-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
        AppPaths.DataRoot=Path.Combine(temporary,"app");
        var app=new AppServices();app.Game.Installation=GameInstallationDetector.Probe(game,"read-only feature check");
        app.Game.Codec=NativeOodleCodec.TryLoad(dll);
        using var archive=GameArchive.Open(app.Game.Installation!.OpenStorage(),app.Game.Codec);app.Game.Archive=archive;
        string liveIndex=Path.Combine(game,"Bundles2","_.index.bin"),indexSha=FileUtil.Sha256OfFile(liveIndex);
        var catalog=new DataCatalogBuilder(archive,DatSchema.LoadDefault(),CatalogRules.LoadDefault()).Build();app.Game.Catalog=catalog.Items;
        int effectCheck=Array.IndexOf(args,"--shield-and-aura");
        if(effectCheck>=0)
        {
            app.Game.Suggestions=catalog.Suggestions;
            app.Game.BuffVisuals=catalog.Buffs;
            app.Game.BuffSkills=catalog.BuffSkills;
            CheckShieldAndAura(app,args[effectCheck+1]);
            if(FileUtil.Sha256OfFile(liveIndex)!=indexSha)throw new Exception("Live index changed");
            return 0;
        }
        int gpu=Array.IndexOf(args,"--gpu-check");
        if(gpu>=0)
        {
            app.Game.Suggestions=catalog.Suggestions;
            CheckGpuWardrobe(app,args[gpu+1]);
            if(FileUtil.Sha256OfFile(liveIndex)!=indexSha)throw new Exception("Live index changed");
            return 0;
        }
        int shopAudit=Array.IndexOf(args,"--shop-audit");
        if(shopAudit>=0)
        {
            // Shop names (per website category) against the catalog read from the installation.
            static string Key(string s)=>new string(s.Replace('’','\'').Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            var byName=catalog.Items.GroupBy(i=>Key(i.DisplayName)).ToDictionary(g=>g.Key,g=>g.ToList());
            using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[shopAudit+1]));
            foreach(var category in json.RootElement.EnumerateObject())
            {
                var shopNames=category.Value.EnumerateArray().Select(v=>v.GetString()!).ToList();
                var missing=shopNames.Where(n=>!byName.ContainsKey(Key(n))).ToList();
                var found=shopNames.Where(n=>byName.ContainsKey(Key(n))).SelectMany(n=>byName[Key(n)]).ToList();
                Console.WriteLine($"{category.Name}: {shopNames.Count} shop items, {shopNames.Count-missing.Count} in catalog, {missing.Count} missing");
                foreach(var group in found.GroupBy(i=>(i.MtxType??"(none)")+" | "+i.Category).OrderByDescending(g=>g.Count()))
                    Console.WriteLine($"     {group.Count(),4} {group.Key}");
                if(missing.Count>0)Console.WriteLine("     missing: "+string.Join("; ",missing.Take(40)));
            }
            return 0;
        }
        if(args.Contains("--catalog-stats"))
        {
            Console.WriteLine($"catalog: {catalog.Items.Count} items");
            foreach(var group in catalog.Items.GroupBy(i=>(i.MtxType??"(none)")+" | "+i.Category).OrderByDescending(g=>g.Count()))
                Console.WriteLine($"{group.Count(),6}  {group.Key}   e.g. {string.Join("; ",group.Take(3).Select(i=>i.DisplayName))}");
            Console.WriteLine("---- untyped Other by resource root");
            foreach(var group in catalog.Items.Where(i=>i.MtxType==null&&i.Category==CosmeticCategory.Other)
                         .GroupBy(i=>string.Join('/',(i.PrimaryResources.FirstOrDefault()??i.RootPath??"(no resource)").Split('/').Take(3)))
                         .OrderByDescending(g=>g.Count()).Take(25))
                Console.WriteLine($"{group.Count(),6}  {group.Key}   e.g. {string.Join("; ",group.Take(3).Select(i=>i.DisplayName))}");
            return 0;
        }
        int ui=Array.IndexOf(args,"--ui-sizes");
        if(ui>=0)
        {
            app.Game.Suggestions=catalog.Suggestions;
            app.Game.BuffVisuals=catalog.Buffs;
            app.Game.BuffSkills=catalog.BuffSkills;
            CheckUiSizes(app,args[ui+1]);
            return 0;
        }
        int lights=Array.IndexOf(args,"--light-check");
        if(lights>=0)
        {
            app.Game.Suggestions=catalog.Suggestions;
            CheckEffectLights(app,args[lights+1],args[lights+2],args[lights+3]);
            if(FileUtil.Sha256OfFile(liveIndex)!=indexSha)throw new Exception("Live index changed");
            return 0;
        }
        int reference=Array.IndexOf(args,"--wardrobe-reference");
        if(reference>=0)
        {
            app.Game.Suggestions=catalog.Suggestions;
            app.Game.BuffVisuals=catalog.Buffs;
            app.Game.BuffSkills=catalog.BuffSkills;
            CheckReferenceWardrobe(app,args[reference+1],args.Skip(reference+2).ToArray());
            if(FileUtil.Sha256OfFile(liveIndex)!=indexSha)throw new Exception("Live index changed");
            return 0;
        }
        int audit=Array.IndexOf(args,"--render-audit");
        if(audit>=0)
        {
            RenderAudit.Run(archive,catalog.Items,args[audit+1]);
            if(FileUtil.Sha256OfFile(liveIndex)!=indexSha)throw new Exception("Live index changed");
            return 0;
        }
        if(args.Contains("--weapon-scene"))
        {
            app.Game.Suggestions=catalog.Suggestions;
            var profile=app.Profiles.Active!;
            foreach(var (character,_) in CharacterClasses.All)
            {
                var scene=new ModelPreviewLoader(app).Load(null,character,true,true,default,showWeapons:true);
                if(scene.Meshes.Count(m=>m.MountFromOrigin)!=1||scene.Skeleton?.Clips.All(c=>c.Name!=scene.PreferredIdle)!=false)
                    throw new Exception($"Default weapon did not load for {character}: {scene.Description}");
                Console.WriteLine($"PASS {character}: {scene.PreferredIdle}, {scene.Meshes.Single(m=>m.MountFromOrigin).Path}");
            }
            foreach(var stance in WeaponVisuals.Stances)
            {
                profile.WeaponSetup.StanceId=stance.Id;
                var scene=new ModelPreviewLoader(app).Load(null,"Duelist",true,true,default,showWeapons:true);
                int expected=(stance.MainClass!=null?1:0)+(stance.OffClass!=null?1:0);
                if(scene.Meshes.Count(m=>m.MountFromOrigin)!=expected||scene.Skeleton?.Clips.All(c=>c.Name!=stance.Idle)!=false)
                    throw new Exception($"{stance.Id}: {scene.Description}");
            }
            Console.WriteLine($"PASS all {WeaponVisuals.Stances.Count} weapon presets on Duelist.");
            profile.WeaponSetup.StanceId="dual_swords";
            var dual=new ModelPreviewLoader(app).Load(null,"Duelist",true,true,default,showWeapons:true);
            var weapons=dual.Meshes.Where(m=>m.MountFromOrigin).ToArray();
            if(dual.PreferredIdle!="idle_sword_sword"||weapons.Length!=2||weapons[0].MountBone==weapons[1].MountBone)
                throw new Exception("Two swords did not use separate sockets.");
            var clip=dual.Skeleton!.Clips.Single(c=>c.Name==dual.PreferredIdle);
            var start=weapons[0].Pose(dual.Skeleton,dual.Skeleton.Skinning(clip,0),0);
            var end=weapons[0].Pose(dual.Skeleton,dual.Skeleton.Skinning(clip,.3f),.3f);
            if(start.SequenceEqual(end))throw new Exception("Main-hand model did not follow the character animation.");
            Console.WriteLine("PASS Duelist dual wield: animated separate hand mounts.");

            profile.WeaponSetup.StanceId="staff";
            var staff=catalog.Items.Single(i=>i.DisplayName=="Celestial Staff");
            var effect=catalog.Items.Single(i=>i.DisplayName=="Celestial Weapon Effect");
            var wings=catalog.Items.Single(i=>i.DisplayName=="Seraph Wings");
            var seraph=catalog.Items.Single(i=>i.DisplayName=="Seraph Character Effect");
            var angelic=catalog.Items.Single(i=>i.DisplayName=="Angelic Character Effect");
            var celestialBody=catalog.Items.Single(i=>i.DisplayName=="Celestial Character Effect");
            foreach(var cosmetic in new[]{seraph,angelic,celestialBody})
            {
                var characterScene=new ModelPreviewLoader(app).Load(cosmetic,"Marauder",true,false,default);
                int emitters=characterScene.Meshes.Sum(m=>m.Emitters.Count);
                if(emitters==0)throw new Exception(cosmetic.DisplayName+": "+characterScene.Description);
                Console.WriteLine($"PASS {cosmetic.DisplayName}: {emitters} emitters from character effect package.");
                if(cosmetic==angelic&&!characterScene.Meshes.Any(mesh=>mesh.Trails.Count>0))throw new Exception("Angelic TRL layers did not load.");
                if(cosmetic==seraph)
                {
                    var ring=characterScene.Meshes.Single(m=>m.GroundOffsetZ.HasValue);
                    if(!ring.Emitters.Any(e=>e.SoftDepth>0))throw new Exception("A live Seraph particle material must carry its depth-fade distance.");
                    var body=characterScene.Skeleton!;
                    float floor=characterScene.Meshes[0].Geometry.Vertices.Max(v=>v.Position.Z);
                    foreach(var name in new[]{"idle_unarmed","run_unarmed"})
                    {
                        var groundCheckClip=body.Clips.Single(c=>c.Name==name);
                        for(float time=0;time<.75f;time+=.15f)
                        {
                            var pose=body.Skinning(groundCheckClip,time);
                            float bottom=ring.Pose(body,pose,time).Max(p=>p.Z);
                            if(Math.Abs(bottom-floor)>3||ring.MountTransform(body,pose).M33!=1)
                                throw new Exception("The Seraph ring must remain horizontal and near the bind floor in idle and run.");
                        }
                    }
                    Console.WriteLine("PASS Seraph ring: grounded in idle and running poses, with horizontal motion only.");
                }
                else if(characterScene.Meshes.Any(m=>m.GroundOffsetZ.HasValue))throw new Exception("A character-bound effect was classified as a ground effect.");
            }
            var gold=catalog.Items.Single(i=>i.DisplayName=="Golden Character Effect");
            var goldScene=new ModelPreviewLoader(app).Load(gold,"Marauder",true,false,default);
            if(!goldScene.Meshes[0].Surfaces.Any(s=>s.Coating is {Metal:not null,Normal:not null}))throw new Exception(goldScene.Description);
            Console.WriteLine("PASS Golden Character Effect: coating tint, reflectance and detail normals applied to the model.");
            profile.GetOrAddEntry(ProfileEntryKind.EquipmentSlot,"character_effect").CosmeticId=seraph.Id;
            var weaponEntry=profile.GetOrAddEntry(ProfileEntryKind.EquipmentSlot,"weapon_main");weaponEntry.CosmeticId=staff.Id;
            profile.GetOrAddEntry(ProfileEntryKind.EquipmentSlot,"back").CosmeticId=wings.Id;
            profile.WeaponSetup.MainEffectId=effect.Id;
            var dressedStaff=new ModelPreviewLoader(app).Load(null,"Marauder",true,true,default,showWeapons:true);
            var staffMesh=dressedStaff.Meshes.Single(m=>m.Path.Contains("CelestialStaff"));
            if(dressedStaff.PreferredIdle!="idle_staff"||!staffMesh.Path.Contains("CelestialStaff")||staffMesh.Emitters.Count==0)
                throw new Exception($"Skin and sample weapon effect did not load: {dressedStaff.Description}");
            if(!staffMesh.Emitters.Any(e=>e.Blend==PreviewOpacity.Multiply&&e.AlphaOverLife.Contains(0)&&e.AlphaOverLife.Any(a=>a>.5f)))
                throw new Exception("The dark Celestial layers and their short/long legacy opacity keys must be preserved.");
            if(!dressedStaff.Meshes.Any(m=>m.Path.Contains("SeraphWings",StringComparison.OrdinalIgnoreCase)&&m.MountFromOrigin
                && m.Glow.Any(im=>im!=null)&&dressedStaff.Skeleton!.Bones[m.MountBone].Name.Equals("Back",StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Back attachment or its emissive layer did not load.");
            Console.WriteLine($"PASS Celestial Staff + Celestial Weapon Effect: {staffMesh.Emitters.Count} emitters, Seraph Wings with emission.");
            int shot=Array.IndexOf(args,"--wardrobe-shot");
            if(shot>=0)CheckWardrobeFrame(app,args[shot+1]);
            var cloak=catalog.Items.First(i=>i.Category==CosmeticCategory.WingsAndBack&&i.DisplayName.Contains("Cloak")&&i.PrimaryResources.Count==1);
            profile.GetOrAddEntry(ProfileEntryKind.EquipmentSlot,"back").CosmeticId=cloak.Id;
            var cloakScene=new ModelPreviewLoader(app).Load(null,"Duelist",true,true,default,showWeapons:true);
            if(!cloakScene.Meshes.Any(m=>m.Path.Contains("LichCape",StringComparison.OrdinalIgnoreCase)&&m.MountFromOrigin
                &&cloakScene.Skeleton!.Bones[m.MountBone].Name.Equals("Back",StringComparison.OrdinalIgnoreCase)))throw new Exception(cloakScene.Description);
            var cape=cloakScene.Meshes.Single(m=>m.Path.Contains("LichCape",StringComparison.OrdinalIgnoreCase));
            var capeStart=cape.WorldPositions(cape.LocalPose(0),cape.MountTransform(cloakScene.Skeleton,null),0);
            var capeEnd=cape.WorldPositions(cape.LocalPose(0),cape.MountTransform(cloakScene.Skeleton,null),.55f);
            var hem=capeStart.Zip(capeEnd).Select(p=>Vector3.Distance(p.First,p.Second)).ToArray();
            if(cape.ClothFlex<=0||hem.Max()<1||hem.Min()>.01f)throw new Exception("The cape should flex at the hem and stay anchored at the top.");
            Console.WriteLine($"PASS {cloak.DisplayName}: attached to the back with a moving hem and fixed upper edge.");
            foreach(var name in new[]{"Lightning Wings","White Lightning Wings III"})
            {
                var item=catalog.Items.Single(i=>i.DisplayName==name);
                var scene=new ModelPreviewLoader(app).Load(item,"Duelist",true,false,default);
                if(!scene.Meshes.Any(m=>m.MountBone>=0&&m.Geometry.Vertices.Length==0&&m.Emitters.Count>0))throw new Exception(scene.Description);
                Console.WriteLine($"PASS {name}: particles attached to the back without a skin mesh.");
            }
            var skull=catalog.Items.Single(i=>i.DisplayName=="Divine Skull");
            var skullScene=new ModelPreviewLoader(app).Load(skull,"Duelist",true,false,default);
            if(skullScene.Meshes.Count<2||skullScene.Meshes[1].Textures.Zip(skullScene.Meshes[1].Glow).Count(p=>p.First!=null||p.Second!=null)<3)
                throw new Exception("The skull's AO material overrides were not loaded: "+skullScene.Description);
            if(!skullScene.Meshes.Any(m=>m.Path.Contains("holyskullmtx",StringComparison.OrdinalIgnoreCase)&&m.Emitters.Count>0))
                throw new Exception("The Divine Skull flame did not mount: "+skullScene.Description);
            Console.WriteLine("PASS Divine Skull: standalone render layers plus shape materials overridden by its AO.");
            profile.WeaponSetup.StanceId="sword";
            weaponEntry.CosmeticId=null;
            profile.GetOrAddEntry(ProfileEntryKind.EquipmentSlot,"character_effect").CosmeticId=null;
            profile.WeaponSetup.MainEffectId=catalog.Items.Single(i=>i.DisplayName=="Sin Weapon Effect").Id;
            var sinScene=new ModelPreviewLoader(app).Load(null,"Duelist",true,true,default,showWeapons:true);
            if(!sinScene.Meshes.Any(m=>m.Surfaces.Any(s=>s.Coating is {Mask:not null} c&&c.EdgeColour.LengthSquared()>.01f)&&m.Trails.Count>0))
                throw new Exception("The Sin weapon material and motion trail did not load: "+sinScene.Description);
            Console.WriteLine("PASS Sin Weapon Effect: masked rim shading and a motion trail on the standard sword.");
            var sinEmitters=sinScene.Meshes.SelectMany(m=>m.Emitters).ToArray();
            if(!sinEmitters.Any(e=>e.Definition?.Blend=="AlphaBlend"&&e.GameShader is {BlendState:1})
                ||!sinEmitters.Any(e=>e.Definition?.Blend=="AdditiveBlend"&&e.GameShader is {BlendState:4}))
                throw new Exception("Sin particle materials must respect the emitter's blend over an opaque default.");
            if(sinScene.Meshes.SelectMany(m=>m.Trails).All(t=>t.GameShader is not {Blend:PreviewOpacity.Cutout,BlendState:0}))
                throw new Exception("The Sin cutout trail must keep its explicit material blend mode.");
            if(shot>=0)CheckWardrobeFrame(app,Path.ChangeExtension(args[shot+1],".sin.png"),checkRibbon:true);
            profile.WeaponSetup.StanceId="bow";
            var bloodlordItem=catalog.Items.First(i=>WeaponVisuals.EffectPath(i,"Bow")?.Contains("/bloodlord/",StringComparison.OrdinalIgnoreCase)==true);
            profile.WeaponSetup.MainEffectId=bloodlordItem.Id;
            var bloodlord=new ModelPreviewLoader(app).Load(null,"Duelist",true,true,default,showWeapons:true);
            if(!bloodlord.Meshes.Any(m=>m.ParentMount!=null&&m.Emitters.Count>0))
                throw new Exception("Bloodlord's extended attachment must follow the bow: "+bloodlord.Description);
            Console.WriteLine($"PASS {bloodlordItem.DisplayName}: nested particles follow the source bow socket via AttachedObjectEx.");
            if(FileUtil.Sha256OfFile(liveIndex)!=indexSha)throw new Exception("Live index changed");
            return 0;
        }
        if(args.Contains("--preview-check"))
        {
            var crown=catalog.Items.Single(i=>i.DisplayName=="Abyss Core Crown");
            var crownScene=new ModelPreviewLoader(app).Load(crown,"Marauder",true,false,default);
            var bitmap=crownScene.Meshes.Single(mesh=>mesh.Path.EndsWith("AbyssHelmetStr.sm"))
                .Textures.OfType<System.Windows.Media.Imaging.BitmapSource>().First();
            byte[] pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];
            bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
            if(pixels.Where((_,i)=>i%4==3).Any(alpha=>alpha!=255))
                throw new Exception("An opaque helmet texture is still using the specular mask as transparency.");
            Console.WriteLine("PASS: Abyss Core Crown loads on Marauder and its opaque shader texture is visible.");
            var armour=catalog.Items.Single(i=>i.DisplayName=="Ancient Dread Body Armour");
            var armourScene=new ModelPreviewLoader(app).Load(armour,"Marauder",true,false,default);
            var corrected=armourScene.Meshes[1].Textures.OfType<BitmapSource>().Single();
            if(!armourScene.Meshes[1].Surfaces.Any(surface=>surface is {Normal:not null,ReflectanceMap:not null,NormalGlossAo:true,UsesRoughness:true}))
                throw new Exception("Ancient Dread PBR surface is missing one of its packed normal/roughness or metal maps.");
            if(!armourScene.Meshes[1].Surfaces.Any(surface=>surface.ColourGraphCount>0))
                throw new Exception("Ancient Dread must execute its linked material graph rather than relying on the previous hand-coded fallback.");
            const string armourTexture="Art/Models/Items/Armours/ArmourSets/AncientDread/Textures/AncientDreadArmourBody_colour_DXT5.dds";
            var basePixels=PoeMtxChanger.Core.Formats.Dds.DdsDecoder.Decode(archive.Read(armourTexture),p=>archive.Exists(p)?archive.Read(p):null).Bgra;
            var output=new byte[basePixels.Length];
            corrected.CopyPixels(output,corrected.PixelWidth*4,0);
            if(!output.Where((channel,i)=>i%4!=3&&channel>basePixels[i]).Any()
                ||output.Where((channel,i)=>i%4==3&&channel!=basePixels[i]).Any())
                throw new Exception("The albedo adjustment must brighten the material without modifying its alpha.");
            var stygian=catalog.Items.Single(i=>i.DisplayName=="Stygian Necrolord Helmet");
            var glow=new ModelPreviewLoader(app).Load(stygian,"Marauder",true,false,default).Meshes[1].Glow.OfType<BitmapSource>().Single();
            var glowPixels=new byte[glow.PixelWidth*glow.PixelHeight*4];
            glow.CopyPixels(glowPixels,glow.PixelWidth*4,0);
            if(!glowPixels.Where((alpha,i)=>i%4==3).Any(alpha=>alpha>0))throw new Exception("The Stygian helmet has no visible emissive texels.");
            Console.WriteLine("PASS: Ancient Dread albedo multiplier preserves alpha and Stygian Necrolord glow is visible.");
            return 0;
        }
        int gemPicker=Array.IndexOf(args,"--gem-picker");
        if(gemPicker>=0)
        {
            // Renders the wardrobe's gem effect picker for one skill (default cyclone) offscreen to a PNG.
            var wpf=new PoeMtxChanger.App.App();wpf.InitializeComponent();
            string skillId=args.Length>gemPicker+2&&!args[gemPicker+2].StartsWith("--")?args[gemPicker+2]:"cyclone";
            var skill=catalog.Skills.Single(s=>s.Id==skillId);
            var asm=typeof(BuffVisualWindow).Assembly;
            var choiceType=asm.GetType("PoeMtxChanger.App.Views.WardrobeChoice")!;
            var choices=Array.CreateInstance(choiceType,skill.Variants.Count);
            for(int i=0;i<skill.Variants.Count;i++){var v=skill.Variants[i];choices.SetValue(Activator.CreateInstance(choiceType,v.PickerName,v,v.IconPath??skill.IconPath,v.Summary),i);}
            Func<object,TargetSuggestion?,(bool Allowed,string Details)> describe=(_,_)=>(true,"");
            var pickerType=asm.GetType("PoeMtxChanger.App.Views.WardrobePicker")!;
            var ctor=pickerType.GetConstructors(System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Single();
            var picker=(Window)ctor.Invoke(["Gem effect · "+skillId,app.Thumbnails,choices,describe,null,null,null,null,null,"Save to profile",null,null]);
            picker.ShowActivated=false;picker.ShowInTaskbar=false;picker.WindowStartupLocation=WindowStartupLocation.Manual;picker.Left=-10000;picker.Top=-10000;picker.Width=860;picker.Height=760;
            picker.Show();Wait(Task.Delay(2500));picker.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            var content=(FrameworkElement)picker.Content;
            var shot=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);shot.Render(content);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(shot));
            using(var file=File.Create(args[gemPicker+1]))encoder.Save(file);
            picker.Close();Console.WriteLine("Gem picker saved: "+args[gemPicker+1]);return 0;
        }
        if(args.Contains("--ui"))
        {
            var wpf=new PoeMtxChanger.App.App();wpf.InitializeComponent();app.Game.BuffVisuals=catalog.Buffs;app.Game.BuffSkills=catalog.BuffSkills;
            var buffWindow=new BuffVisualWindow(app){ShowActivated=false,ShowInTaskbar=false,Left=-10000,Top=-10000};buffWindow.Show();
            buffWindow.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Find<Button>(buffWindow).Single(b=>b.Content as string=="Add to the active set").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved=app.Profiles.Active!.Entries.Single(e=>e.Key=="buff-visual:player_aura_cold_damage");
            if(saved.Mappings.Count!=1||!saved.Notes.Any(n=>n.Contains("EPKFiles1")))throw new Exception("Buff UI did not persist plan/caveat");
            if(saved.BuffSelection?.SkillId!="hatred")throw new Exception("Gem association was not saved.");
            if(new ProfileActions(app).Validate(app.Profiles.Active)!.HasErrors)throw new Exception("Valid saved aura plan was rejected.");
            var correctSelection=saved.BuffSelection;saved.BuffSelection=correctSelection with{SkillId="herald_of_ice"};
            if(!new ProfileActions(app).Validate(app.Profiles.Active)!.Issues.Any(i=>i.Code=="BUFF_COMPAT"))throw new Exception("Cross-gem saved plan was accepted.");
            saved.BuffSelection=correctSelection;
            var skillPicker=Find<ComboBox>(buffWindow).Single(c=>c.Name=="BuffSkillPicker");
            skillPicker.SelectedItem=catalog.BuffSkills.Single(s=>s.Id=="herald_of_ice");
            var visibleDonors=Find<ListBox>(buffWindow).Single(l=>l.Name=="CompatibleBuffVisuals").Items.OfType<BuffVisualDefinition>().ToArray();
            if(visibleDonors.Length==0||visibleDonors.Any(v=>v.SkillGroups.All(g=>g=="global_auras")))throw new Exception("Herald picker leaked universal aura effects.");
            Console.WriteLine("PASS gem-aware filtering and saved-plan preflight: universal aura effects excluded for Herald of Ice.");
            buffWindow.Close();Console.WriteLine("PASS aura window: actual selections, Add button, profile persistence and partial-role note.");
            var audioWindow=new AudioWindow(app){ShowActivated=false,ShowInTaskbar=false,Left=-10000,Top=-10000};audioWindow.Show();
            audioWindow.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);Wait(audioWindow.Ready);
            Find<Button>(audioWindow).Single(b=>b.Content as string=="Add to plan").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(app.Profiles.Active.AudioSwaps.Count!=1)throw new Exception("Audio UI did not persist a sample swap");
            audioWindow.Close();Console.WriteLine("PASS audio window: loaded banks, compatible sample selection, Add button, persisted plan; no Apply/Play action invoked.");
            if(FileUtil.Sha256OfFile(liveIndex)!=indexSha)throw new Exception("Live index changed");
            Console.WriteLine("Isolated profile: "+temporary);return 0;
        }
        if(args.Contains("--sets"))
        {
            // Complete looks: a name prefix with a helmet, body armour, gloves, boots and a back attachment.
            string Prefix(string name)=>System.Text.RegularExpressions.Regex.Replace(name,@"\s+(Helmet|Hood|Body Armour|Gloves|Boots|Back Attachment|Wings|Cloak)$","");
            var groups=catalog.Items.GroupBy(i=>Prefix(i.DisplayName)).Select(g=>(Name:g.Key,Cats:g.Select(i=>i.Category).ToHashSet(),Items:g.Select(i=>i.DisplayName).ToArray()))
                .Where(g=>new[]{CosmeticCategory.Helmet,CosmeticCategory.BodyArmour,CosmeticCategory.Gloves,CosmeticCategory.Boots}.All(g.Cats.Contains))
                .OrderByDescending(g=>g.Cats.Contains(CosmeticCategory.WingsAndBack)).ThenBy(g=>g.Name).ToArray();
            Console.WriteLine($"{groups.Length} complete armour sets");
            foreach(var g in groups.Take(80))Console.WriteLine($"  {g.Name} [{(g.Cats.Contains(CosmeticCategory.WingsAndBack)?"back":"-")}]: {string.Join(" | ",g.Items.Take(6))}");
            return 0;
        }
        if(args.Contains("--find-oodle"))
        {
            // Where automatic discovery finds Oodle on this computer, in order, and how long it takes.
            var clock=System.Diagnostics.Stopwatch.StartNew();
            var all=PoeMtxChanger.Core.Compression.OodleLocator.Candidates(AppContext.BaseDirectory).ToArray();
            Console.WriteLine($"{all.Length} candidates in {clock.ElapsedMilliseconds} ms");
            foreach(var (candidatePath,candidateSource) in all.Take(15))Console.WriteLine($"  {candidateSource}: {candidatePath}");
            clock.Restart();
            var found=PoeMtxChanger.Core.Compression.OodleLocator.FindAndLoad(AppContext.BaseDirectory);
            Console.WriteLine(found is {} f?$"loaded {f.Path} ({f.Source}) in {clock.ElapsedMilliseconds} ms":"none loaded");
            return 0;
        }
        if(args.Contains("--physics-params"))
        {
            // Distribution of the bone physics settings of ClientAnimationController over all objects.
            var values=new Dictionary<string,List<float>>(StringComparer.Ordinal);
            int files=0;
            foreach(var path in archive.EnumeratePaths().Where(p=>p.EndsWith(".ao",StringComparison.OrdinalIgnoreCase)))
            {
                string text;
                try{text=PoeMtxChanger.Core.Resources.ReferenceExtractor.DecodeText(archive.Read(path))??"";}catch{continue;}
                var block=System.Text.RegularExpressions.Regex.Match(text,@"ClientAnimationController\s*\{([^{}]*)\}");
                if(!block.Success||!block.Groups[1].Value.Contains("viscosity"))continue;
                files++;
                foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(block.Groups[1].Value,@"(\w+)\s*=\s*(-?[\d.]+)"))
                    if(float.TryParse(m.Groups[2].Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out float v))
                        (values.TryGetValue(m.Groups[1].Value,out var list)?list:values[m.Groups[1].Value]=[]).Add(v);
            }
            Console.WriteLine($"objects with bone physics: {files}");
            foreach(var (key,list) in values.OrderBy(k=>k.Key))
            {
                var sorted=list.Order().ToArray();
                Console.WriteLine($"  {key}: n={list.Count} min={sorted[0]:0.###} p50={sorted[sorted.Length/2]:0.###} max={sorted[^1]:0.###}; common: "+
                    string.Join(", ",list.GroupBy(v=>v).OrderByDescending(g=>g.Count()).Take(5).Select(g=>$"{g.Key:0.###}×{g.Count()}")));
            }
            return 0;
        }
        if(args.Contains("--buffs"))
        {
            var sch=DatSchema.LoadDefault();
            PoeMtxChanger.Core.Formats.Dat.DatTable T(string name)=>PoeMtxChanger.Core.Formats.Dat.DatTable.Parse(archive.Read(sch.Tables[name].GamePath),sch.Tables[name]);
            var mtxTypes=T("MtxTypes");var mtxGame=T("MtxTypeGameSpecific");var itemised=T("ItemisedVisualEffect");var baseItems=T("BaseItemTypes");
            int shown=0;
            for(int r=0;r<itemised.RowCount&&shown<2;r++)
            {
                int? g=itemised.GetRowRef(r,"Mtx");int? mtxTypeRow=g is {} gi?mtxGame.GetRowRef(gi,"Type"):null;
                if(mtxTypeRow is not {} ti||mtxTypes.GetString(ti,"Type")!="Aura Skin")continue;
                Console.WriteLine("Aura MTX whitelist "+mtxTypes.GetString(ti,"Name")+": "+string.Join(",",itemised.GetRowRefArray(r,"BaseItemTypes").OfType<int>().Select(i=>baseItems.GetString(i,"Name"))));shown++;
            }
            Console.WriteLine("Buff visuals: "+catalog.Buffs.Count);
            Console.WriteLine("Buff visuals with aura skin names: "+catalog.Buffs.Count(b=>b.CosmeticName!=null)+"; with icons: "+catalog.Buffs.Count(b=>b.IconPath!=null)
                +"; icons present in archive: "+catalog.Buffs.Count(b=>b.IconPath is {} icon&&archive.Exists(icon)));
            foreach(var missing in catalog.Buffs.Where(b=>!string.IsNullOrWhiteSpace(b.IconPath)&&!archive.Exists(b.IconPath)).Take(5))Console.WriteLine("MISSING ICON: "+missing.Id+" :: "+missing.IconPath);
            Console.WriteLine("Empty icon paths: "+catalog.Buffs.Count(b=>string.IsNullOrWhiteSpace(b.IconPath)));
            foreach(var named in catalog.Buffs.Where(b=>b.CosmeticName!=null).Take(8))Console.WriteLine("AURA SKIN: "+named.PickerName+" :: "+named.Id+" :: "+named.IconPath);
            foreach(var group in catalog.Items.GroupBy(i=>i.Category).OrderBy(g=>g.Key.ToString()))
            {
                int none=group.Count(i=>string.IsNullOrWhiteSpace(i.IconPath)),missing=group.Count(i=>!string.IsNullOrWhiteSpace(i.IconPath)&&!archive.Exists(i.IconPath!));
                if(none+missing>0)Console.WriteLine($"ITEM ICONS {group.Key}: {group.Count()} items, no icon {none}, icon not in archive {missing}; e.g. {string.Join("; ",group.Where(i=>string.IsNullOrWhiteSpace(i.IconPath)||!archive.Exists(i.IconPath!)).Take(3).Select(i=>i.DisplayName))}");
            }
            var skillVariants=catalog.Skills.SelectMany(s=>s.Variants).ToArray();
            Console.WriteLine($"Skill variants: {skillVariants.Length}; with shop names: {skillVariants.Count(v=>v.CosmeticName!=null)}; icons present: {skillVariants.Count(v=>v.IconPath is {} icon&&archive.Exists(icon))}");
            {
                // Same union as the wardrobe's gem picker: skill art variations plus buff skills.
                var gemIds=catalog.Skills.Select(s=>s.Id).Union(catalog.BuffSkills.Select(b=>b.Id),StringComparer.OrdinalIgnoreCase).ToArray();
                string GemName(string id)=>(catalog.Skills.FirstOrDefault(s=>s.Id==id)?.Name??catalog.BuffSkills.FirstOrDefault(b=>b.Id==id)?.Name??id).Split(" · ",2)[0];
                string? GemIcon(string id)=>catalog.Skills.FirstOrDefault(s=>s.Id==id)?.IconPath??catalog.BuffSkills.FirstOrDefault(b=>b.Id==id)?.IconPath;
                Console.WriteLine($"GEMS {gemIds.Length}; name is raw id: {gemIds.Count(id=>GemName(id)==id)}; no icon: {gemIds.Count(id=>string.IsNullOrWhiteSpace(GemIcon(id))||!archive.Exists(GemIcon(id)!))}");
                Console.WriteLine("RAW NAMES: "+string.Join(", ",gemIds.Where(id=>GemName(id)==id).Take(40)));
                Console.WriteLine("NO ICON: "+string.Join(", ",gemIds.Where(id=>string.IsNullOrWhiteSpace(GemIcon(id))||!archive.Exists(GemIcon(id)!)).Select(GemName).Take(40)));
                var gemNames=gemIds.Select(GemName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var skinTypes=catalog.Items.Where(i=>i.Category==CosmeticCategory.SkillEffect).Select(i=>i.MtxType).OfType<string>().Distinct().ToArray();
                var unmatched=skinTypes.Where(t=>!gemNames.Contains(System.Text.RegularExpressions.Regex.Replace(t,@"\s+(Skin|Effect)$",""))).ToArray();
                Console.WriteLine($"SKILL SKIN TYPES {skinTypes.Length}; without a gem of that name: {unmatched.Length}: "+string.Join(", ",unmatched.Take(60)));
            }
            foreach(var skill in catalog.Skills.Where(s=>s.Id=="cyclone"))foreach(var v in skill.Variants)Console.WriteLine("CYCLONE "+v.Number+": "+v.PickerName+" :: "+v.IconPath);
            Console.WriteLine("Gem-specific buff choices: "+catalog.BuffSkills.Count);
            foreach(var choice in catalog.BuffSkills)Console.WriteLine(choice.Name+" :: "+string.Join(",",choice.VisualGroups)+"; global aura="+choice.AllowsGlobalAuras);
            foreach(var entry in catalog.Buffs.Take(12))Console.WriteLine(entry.DisplayName);
            foreach(var entry in catalog.Buffs.Where(b=>b.Roles.Values.SelectMany(v=>v).Any(p=>p.Contains("hatred",StringComparison.OrdinalIgnoreCase))).Take(12))Console.WriteLine("HATRED FILE: "+entry.DisplayName);
            foreach(var buff in catalog.Buffs.Where(b=>b.DisplayName.Contains("hatred",StringComparison.OrdinalIgnoreCase)))Console.WriteLine(buff.DisplayName+" :: "+string.Join(" | ",buff.Roles.Select(r=>r.Key+"="+string.Join(",",r.Value))));
            var candidates=catalog.Buffs.Where(b=>b.DisplayName.Contains("hatred",StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach(var original in candidates.Where(c=>c.Id=="player_aura_cold_damage"))foreach(var buffDonor in candidates.Where(c=>c.Id!="player_aura_cold_damage"))
            {var buffPlan=BuffVisualCatalogBuilder.Plan(original,buffDonor,archive);Console.WriteLine(original.Id+" → "+buffDonor.Id+": "+buffPlan.Summary);if(buffPlan.CanApply){var report=new MappingValidator(ResourcePolicy.Default).Validate(buffPlan.Mappings,archive);Console.WriteLine("Dependencies: errors="+report.Issues.Count(i=>i.Severity==IssueSeverity.Error));}}
            return 0;
        }
        Console.WriteLine($"Skill roles: {catalog.Skills.Count} skills; {catalog.Skills.Sum(s=>s.Variants.Count(v=>v.CanApply))} applicable variants; beam={catalog.Skills.Sum(s=>s.Variants.Sum(v=>v.Mappings.Count(m=>m.Note?.StartsWith("beam:")==true)))}; packs={catalog.Skills.Sum(s=>s.Variants.Sum(v=>v.Mappings.Count(m=>m.Note?.StartsWith("pack:")==true)))}");
        foreach(var cls in CharacterClasses.All)
        {
            var scene=new ModelPreviewLoader(app).Load(null,cls.Key,true,false,default);
            var skeleton=scene.Skeleton!;var clip=skeleton.Clips.FirstOrDefault(c=>c.Name=="idle_unarmed")??skeleton.Clips.First(c=>c.Name.Contains("idle"));
            float duration=skeleton.Duration(clip);
            var mesh=scene.Meshes[0].Geometry;var a=mesh.Positions(skeleton.Skinning(clip,0));var b=mesh.Positions(skeleton.Skinning(clip,Math.Min(duration*.5f,.6f)));
            if(!a.Where((v,i)=>Vector3.DistanceSquared(v,b[i])>.00001f).Any())throw new Exception(cls.Key+": pose did not move");
            if(scene.Meshes[0].Textures.All(t=>t==null))throw new Exception(cls.Key+": body texture did not decode");
            Console.WriteLine($"PASS {cls.Key}: {mesh.Vertices.Length} vertices, {skeleton.Bones.Length} bones, idle {duration:0.000}s; pose changed; body texture decoded");
        }
        var helmet=catalog.Items.Single(i=>i.DisplayName=="Seraph Helmet");
        var dressed=new ModelPreviewLoader(app).Load(helmet,"Marauder",true,false,default);
        if(dressed.Meshes.Count!=2)throw new Exception("Helmet not attached.");
        Console.WriteLine("Dressed character: "+dressed.Description);

        string copyRoot=Path.Combine(temporary,"copy"),audio=Path.Combine(copyRoot,"FMOD","Desktop");Directory.CreateDirectory(audio);
        string target="AuspiciousCycloneMTX.bank",donor="CelestialCycloneMTX.bank";
        var hashes=new Dictionary<string,string>();foreach(string name in new[]{target,donor})
        {string original=Path.Combine(game,"FMOD","Desktop",name);hashes[name]=FileUtil.Sha256OfFile(original);File.Copy(original,Path.Combine(audio,name));}
        var engine=new AudioBankEngine(copyRoot,Path.Combine(temporary,"audio-backups"),()=>false);
        var t=new FmodBank(engine.ReadClean(target));var d=new FmodBank(engine.ReadClean(donor));
        var pair=(from ts in t.Fsb.Samples from ds in d.Fsb.Samples where FsbSoundBank.Compatible(ts,ds)&&FileUtil.Sha256(ts.Payload)!=FileUtil.Sha256(ds.Payload) select(Target:ts,Donor:ds)).First();
        var plan=engine.Plan(target,pair.Target.Index,donor,pair.Donor.Index);engine.Apply(target,[plan],true);
        var changed=new FmodBank(File.ReadAllBytes(Path.Combine(audio,target)));t.AssertMetadataPreserved(changed.Raw);
        if(!changed.Fsb.Samples[pair.Target.Index].Payload.AsSpan().StartsWith(pair.Donor.Payload))throw new Exception("Audio donor mismatch");
        string fsb=Path.Combine(temporary,"preview.fsb");File.WriteAllBytes(fsb,changed.Fsb.Raw);
        using(var decoder=new FmodPcmDecoder(Path.Combine(game,"fmod.dll")))
        {byte[] wave=decoder.DecodeWave(fsb,changed.Fsb.Samples[pair.Target.Index]);File.WriteAllBytes(Path.Combine(temporary,"preview.wav"),wave);if(wave.Length<100)throw new Exception("Empty decoded preview");Console.WriteLine("PASS native PCM preview: "+wave.Length+" bytes");}
        engine.Restore(target,true);if(FileUtil.Sha256OfFile(Path.Combine(audio,target))!=hashes[target])throw new Exception("Audio restore mismatch");
        Console.WriteLine("PASS individual audio apply/readback/exact restore on a copied bank; event metadata preserved.");
        // Real index and real compressed bundles in a small GGPK fixture, rather than synthetic bundle bytes.
        var portalMapping=SkinMappingResolver.Resolve(catalog.Suggestions.Single(s=>s.SlotKey=="portal"),null,catalog.Items.Single(i=>i.DisplayName=="Abyss Portal Effect"),archive).Mappings[0];
        string portal=portalMapping.TargetPath,source=portalMapping.SourcePath;
        var names=new[]{portal,source}.Select(p=>{archive.TryGetRecord(p,out var r);return archive.Index.Bundles[r.BundleIndex].Name;}).Distinct().ToArray();
        string ggpk=Path.Combine(temporary,"fixture.ggpk");var inputs=new List<GgpkInputFile>{GgpkInputFile.FromDisk("Bundles2/_.index.bin",liveIndex)};
        foreach(string name in names)inputs.Add(GgpkInputFile.FromDisk("Bundles2/"+name+".bundle.bin",Path.Combine(game,"Bundles2",name.Replace('/',Path.DirectorySeparatorChar)+".bundle.bin")));
        using(var stream=File.Create(ggpk))GgpkRebuilder.Write(stream,3,inputs);
        string originalGgpk=FileUtil.Sha256OfFile(ggpk);
        var standalone=new StandalonePatchEngine(ggpk,app.Game.Codec,Path.Combine(temporary,"ggpk-backup"),()=>false);
        standalone.Apply(new PatchRequest{UserConfirmed=true,AnalyzeDependencies=false,ProfileName="real-resource fixture",Mappings=[new(){TargetPath=portal,SourcePath=source}]});
        using(var patched=GameArchive.Open(new GgpkBundleStorage(ggpk),app.Game.Codec))if(FileUtil.Sha256(patched.Read(portal))!=FileUtil.Sha256(archive.Read(source)))throw new Exception("GGPK readback mismatch");
        standalone.RestoreAsync(true).GetAwaiter().GetResult();if(FileUtil.Sha256OfFile(ggpk)!=originalGgpk)throw new Exception("GGPK restore mismatch");
        Console.WriteLine("PASS GGPK with real compressed index/bundles: apply/readback/exact restore. Not a real standalone-install acceptance test.");
        if(FileUtil.Sha256OfFile(liveIndex)!=indexSha)throw new Exception("Live index changed externally");
        foreach(var (name,hash) in hashes)if(FileUtil.Sha256OfFile(Path.Combine(game,"FMOD","Desktop",name))!=hash)throw new Exception("Live audio changed externally");
        Console.WriteLine("PASS live input hashes unchanged. Scratch: "+temporary);return 0;
    }
    private static void CheckWardrobeFrame(AppServices app,string path,bool checkRibbon=false)
    {
        if(Application.Current==null)
        {
            var wpf=new PoeMtxChanger.App.App { ShutdownMode=ShutdownMode.OnExplicitShutdown };
            wpf.InitializeComponent();
        }
        var window=new MainWindow
        {
            DataContext=new MainViewModel(app){SelectedTab=MainTab.Wardrobe},
            ShowActivated=false,ShowInTaskbar=false,Left=-10000,Top=-10000,WindowStartupLocation=WindowStartupLocation.Manual
        };
        window.Show();
        try
        {
            var view=(WardrobeView)window.FindName("WardrobePage");
            WriteableBitmap? Frame()=>Find<Image>(view).SingleOrDefault(i=>i.Name=="Direct3DFrame")?.Source as WriteableBitmap;
            for(int attempt=0;attempt<120&&Frame()==null;attempt++)Wait(Task.Delay(250));
            if(Frame()==null)throw new Exception("The wardrobe viewport did not produce a Direct3D frame.");
            byte[] ReadFrame()
            {
                var frame=Frame()??throw new Exception("The wardrobe fell back from Direct3D.");
                var pixels=new byte[frame.PixelWidth*frame.PixelHeight*4];
                frame.CopyPixels(pixels,frame.PixelWidth*4,0);
                return pixels;
            }
            var baseline=ReadFrame();
            if(baseline.Chunk(4).Count(pixel=>Math.Max(pixel[0],Math.Max(pixel[1],pixel[2]))>100)<1000)
                throw new Exception("The Direct3D preview has insufficient visible geometry.");
            var animation=Find<CheckBox>(view).Single(check=>check.Content as string=="Animation");
            animation.IsChecked=false;
            var motion=Find<Button>(view).Single(button=>button.ToolTip as string=="Check the cape and trails in motion");
            if(!motion.IsEnabled)throw new Exception("The run animation shortcut is unavailable.");
            motion.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(motion.Content as string!="Stance")throw new Exception("The motion shortcut did not choose the run clip.");
            if(checkRibbon)
            {
                static int BlueTrail(byte[] bytes)
                {
                    int blue=0;
                    for(int i=0;i<bytes.Length;i+=4)
                        if(bytes[i]>108&&bytes[i]-bytes[i+2]>30)blue++;
                    return blue;
                }
                int idle=BlueTrail(baseline);
                int mostBlue=idle+24;
                BitmapSource? running=null;
                for(int tick=0;tick<12;tick++)
                {
                    Wait(Task.Delay(82));
                    int blue=BlueTrail(ReadFrame());
                    if(blue<=mostBlue)continue;
                    mostBlue=blue;
                    running=CaptureWindow();
                }
                if(running==null)throw new Exception("The Sin ribbon did not show up in any rendered running frame.");
                SaveFrame(running);
                Console.WriteLine($"PASS live Sin ribbon: {mostBlue-idle} additional blue pixels on the running sword in the wardrobe.");
                return;
            }
            motion.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            animation.IsChecked=false;
            var lighting=Find<ComboBox>(view).Single(combo=>combo.ToolTip as string=="Wardrobe lighting");
            lighting.SelectedIndex=1;
            var warmed=ReadFrame();
            if(warmed.Where((component,i)=>Math.Abs(component-baseline[i])>10).Count()<1000)
                throw new Exception("The lighting preset did not change the rendered material response.");
            lighting.SelectedIndex=0;
            Wait(Task.Delay(250));
            window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            SaveFrame(CaptureWindow());
            var frame=Frame()??throw new Exception("The wardrobe fell back from Direct3D.");
            Console.WriteLine($"PASS live HDR Direct3D wardrobe: rendered {frame.PixelWidth}×{frame.PixelHeight}, lighting presets change actual pixels.");
            BitmapSource CaptureWindow()
            {
                var content=(FrameworkElement)window.Content;
                var dpi=VisualTreeHelper.GetDpi(content);
                var bitmap=new RenderTargetBitmap((int)(content.ActualWidth*dpi.DpiScaleX),(int)(content.ActualHeight*dpi.DpiScaleY),
                    dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
                bitmap.Render(content);
                return bitmap;
            }
            void SaveFrame(BitmapSource bitmap)
            {
                var encoder=new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output=File.Create(path);
                encoder.Save(output);
            }
        }
        finally { window.Close(); }
    }

    private static IEnumerable<T> Find<T>(DependencyObject parent)where T:DependencyObject
    {
        if(parent is T found)yield return found;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)foreach(var child in Find<T>(VisualTreeHelper.GetChild(parent,i)))yield return child;
    }
    private static void Wait(Task task)
    {
        if(!task.IsCompleted){var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;_=task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);Dispatcher.PushFrame(frame);}
        task.GetAwaiter().GetResult();
    }
}
