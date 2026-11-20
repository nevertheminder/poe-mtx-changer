using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using PoeMtxChanger.App.Services;
using PoeMtxChanger.App.ViewModels;
using PoeMtxChanger.App.Views;
using PoeMtxChanger.Core.Profiles;

internal static partial class Program
{
    private static void CheckShieldAndAura(AppServices app,string directory)
    {
        var profile=app.Profiles.Active!;
        var skin=app.Game.Catalog.Single(item=>item.DisplayName=="Arcane Celestial Shield");
        profile.WeaponSetup.StanceId="sword_shield";
        profile.WeaponSetup.OffBasePath="Metadata/Items/Armours/Shields/ShieldStr1/ShieldStr1Held.ao";
        profile.GetOrAddEntry(ProfileEntryKind.EquipmentSlot,"shield").CosmeticId=skin.Id;
        var scene=new ModelPreviewLoader(app).Load(null,"Marauder",true,true,default,showWeapons:true);
        var shield=scene.Meshes.Single(mesh=>mesh.MountBone>=0&&mesh.Geometry.Vertices.Length>0&&
            scene.Skeleton!.Bones[mesh.MountBone].Name.Equals("L_Weapon",StringComparison.OrdinalIgnoreCase));
        if(shield.Emitters.All(emitter=>emitter.GameShader==null)||shield.Trails.Count<3)
            throw new InvalidOperationException($"Arcane Celestial Shield has {shield.Emitters.Count} emitters and {shield.Trails.Count} trails. {scene.Description}");
        if(shield.LocalIdle?.Name!="loop"||shield.LocalIdleSeconds<=0)
            throw new InvalidOperationException($"The shield's effect anchors must animate. {scene.Description}");
        var firstPose=shield.LocalPose(0)!;
        var laterPose=shield.LocalPose(shield.LocalIdleSeconds/4)!;
        if(!shield.Trails.Any(trail=>trail.Anchor.Bone>=0&&Vector3.DistanceSquared(
            Vector3.Transform(trail.Anchor.Position,firstPose[trail.Anchor.Bone]),
            Vector3.Transform(trail.Anchor.Position,laterPose[trail.Anchor.Bone]))>.1f))
            throw new InvalidOperationException("The shield's trail anchors did not move with its local animation.");
        var standalone=new ModelPreviewLoader(app).Load(skin,"Marauder",false,false,default).Meshes[0];
        if(standalone.Emitters.Count!=shield.Emitters.Count||standalone.Trails.Count!=shield.Trails.Count)
            throw new InvalidOperationException("The standalone shield lost a built-in effect layer.");
        Console.WriteLine($"PASS Arcane Celestial Shield: {shield.Emitters.Count} built-in emitters and {shield.Trails.Count} trails in wardrobe and catalog.");

        Directory.CreateDirectory(directory);
        CaptureReferenceWardrobe(app,Path.Combine(directory,"arcane-celestial-shield.png"),gameAngle:false,muted:false);

        profile.GetOrAddEntry(ProfileEntryKind.EquipmentSlot,"shield").CosmeticId=null;
        var wrath=app.Game.BuffSkills.Single(skill=>skill.Id=="wrath");
        var original=app.Game.BuffVisuals.First(visual=>visual.SkillGroups.Intersect(wrath.VisualGroups,StringComparer.OrdinalIgnoreCase).Any());
        profile.WardrobeSkills.Add(wrath.Id);
        var aura=profile.GetOrAddEntry(ProfileEntryKind.SkillEffect,"buff-visual:"+original.Id);
        aura.BuffSelection=new(wrath.Id,original.Id,"player_aura_lightbringer");
        aura.CosmeticName="Lightbringer Aura Effect";
        var equipped=new ModelPreviewLoader(app).Load(null,"Marauder",true,true,default,showWeapons:true);
        var flowers=equipped.Meshes.SelectMany(mesh=>mesh.Emitters).Where(emitter=>
            emitter.Definition is {ParticleDuration:5} definition&&
            definition.Material.EndsWith("/nightbane_lightbringer/rune_flower.mat",StringComparison.OrdinalIgnoreCase)&&
            definition.ParticlesPerSecond.Max()<.3f).ToArray();
        if(flowers.Length!=4||flowers.Any(emitter=>emitter.GameShader is not {BlendState:2 or 4}))
            throw new InvalidOperationException($"Lightbringer loaded {flowers.Length} of the four additive flower layers.");
        Console.WriteLine("PASS Lightbringer: four golden flower emitters with additive materials.");
        CaptureReferenceWardrobe(app,Path.Combine(directory,"lightbringer.png"),gameAngle:true,muted:false);

        CheckReferenceWardrobe(app,directory,["--game-reference-only","--sword-and-shield","--arcane-shield"]);
    }

    private static void CheckReferenceWardrobe(AppServices app, string directory, string[] options)
    {
        Directory.CreateDirectory(directory);
        var profile = app.Profiles.Active!;
        string ItemId(string name) => app.Game.Catalog.Single(item => item.DisplayName.Replace('’', '\'') == name.Replace('’', '\'')).Id;
        void Equip(string slot, string? name) =>
            profile.GetOrAddEntry(ProfileEntryKind.EquipmentSlot, slot).CosmeticId = name == null ? null : ItemId(name);

        int showcase = Array.IndexOf(options, "--showcase");
        if (showcase >= 0)
        {
            // A full shop set for documentation screenshots: "<set name prefix>" (helmet, body, gloves, boots, wings/back).
            string set = options[showcase + 1];
            foreach (var (slot, suffixes) in new[] { ("helmet", new[] { "Helmet", "Hood" }), ("body", new[] { "Body Armour" }),
                ("gloves", new[] { "Gloves" }), ("boots", new[] { "Boots" }), ("back", new[] { "Wings", "Back Attachment", "Cloak" }) })
            {
                var item = suffixes.Select(s => app.Game.Catalog.FirstOrDefault(i => i.DisplayName == $"{set} {s}")).FirstOrDefault(i => i != null);
                if (item != null) profile.GetOrAddEntry(ProfileEntryKind.EquipmentSlot, slot).CosmeticId = item.Id;
                Console.WriteLine($"showcase {slot}: {item?.DisplayName ?? "(none)"}");
            }
            profile.WardrobeSkills.Clear();
            app.Profiles.SaveActive();
            CaptureCloth(app, directory);
            return;
        }
        Equip("helmet", "Stygian Necrolord Helmet");
        Equip("body", "Ancient Dread Body Armour");
        Equip("back", "Arctic Crystal Cloak");
        Equip("boots", "Stygian Necrolord Boots");
        if(!profile.WardrobeSkills.Contains("wrath"))profile.WardrobeSkills.Add("wrath");
        var poses = new[]
        {
            ("angelic-atlas", "mace", "Automaton Sword", (string?)null, "Atlas Core Weapon Effect", "Angelic Character Effect"),
            ("vaal-aspirant", "dual_swords", null, "Darkprism Weapon", "Aspirant Weapon Effect", "Vaal Orb Character Effect"),
            ("game-reference", "dual_swords", "Iron Count's Zweihander", null, "Blood-crazed Pyromaniac Weapon Effect", "Vaal Orb Character Effect")
        };
        foreach (var (name, stance, main, off, weaponEffect, charEffect) in poses)
        {
            if (options.Contains("--game-reference-only") && name != "game-reference") continue;
            bool game = name == "game-reference";
            if (game)
            {
                Equip("helmet", "Hallowed Cryptkeeper Hood");
                Equip("helmet_attachment", "Hallowed Cryptkeeper Helmet Attachment");
                Equip("body", "Stygian Necrolord Body Armour");
                Equip("gloves", "Stygian Necrolord Gloves");
                // --back "<name>" swaps the reference back attachment (e.g. to check another cape's physics).
                int backOption = Array.IndexOf(options, "--back");
                Equip("back", backOption >= 0 ? options[backOption + 1] : "Lord of Ogham Back Attachment");
                profile.VisualTiers[ItemId("Lord of Ogham Back Attachment")] = 1;
                var wrath = app.Game.BuffSkills.Single(s => s.Id == "wrath");
                var original = app.Game.BuffVisuals.First(v => v.SkillGroups.Intersect(wrath.VisualGroups, StringComparer.OrdinalIgnoreCase).Any());
                var aura = profile.GetOrAddEntry(ProfileEntryKind.SkillEffect, "buff-visual:" + original.Id);
                aura.BuffSelection = new("wrath", original.Id, "player_aura_lightbringer");
                aura.CosmeticName = "Lightbringer Aura Effect";
            }
            bool shield = game && options.Contains("--sword-and-shield");
            profile.WeaponSetup.StanceId = shield ? "sword_shield" : stance;
            if (shield) Equip("shield", options.Contains("--arcane-shield")?"Arcane Celestial Shield":"Badge of Skulls");
            profile.WeaponSetup.MainBasePath = stance == "dual_swords" && !game
                ? "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/Heist1HSwordHeld.ao" : null;
            profile.WeaponSetup.OffBasePath = shield ? "Metadata/Items/Armours/Shields/ShieldStr1/ShieldStr1Held.ao" : stance == "dual_swords"
                ? "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/Basetypes/OneHandSword1/OneHandSword1Held.ao" : null;
            profile.WeaponSetup.MainEffectId = options.Contains("--omit-weapon-effect") ? null : ItemId(weaponEffect);
            profile.WeaponSetup.OffEffectId = game && !options.Contains("--omit-weapon-effect") ? ItemId("Pyromaniac Weapon Effect") : null;
            Equip("weapon_main", main);
            Equip("weapon_off", off);
            Equip("character_effect", options.Contains("--omit-character-effect") ? null : charEffect);
            var scene = new ModelPreviewLoader(app).Load(null, "Marauder", true, true, default, showWeapons: true);
            Console.WriteLine($"{name}: {scene.Description}");
            if(game&&!options.Contains("--back"))
            {
                var cloak=scene.Meshes.Single(mesh=>mesh.Path.EndsWith("/BetaBaronCloak.sm",StringComparison.OrdinalIgnoreCase));
                // The in-game reference wears tier 1 (black feathers); tier 2 (red cloth) is the asset's fallback.
                if(cloak.LocalIdle?.Name!="str_town"||cloak.LocalRun?.Name!="str_run"||
                    cloak.HiddenShapes.Contains("BaronCloakBeta01B_OpShape")||!cloak.HiddenShapes.Contains("Cloak_AShape"))
                    throw new InvalidOperationException($"Lord of Ogham cloak has wrong tier or clips: {cloak.LocalIdle?.Name}, {cloak.LocalRun?.Name}.");
                Console.WriteLine("PASS Lord of Ogham tier 1 (feathers) from the profile, class idle and run.");
            }
            if(game&&options.Contains("--head-check"))
            {
                CaptureHead(app,scene,Path.Combine(directory,"hood-closeup.png"));
                var hoodMesh=scene.Meshes.Single(mesh=>mesh.Path.EndsWith("/HallowedCryptPlateHelmetAltStr.sm",StringComparison.OrdinalIgnoreCase));
                if(hoodMesh.RenderPasses is not [{Shader.BlendState:7} pass]||
                    !pass.Path.EndsWith("/Hood_BlurPass_01.mat",StringComparison.OrdinalIgnoreCase)||pass.Textures.Count(t=>t!=null)!=2)
                    throw new InvalidOperationException("Hallowed Cryptkeeper Hood is missing its authored standalone render pass.");
                Console.WriteLine("PASS authored hood refraction pass and its two masks.");
                var body=scene.Meshes[0];
                string[] replaced=["warrior_headShape","warrior_hairShape","warrior_neckShape"];
                var exposed=body.Geometry.Shapes.Where(part=>replaced.Contains(part.Name,StringComparer.OrdinalIgnoreCase)&&!body.HiddenShapes.Contains(part.Name)).ToArray();
                Console.WriteLine($"Hood: {exposed.Sum(part=>part.TriangleCount)} exposed triangles on replaced Marauder head, hair or neck.");
                if(exposed.Length>0)throw new InvalidOperationException("The old head is still visible through Hallowed Cryptkeeper Hood.");
                var proxy=scene.Meshes.Single(mesh=>mesh.Path.EndsWith("/BaseCharacterHeadStr.sm",StringComparison.OrdinalIgnoreCase));
                string[] insideHood=["eyes_meshShape","head_meshShape","neck_meshShape"];
                if(insideHood.Any(name=>!proxy.HiddenShapes.Contains(name))||
                    scene.Meshes.Any(mesh=>mesh.Path.EndsWith("/BaseCharacterHairStr.sm",StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The hood must hide the replacement head segments and its hair attachment.");
                var bare=new ModelPreviewLoader(app).Load(null,"Marauder",true,false,default).Meshes[0];
                if(replaced.Any(name=>bare.HiddenShapes.Contains(name)))
                    throw new InvalidOperationException("The unequipped character must keep the original head, hair and neck.");
                var hood=app.Game.Catalog.Single(item=>item.DisplayName=="Hallowed Cryptkeeper Hood");
                foreach(var (character,suffix) in new[]
                {
                    ("Ranger","Dex"),("Witch","Int"),("Duelist","StrDex"),("Templar","StrInt"),("Shadow","DexInt"),("Scion","StrDexInt")
                })
                {
                    var variant=new ModelPreviewLoader(app).Load(hood,character,true,false,default);
                    if(!variant.Meshes.Any(mesh=>mesh.Path.EndsWith($"/BaseCharacterHead{suffix}.sm",StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"{character} did not resolve the matching head variant.");
                }
                Console.WriteLine("PASS head-controller variants for all seven character classes.");
                return;
            }
            bool fullSet=!options.Contains("--omit-weapon-effect")&&!options.Contains("--omit-character-effect");
            if(fullSet&&name=="angelic-atlas"&&(
                !scene.Meshes.SelectMany(m=>m.Emitters).Any(e=>e.Definition?.Material.EndsWith("/atlas/flames_blue.mat",StringComparison.OrdinalIgnoreCase)==true&&e.Definition.ParticlesPerSecond.Max()>0)
                ||!scene.Meshes.SelectMany(m=>m.Emitters).Any(e=>e.Definition?.Material.EndsWith("/surges.mat",StringComparison.OrdinalIgnoreCase)==true&&e.ParticleMesh!=null)))
                throw new InvalidOperationException("Atlas PET v3 or an Angelic mesh particle failed to load.");
            if(fullSet&&game)
            {
                if(!scene.Meshes.Any(m=>m.Path.Contains("IronCountsZweihander",StringComparison.OrdinalIgnoreCase))||
                    !scene.Meshes.Any(m=>m.Path.Contains("HallowedCryptPlateHelmAttach",StringComparison.OrdinalIgnoreCase))||
                    !scene.Meshes.Any(m=>m.Path.Contains("VaalOrbCharacter.sm",StringComparison.OrdinalIgnoreCase)&&m.LocalIdle!=null&&m.Trails.Count>=8&&m.ActorOriginZ.HasValue)||
                    !scene.Meshes.Any(m=>m.ParentMount?.GroundOffsetZ.HasValue==true&&m.Emitters.Any(e=>e.ParticleMesh!=null)))
                    throw new InvalidOperationException("The game reference outfit is missing a sword, helmet attachment, Vaal animation or grounded aura.");
                if (!scene.Meshes.SelectMany(mesh => mesh.Emitters).Any(emitter =>
                    emitter.Definition?.Material.EndsWith("/shim_Rad2.mat", StringComparison.OrdinalIgnoreCase) == true &&
                    emitter.GameShader is { BlendState: 7 }))
                    throw new InvalidOperationException("Blood-crazed Pyromaniac did not load its refraction material.");
                if (shield && !scene.Meshes.Any(mesh => mesh.MountFromOrigin && mesh.Path.Contains("Shield", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The reference shield is absent from the wardrobe scene.");
            }
            if (options.Contains("--phys-report"))
            {
                // Which meshes carry physics bones (phys_*), on which skeleton, and how the bones chain.
                void Report(string owner, PoeMtxChanger.Core.Formats.Geometry.AnimationSkeleton? rig)
                {
                    if (rig == null) return;
                    var phys = rig.Bones.Select((b, i) => (b, i)).Where(x => x.b.Name.StartsWith("phys_", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (phys.Length == 0) return;
                    var roots = phys.Where(x => x.b.Parent < 0 || !rig.Bones[x.b.Parent].Name.StartsWith("phys_", StringComparison.OrdinalIgnoreCase)).ToArray();
                    Console.WriteLine($"PHYS {owner}: {phys.Length} phys bones of {rig.Bones.Length}, {roots.Length} chains; roots: " +
                        string.Join(", ", roots.Take(12).Select(r => r.b.Name + "<-" + (r.b.Parent >= 0 ? rig.Bones[r.b.Parent].Name : "-"))));
                    Console.WriteLine("     names: " + string.Join(" ", phys.Take(40).Select(x => x.b.Name)));
                }
                Report("BODY " + scene.Meshes[0].Path, scene.Skeleton);
                System.Numerics.Vector3 Centre(PreviewMesh m) { var p = m.Pose(scene.Skeleton, null, 0); return p.Aggregate(System.Numerics.Vector3.Zero, (a, b) => a + b) / Math.Max(1, p.Length); }
                var bodyCentre = Centre(scene.Meshes[0]);
                var bodyPoints = scene.Meshes[0].Pose(scene.Skeleton, null, 0);
                Console.WriteLine($"BODY centre {bodyCentre}; Z {bodyPoints.Min(p => p.Z):F1}…{bodyPoints.Max(p => p.Z):F1}");
                foreach (var mesh in scene.Meshes.Where(m => m.LocalRig != null && m.LocalRig.Bones.Any(b => b.Name.StartsWith("phys_", StringComparison.OrdinalIgnoreCase))))
                    Console.WriteLine($"CENTRE {Path.GetFileName(mesh.Path)} − body = {Centre(mesh) - bodyCentre}");
                foreach (var mesh in scene.Meshes)
                {
                    Report(mesh.Path, mesh.LocalRig);
                    int bodyPhys = scene.Skeleton == null || mesh.LocalRig != null ? 0 : mesh.Geometry.Vertices.SelectMany(v => v.Bones.Zip(v.Weights))
                        .Where(bw => bw.Second > 0 && bw.First < scene.Skeleton.Bones.Length && scene.Skeleton.Bones[bw.First].Name.StartsWith("phys_", StringComparison.OrdinalIgnoreCase))
                        .Select(bw => bw.First).Distinct().Count();
                    if (bodyPhys > 0) Console.WriteLine($"SKIN {mesh.Path}: weighted to {bodyPhys} phys bones of the body skeleton; mount bone {mesh.MountBone}");
                }
                continue;
            }
            if (options.Contains("--layers"))
            {
                foreach (var mesh in scene.Meshes)
                {
                    Console.WriteLine($"  {mesh.Path}: {mesh.Geometry.Vertices.Length} vertices; {mesh.Emitters.Count} emitters; {mesh.Trails.Count} trails");
                    foreach (var surface in mesh.Surfaces.DistinctBy(s => s.MaterialPath))
                        Console.WriteLine($"    surface {Path.GetFileName(surface.MaterialPath)}: game shader {(surface.GameShader != null ? surface.GameShader.BlendState.ToString() : "none")}, extra passes {surface.ExtraPasses.Length}");
                    if(mesh.GroundOffsetZ.HasValue||mesh.ParentMount?.GroundOffsetZ.HasValue==true)
                        Console.WriteLine($"    Ground {mesh.GroundOffsetZ}; Parent {mesh.ParentMount?.GroundOffsetZ}; mount {mesh.MountTransform(scene.Skeleton,null).Translation}; frame {mesh.LocalIdle?.Name}");
                    if(mesh.Path.Contains("VaalOrbCharacter",StringComparison.OrdinalIgnoreCase))
                    {
                        var mount=mesh.MountTransform(scene.Skeleton,null).Translation;
                        string range=string.Join(" | ",new[] { .25f,.5f,1f }.Select(t=>
                        {
                            var points=mesh.Pose(scene.Skeleton,null,t);
                            return $"{t}: {points.Min(p=>p.Z):F1}…{points.Max(p=>p.Z):F1}";
                        }));
                        var body=scene.Meshes[0].Geometry.Vertices;
                        Console.WriteLine($"    Vaal mount {mount} — body {body.Min(p=>p.Position.Z):F1}…{body.Max(p=>p.Position.Z):F1}; poses {range}");
                    }
                    foreach (var group in mesh.Emitters.GroupBy(e => e.Definition?.Material))
                    {
                        var e = group.First();
                        Console.WriteLine($"    {group.Key ?? "(legacy)"} ×{group.Count()}: {e.Definition?.Kind}; {e.Definition?.Blend} → {e.GameShader?.BlendState.ToString() ?? "fallback"}; size {e.Definition?.Scale.Max():F1}, rate {e.Definition?.ParticlesPerSecond.Max():F1}; mesh {e.Definition?.Mesh}; anchors {e.Anchors.Count}");
                        if(mesh.ParentMount?.GroundOffsetZ.HasValue==true)
                            Console.WriteLine("      "+string.Join(" | ",e.Anchors.Select(a=>a.Position)));
                    }
                }
            }
            int layerShots = Array.IndexOf(options, "--layer-shots");
            if (layerShots >= 0)
            {
                int at = Array.IndexOf(options, "--layer-time");
                float seconds = at >= 0 ? float.Parse(options[at + 1], System.Globalization.CultureInfo.InvariantCulture) : 3f;
                CaptureLayers(app, scene, Path.Combine(directory, "layers"), options[layerShots + 1], seconds, options.Contains("--muted"));
                continue;
            }
            bool comparePost=options.Contains("--compare-reference-post");
            if(comparePost)
            {
                if(scene.ReferenceExposure is not >0)
                    throw new InvalidOperationException("The reference environment's camera exposure did not load.");
                Console.WriteLine($"Reference exposure from 2_3_2_Default.env: {scene.ReferenceExposure:F5}");
            }
            if(options.Contains("--fps")){MeasureWardrobeFrames(app);continue;}
            if(options.Contains("--cloth-shots")){CaptureCloth(app,directory);continue;}
            int white=CaptureReferenceWardrobe(app, Path.Combine(directory, name + ".png"), game,
                muted: options.Contains("--muted"), comparePost: comparePost);
            if(fullSet&&name=="vaal-aspirant"&&white>2500)
                throw new InvalidOperationException("Aspirant overexposed the character in the wardrobe.");
        }
    }

    /// <summary>Equips an object whose skeleton carries an authored light and reports and renders it.</summary>
    internal static void CheckEffectLights(AppServices app,string directory,string resourceFragment,string slot)
    {
        Directory.CreateDirectory(directory);
        var item=app.Game.Catalog.First(i=>i.PrimaryResources.Any(p=>p.Contains(resourceFragment,StringComparison.OrdinalIgnoreCase)));
        app.Profiles.Active!.GetOrAddEntry(ProfileEntryKind.EquipmentSlot,slot).CosmeticId=item.Id;
        var scene=new ModelPreviewLoader(app).Load(null,"Marauder",true,true,default,showWeapons:true);
        var lit=scene.Meshes.Where(m=>m.LocalRig?.Lights.Length>0).ToArray();
        foreach(var mesh in lit)
            foreach(var light in mesh.LocalRig!.Lights)
                Console.WriteLine($"light {item.DisplayName}: {mesh.Path} {light.Name} colour {light.Colour} at {light.Position} radius {light.Radius}");
        if(lit.Length==0)throw new InvalidOperationException($"{item.DisplayName}: no skeleton light reached the wardrobe scene.");
        Console.WriteLine($"PASS {item.DisplayName}: {lit.Sum(m=>m.LocalRig!.Lights.Length)} authored lights.");
        CaptureReferenceWardrobe(app,Path.Combine(directory,"lights-"+slot+".png"),gameAngle:false,muted:true);
    }

    private static void CheckGpuWardrobe(AppServices app,string directory)
    {
        Directory.CreateDirectory(directory);
        var item=app.Game.Catalog.Single(item=>item.PrimaryResources.Any(path=>
            path.EndsWith("/chiyou_gold/chiyou_gold_cfx.epk",StringComparison.OrdinalIgnoreCase)));
        app.Profiles.Active!.GetOrAddEntry(ProfileEntryKind.EquipmentSlot,"character_effect").CosmeticId=item.Id;
        var scene=new ModelPreviewLoader(app).Load(null,"Marauder",true,true,default,showWeapons:true);
        var emitters=scene.Meshes.SelectMany(mesh=>mesh.Emitters).Where(emitter=>emitter.GpuEmitterSeed.HasValue).ToArray();
        if(!emitters.Any(emitter=>emitter.FaceGround&&emitter.Definition?.InitialBurst>0)||
            !emitters.Any(emitter=>emitter.ParticleMesh!=null&&emitter.Definition?.PersistentParticles==true)||
            !emitters.Any(emitter=>emitter.GameShader!=null))
            throw new InvalidOperationException($"{item.DisplayName} lost a grounded GPU layer, a persistent mesh or a render material.");
        Console.WriteLine($"PASS {item.DisplayName}: {emitters.Length} GPU render layers, {emitters.Count(e=>e.ParticleMesh!=null)} meshes, {emitters.Count(e=>e.GameShader!=null)} material shaders.");
        foreach(var emitter in emitters)
        {
            var radius=emitter.ParticleMesh?.Vertices.Max(vertex=>vertex.Position.Length());
            Console.WriteLine($"{emitter.Definition?.Material}: size={emitter.Size} rate={emitter.Rate} blend={emitter.GameShader?.BlendState}, face={emitter.Definition?.FaceType}, radius={radius}");
        }
        CaptureReferenceWardrobe(app,Path.Combine(directory,"gpu-character-effect.png"),gameAngle:true,muted:true, audit:scene);
    }

    private static void CaptureHead(AppServices app,PreviewScene scene,string path)
    {
        if(Application.Current==null)
        {
            var wpf=new PoeMtxChanger.App.App { ShutdownMode=ShutdownMode.OnExplicitShutdown };
            wpf.InitializeComponent();
        }
        var window=new MainWindow
        {
            DataContext=new MainViewModel(app){SelectedTab=MainTab.Wardrobe},
            ShowActivated=false,ShowInTaskbar=false,Left=-10000,Top=-10000,
            WindowStartupLocation=WindowStartupLocation.Manual
        };
        window.Show();
        try
        {
            var view=(WardrobeView)window.FindName("WardrobePage");
            var image=Find<Image>(view).Single(i=>i.Name=="Direct3DFrame");
            for(int attempt=0;attempt<120&&image.Source==null;attempt++)Wait(Task.Delay(250));
            if(image.Source==null)throw new InvalidOperationException("No Direct3D frame in the wardrobe.");
            Find<CheckBox>(view).Single(check=>check.Content as string=="Animation").IsChecked=false;
            var skeleton=scene.Skeleton!;
            var hood=scene.Meshes.Single(mesh=>mesh.Path.Contains("HallowedCryptPlateHelmetAlt",StringComparison.OrdinalIgnoreCase));
            var points=hood.Pose(skeleton,skeleton.Skinning(skeleton.Clips.Single(c=>c.Name==scene.PreferredIdle),0),0);
            var focus=new Point3D(
                (points.Min(p=>p.X)+points.Max(p=>p.X))*.5,
                -(points.Min(p=>p.Z)+points.Max(p=>p.Z))*.5-4,
                (points.Min(p=>p.Y)+points.Max(p=>p.Y))*.5);
            var camera=(PerspectiveCamera)Find<Viewport3D>(view).Single().Camera;
            var shift=new Vector3D(-28,18,-106);
            camera.Position=focus+shift;
            camera.LookDirection=-shift;
            var lighting=Find<ComboBox>(view).Single(combo=>combo.ToolTip as string=="Wardrobe lighting");
            lighting.SelectedIndex=1;
            lighting.SelectedIndex=0;
            var neutral=SaveHead(path);
            lighting.SelectedIndex=2;
            var muted=SaveHead(Path.ChangeExtension(path,"muted.png"));
            if(muted>=neutral*.90)throw new InvalidOperationException("Muted lighting must reduce the mask highlights.");
            Console.WriteLine($"Hood lighting: {neutral:F1} → {muted:F1} mean brightness in the mask region.");

            double SaveHead(string destination)
            {
                var bitmap=(BitmapSource)image.Source;
                var encoder=new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file=File.Create(destination);
                encoder.Save(file);
                int x=bitmap.PixelWidth*4/10,y=bitmap.PixelHeight*3/10;
                var region=new Int32Rect(x,y,bitmap.PixelWidth/5,bitmap.PixelHeight*4/10);
                var pixels=new byte[region.Width*region.Height*4];
                bitmap.CopyPixels(region,pixels,region.Width*4,0);
                double total=0;
                for(int offset=0;offset<pixels.Length;offset+=4)
                    total+=(pixels[offset]+pixels[offset+1]+pixels[offset+2])/3.0;
                return total/(region.Width*region.Height);
            }
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Side and back views of the wardrobe at 3 s of idle and of running (animation driven frame by frame at 60 Hz so
    /// the bone physics settles): cloth-&lt;view&gt;-&lt;idle|run&gt;.png. Compare with PMC_NO_BONE_PHYSICS=1.
    /// </summary>
    private static void CaptureCloth(AppServices app, string directory)
    {
        if (Application.Current == null)
        {
            var wpf = new PoeMtxChanger.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            wpf.InitializeComponent();
        }
        var window = new MainWindow
        {
            DataContext = new MainViewModel(app) { SelectedTab = MainTab.Wardrobe },
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, Width = 1600, Height = 1000,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        try
        {
            var view = (WardrobeView)window.FindName("WardrobePage");
            WriteableBitmap? Frame() => Find<Image>(view).SingleOrDefault(i => i.Name == "Direct3DFrame")?.Source as WriteableBitmap;
            for (int attempt = 0; attempt < 120 && Frame() == null; attempt++) Wait(Task.Delay(250));
            if (Frame() == null) throw new InvalidOperationException("No Direct3D frame in the wardrobe.");
            Find<CheckBox>(view).Single(check => check.Content as string == "Animation").IsChecked = false;
            var panel = Find<UserControl>(view).Single(control => control.GetType().Name == "ModelPreviewPanel");
            const System.Reflection.BindingFlags nonPublic = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            // The wardrobe may reload once more after its profile arrives; wait until the latest load has finished.
            var ready = panel.GetType().GetProperty("Ready")!;
            var sceneField = panel.GetType().GetField("_scene", nonPublic)!;
            for (int attempt = 0; attempt < 120; attempt++)
            {
                Wait((Task)ready.GetValue(panel)!);
                Wait(Task.Delay(300));
                if (((Task)ready.GetValue(panel)!).IsCompleted && sceneField.GetValue(panel) != null && Frame() != null) break;
            }
            var draw = panel.GetType().GetMethod("DrawFrame")!;
            var yaw = panel.GetType().GetField("_yaw", nonPublic)!;
            var pitch = panel.GetType().GetField("_pitch", nonPublic)!;
            var updateCamera = panel.GetType().GetMethod("UpdateCamera", nonPublic)!;
            var motion = Find<Button>(view).Single(b => b.ToolTip as string == "Check the cape and trails in motion");
            Directory.CreateDirectory(directory);
            foreach (string state in new[] { "idle", "run" })
            {
                if (state == "run") motion.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                foreach (var (label, angle) in new[] { ("side", Math.PI / 2), ("back", 0.0) })
                {
                    yaw.SetValue(panel, angle); pitch.SetValue(panel, .1); updateCamera.Invoke(panel, null);
                    for (int step = 0; step <= 180; step++) draw.Invoke(panel, [step / 60f]);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(Frame()!));
                    using var file = File.Create(Path.Combine(directory, $"cloth-{label}-{state}.png"));
                    encoder.Save(file);
                }
            }
            // A front view of the whole window (idle, physics on) for documentation.
            motion.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            yaw.SetValue(panel, Math.PI + .5); pitch.SetValue(panel, .12); updateCamera.Invoke(panel, null);
            for (int step = 0; step <= 180; step++) draw.Invoke(panel, [step / 60f]);
            var front = (FrameworkElement)window.Content;
            var frontShot = new RenderTargetBitmap((int)front.ActualWidth, (int)front.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            frontShot.Render(front);
            var frontEncoder = new PngBitmapEncoder();
            frontEncoder.Frames.Add(BitmapFrame.Create(frontShot));
            using (var file = File.Create(Path.Combine(directory, "wardrobe-window.png"))) frontEncoder.Save(file);
            // The wardrobe's own switch: off must change the frame back to the animated pose.
            var toggle = Find<CheckBox>(view).Single(check => check.Content as string == "Cloth physics");
            if (toggle.IsChecked != app.Settings.ClothPhysics) throw new InvalidOperationException("The cloth physics switch does not show the setting.");
            byte[] Pixels() { var f = Frame()!; var px = new byte[f.PixelWidth * f.PixelHeight * 4]; f.CopyPixels(px, f.PixelWidth * 4, 0); return px; }
            var before = Pixels();
            toggle.IsChecked = !toggle.IsChecked;
            toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            for (int step = 0; step <= 30; step++) draw.Invoke(panel, [3 + step / 60f]);
            if (before.SequenceEqual(Pixels())) throw new InvalidOperationException("Switching cloth physics did not change the frame.");
            if (sceneField.GetValue(panel) == null || !((Task)ready.GetValue(panel)!).IsCompleted)
                throw new InvalidOperationException("Switching cloth physics reloaded the scene.");
            if (app.Settings.ClothPhysics != (toggle.IsChecked == true)) throw new InvalidOperationException("The cloth physics switch was not saved.");
            var content = (FrameworkElement)window.Content;
            var shot = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            shot.Render(content);
            var windowShot = new PngBitmapEncoder();
            windowShot.Frames.Add(BitmapFrame.Create(shot));
            using (var file = File.Create(Path.Combine(directory, "wardrobe-toggle.png"))) windowShot.Save(file);
            toggle.IsChecked = !toggle.IsChecked;
            toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Console.WriteLine("PASS cloth physics switch changes the frame and is saved.");
            Console.WriteLine("Cloth shots saved to " + directory);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Times the wardrobe's own frame loop on the current set in a 2560×1440 window: total DrawFrame time, the
    /// native draw with readback, and the copy into the WPF bitmap. Animation is driven directly, not by the timer.
    /// </summary>
    private static void MeasureWardrobeFrames(AppServices app)
    {
        if (Application.Current == null)
        {
            var wpf = new PoeMtxChanger.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            wpf.InitializeComponent();
        }
        var window = new MainWindow
        {
            DataContext = new MainViewModel(app) { SelectedTab = MainTab.Wardrobe },
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, Width = 2560, Height = 1440,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        try
        {
            var view = (WardrobeView)window.FindName("WardrobePage");
            WriteableBitmap? Frame() => Find<Image>(view).SingleOrDefault(i => i.Name == "Direct3DFrame")?.Source as WriteableBitmap;
            for (int attempt = 0; attempt < 120 && Frame() == null; attempt++) Wait(Task.Delay(250));
            if (Frame() == null) throw new InvalidOperationException("No Direct3D frame in the wardrobe.");
            Find<CheckBox>(view).Single(check => check.Content as string == "Animation").IsChecked = false;
            var panel = Find<UserControl>(view).Single(control => control.GetType().Name == "ModelPreviewPanel");
            const System.Reflection.BindingFlags nonPublic = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var renderer = panel.GetType().GetField("_direct", nonPublic)!.GetValue(panel)!;
            var native = renderer.GetType().GetProperty("LastNativeMs", nonPublic)!;
            var copy = renderer.GetType().GetProperty("LastCopyMs", nonPublic)!;
            var draw = panel.GetType().GetMethod("DrawFrame")!;
            for (int i = 0; i < 30; i++) draw.Invoke(panel, [i / 60f]);
            var total = new List<double>(); var gpu = new List<double>(); var copies = new List<double>();
            for (int i = 0; i < 240; i++)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                draw.Invoke(panel, [.5f + i / 60f]);
                total.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                gpu.Add((double)native.GetValue(renderer)!); copies.Add((double)copy.GetValue(renderer)!);
            }
            string Stat(List<double> v) { var s = v.Order().ToArray(); return $"avg {v.Average():F2} ms, p50 {s[s.Length / 2]:F2}, p95 {s[(int)(s.Length * .95)]:F2}"; }
            var bitmap = Frame()!;
            Console.WriteLine($"Wardrobe frame {bitmap.PixelWidth}x{bitmap.PixelHeight}: DrawFrame {Stat(total)}; native draw+readback {Stat(gpu)}; WPF copy {Stat(copies)}");
            Console.WriteLine($"CPU scene update ≈ {total.Average() - gpu.Average() - copies.Average():F2} ms; max FPS by frame cost ≈ {1000 / total.Average():F0}");

            // Live loop: count the frames the panel actually draws with animation on.
            var lastFrame = panel.GetType().GetField("_lastFrame", nonPublic)!;
            int drawn = 0, composed = 0; float previous = -1;
            void Count(object? sender, EventArgs e) { composed++; float t = (float)lastFrame.GetValue(panel)!; if (t != previous) { drawn++; previous = t; } }
            Find<CheckBox>(view).Single(check => check.Content as string == "Animation").IsChecked = true;
            Wait(Task.Delay(500));
            CompositionTarget.Rendering += Count;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Wait(Task.Delay(3000));
            CompositionTarget.Rendering -= Count;
            Console.WriteLine($"Live wardrobe loop: {drawn / clock.Elapsed.TotalSeconds:F1} FPS drawn, {composed / clock.Elapsed.TotalSeconds:F1} WPF frames/s");
        }
        finally { window.Close(); }
    }

    private static int CaptureReferenceWardrobe(AppServices app, string path, bool gameAngle, bool muted, PreviewScene? audit=null,
        bool comparePost=false)
    {
        if (Application.Current == null)
        {
            var wpf = new PoeMtxChanger.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            wpf.InitializeComponent();
        }
        var window = new MainWindow
        {
            DataContext = new MainViewModel(app) { SelectedTab = MainTab.Wardrobe },
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        try
        {
            var view = (WardrobeView)window.FindName("WardrobePage");
            WriteableBitmap? Frame() => Find<Image>(view).SingleOrDefault(i => i.Name == "Direct3DFrame")?.Source as WriteableBitmap;
            for (int attempt = 0; attempt < 120 && Frame() == null; attempt++) Wait(Task.Delay(250));
            if (Frame() == null) throw new InvalidOperationException("No Direct3D frame in the wardrobe.");
            if (gameAngle) Find<Button>(view).Single(button => button.Content as string == "Top view")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(comparePost)
            {
                var lighting=Find<ComboBox>(view).Single(combo=>combo.ToolTip as string=="Wardrobe lighting");
                if(lighting.SelectedIndex!=0||!lighting.Items.Contains("PoE · post-processing"))
                    throw new InvalidOperationException("The reference mode must load without replacing neutral lighting as the default.");
                Find<CheckBox>(view).Single(check=>check.Content as string=="Animation").IsChecked=false;
                var panel=Find<UserControl>(view).Single(control=>control.GetType().Name=="ModelPreviewPanel");
                var draw=panel.GetType().GetMethod("DrawFrame")!;

                byte[] Capture(string output)
                {
                    // Repeat the trail warm-up so both captures start from the same render state.
                    for(int step=0;step<=60;step++)draw.Invoke(panel,[step/30f]);
                    var frame=Frame()!;
                    var pixels=new byte[frame.PixelWidth*frame.PixelHeight*4];
                    frame.CopyPixels(pixels,frame.PixelWidth*4,0);
                    var encoder=new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(frame));
                    using var file=File.Create(output);
                    encoder.Save(file);
                    return pixels;
                }
                var studio=Capture(Path.ChangeExtension(path,"neutral.png"));
                lighting.SelectedItem="PoE · post-processing";
                var reference=Capture(Path.ChangeExtension(path,"poe-post.png"));
                if(studio.SequenceEqual(reference))
                    throw new InvalidOperationException("Selecting the reference post-process did not change the Direct3D frame.");
                Console.WriteLine("PASS paired wardrobe captures at the same pose, camera and animation time.");
                return 0;
            }
            if (muted) Find<ComboBox>(view).Single(combo => combo.ToolTip as string == "Wardrobe lighting").SelectedIndex = 2;
            if(audit!=null)
            {
                var panel=Find<UserControl>(view).Single(control=>control.GetType().Name=="ModelPreviewPanel");
                const System.Reflection.BindingFlags nonPublic=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                var renderer=panel.GetType().GetField("_direct",nonPublic)!.GetValue(panel)!;
                var load=renderer.GetType().GetMethod("SetScene",nonPublic)!;
                var draw=panel.GetType().GetMethod("DrawFrame")!;
                var sceneField=panel.GetType().GetField("_scene",nonPublic)!;
                Find<CheckBox>(view).Single(check=>check.Content as string=="Animation").IsChecked=false;
                foreach(string material in audit.Meshes.SelectMany(m=>m.Emitters).Select(e=>e.Definition?.Material).OfType<string>().Distinct())
                {
                    var sample=audit with{Meshes=audit.Meshes.Select(m=>m with{Emitters=m.Emitters.Where(e=>e.Definition?.Material==material).ToArray()}).ToArray()};
                    sceneField.SetValue(panel,sample);
                    load.Invoke(renderer,[sample]);
                    draw.Invoke(panel,[0.8f]);
                    var pixels=new byte[Frame()!.PixelWidth*Frame()!.PixelHeight*4];
                    Frame()!.CopyPixels(pixels,Frame()!.PixelWidth*4,0);
                    int pale=0;
                    for(int i=0;i<pixels.Length;i+=4) if(pixels[i]>205&&pixels[i+1]>205&&pixels[i+2]>205)pale++;
                    if(pale>100)Console.WriteLine($"PALE {pale}: {material}");
                }
                return 0;
            }
            BitmapSource? brightest = null;
            int mostWhite = 0, bestScore = -1;
            var content = (FrameworkElement)window.Content;
            var dpi = VisualTreeHelper.GetDpi(content);
            for (int i = 0; i < (gameAngle ? 22 : 12); i++)
            {
                Wait(Task.Delay(130));
                var frame = Frame() ?? throw new InvalidOperationException("No Direct3D frame in the wardrobe.");
                var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
                frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
                int white = 0, gold = 0;
                for (int offset = 0; offset < pixels.Length; offset += 4)
                {
                    if (pixels[offset] > 220 && pixels[offset + 1] > 220 && pixels[offset + 2] > 220) white++;
                    if (gameAngle&&pixels[offset+2]>180&&pixels[offset+1]>120&&pixels[offset+1]-pixels[offset]>38) gold++;
                }
                mostWhite = Math.Max(white, mostWhite);
                int score=gameAngle?gold:white;
                if (score <= bestScore) continue;
                bestScore = score;
                var shot = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                shot.Render(content);
                brightest = shot;
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(brightest!));
            using var file = File.Create(path);
            encoder.Save(file);
            Console.WriteLine($"{Path.GetFileName(path)}: {mostWhite} near-white pixels in the brightest sampled frame.");
            return mostWhite;
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Renders the wardrobe's own Direct3D frame (top view, animation frozen at <paramref name="seconds"/>) for the
    /// effects of the meshes whose path contains <paramref name="meshFilter"/>: once with all their layers, then one
    /// image per material. Other meshes keep their geometry but lose their effects.
    /// </summary>
    private static void CaptureLayers(AppServices app, PreviewScene scene, string directory, string meshFilter, float seconds, bool muted)
    {
        Directory.CreateDirectory(directory);
        if (Application.Current == null)
        {
            var wpf = new PoeMtxChanger.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            wpf.InitializeComponent();
        }
        var window = new MainWindow
        {
            DataContext = new MainViewModel(app) { SelectedTab = MainTab.Wardrobe },
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        try
        {
            var view = (WardrobeView)window.FindName("WardrobePage");
            WriteableBitmap? Frame() => Find<Image>(view).SingleOrDefault(i => i.Name == "Direct3DFrame")?.Source as WriteableBitmap;
            for (int attempt = 0; attempt < 120 && Frame() == null; attempt++) Wait(Task.Delay(250));
            Find<Button>(view).Single(button => button.Content as string == "Top view").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (muted) Find<ComboBox>(view).Single(combo => combo.ToolTip as string == "Wardrobe lighting").SelectedIndex = 2;
            Find<CheckBox>(view).Single(check => check.Content as string == "Animation").IsChecked = false;
            Wait(Task.Delay(300));
            var panel = Find<UserControl>(view).Single(control => control.GetType().Name == "ModelPreviewPanel");
            const System.Reflection.BindingFlags nonPublic = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var renderer = panel.GetType().GetField("_direct", nonPublic)!.GetValue(panel)!;
            var load = renderer.GetType().GetMethod("SetScene", nonPublic)!;
            var draw = panel.GetType().GetMethod("DrawFrame")!;
            var sceneField = panel.GetType().GetField("_scene", nonPublic)!;
            bool Selected(PreviewMesh mesh) => mesh.Path.Contains(meshFilter, StringComparison.OrdinalIgnoreCase);
            PreviewScene Only(Func<string?, bool> material) => scene with
            {
                Meshes = scene.Meshes.Select(m => Selected(m)
                    ? m with { Emitters = m.Emitters.Where(e => material(e.Definition?.Material)).ToArray(), Trails = m.Trails.Where(t => material(t.Definition?.Material)).ToArray() }
                    : m with { Emitters = [], Trails = [] }).ToArray(),
            };
            void Shot(PreviewScene sample, string name)
            {
                sceneField.SetValue(panel, sample);
                load.Invoke(renderer, [sample]);
                // Trails and the particle warm-up need a few earlier frames.
                for (float t = Math.Max(0, seconds - 1); t < seconds; t += 1 / 30f) draw.Invoke(panel, [t]);
                draw.Invoke(panel, [seconds]);
                var frame = Frame()!;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(frame));
                using var file = File.Create(Path.Combine(directory, name + ".png"));
                encoder.Save(file);
            }
            Shot(Only(_ => true), "all");
            int index = 0;
            foreach (var material in scene.Meshes.Where(Selected)
                         .SelectMany(m => m.Emitters.Select(e => e.Definition?.Material).Concat(m.Trails.Select(t => t.Definition?.Material)))
                         .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string name = $"{++index:00}-{Path.GetFileNameWithoutExtension(material)}";
                Shot(Only(m => string.Equals(m, material, StringComparison.OrdinalIgnoreCase)), name);
                Console.WriteLine($"layer {name}");
            }
        }
        finally { window.Close(); }
    }
}
