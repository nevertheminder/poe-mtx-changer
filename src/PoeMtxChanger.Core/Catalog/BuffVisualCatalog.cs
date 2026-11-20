using PoeMtxChanger.Core.Formats.Dat;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Catalog;

public sealed record BuffVisualDefinition(string Id,string Name,IReadOnlyDictionary<string,IReadOnlyList<string>> Roles,IReadOnlyList<string> Limitations)
{
    public IReadOnlyList<string> SkillGroups { get; init; } = [];
    public bool AllowsGlobalAuras { get; init; }
    public string? CosmeticName { get; init; }
    public string? IconPath { get; init; }
    public string PickerName => CosmeticName ?? DisplayName;
    public string DisplayName=>string.IsNullOrWhiteSpace(Name)||Id.Equals(Name,StringComparison.OrdinalIgnoreCase)?Id:Name+" · "+Id;
}

public sealed record BuffSkillDefinition(string Id,string Name,IReadOnlyList<string> VisualGroups,bool AllowsGlobalAuras)
{
    public string? IconPath { get; init; }
    public bool Accepts(BuffVisualDefinition visual) => visual.SkillGroups.Intersect(VisualGroups,StringComparer.OrdinalIgnoreCase).Any()
        || AllowsGlobalAuras && visual.SkillGroups.Contains("global_auras",StringComparer.OrdinalIgnoreCase);
}

/// <summary>Explicit visual-to-visual selection for auras/heralds/buffs. No unnamed variation numbers or gameplay tables are used.</summary>
public sealed class BuffVisualCatalogBuilder(Func<string,DatTable?> table)
{
    public IReadOnlyList<BuffVisualDefinition> Build()
    {
        var buffs=table("BuffVisuals");if(buffs==null)return [];
        var animations=table("MiscAnimated");var orbs=table("BuffVisualOrbs");var art=table("BuffVisualOrbArt");
        var variations=table("BuffVisualsArtVariations");var labels=new Dictionary<int,HashSet<string>>();
        var auraArt = ReadAuraArt(variations);
        if(variations is not null&&variations.HasColumn("Buff")&&variations.HasColumn("BuffVisual"))
            for(int v=0;v<variations.RowCount;v++)
                foreach(int index in variations.GetRowRefArray(v,"BuffVisual").OfType<int>())
                {if(!labels.TryGetValue(index,out var names))labels[index]=names=[];names.Add(variations.GetString(v,"Buff"));}
        var result=new List<BuffVisualDefinition>();
        string Animated(int? index)=>index is {} a&&animations!=null&&a<animations.RowCount?animations.GetString(a,"AOFile"):"";
        for(int r=0;r<buffs.RowCount;r++)
        {
            string id=buffs.GetString(r,"Id");if(string.IsNullOrWhiteSpace(id)||DataCatalogBuilder.IsInternal(id))continue;
            var roles=new Dictionary<string,IReadOnlyList<string>>();var limits=new List<string>();
            foreach(string c in new[]{"EPKFiles1","EPKFiles2","EPKFiles"})if(buffs.HasColumn(c))
            {var paths=buffs.GetStringArray(r,c);if(paths.Count>0)roles[c]=paths;}
            foreach(string c in new[]{"EPKFile","ExtraArt"})if(buffs.HasColumn(c))
            {string path=buffs.GetString(r,c);if(!string.IsNullOrWhiteSpace(path))roles[c]=[path];}
            foreach(string c in new[]{"MiscAnimated1","MiscAnimated2","MiscAnimated3"})if(buffs.HasColumn(c)&&buffs.GetRowRef(r,c) is {} index)
                roles[c]=[Animated(index)];
            if(buffs.HasColumn("WeaponEffect")&&buffs.GetRowRef(r,"WeaponEffect")!=null)limits.Add("This buff's weapon effect is not carried over.");
            if(buffs.HasColumn("BuffVisualOrbs"))
                foreach(var (orb,i) in buffs.GetRowRefArray(r,"BuffVisualOrbs").Select((index,i)=>(index,i)))
                {
                    if(orb==null||orbs==null||art==null||orb>=orbs.RowCount){limits.Add("The orb role was not read.");continue;}
                    foreach(string c in new[]{"BuffVisualOrbArtKeys","Player_BuffVisualOrbArtKeys","BuffVisualOrbArtKeys2"})
                    {
                        if(!orbs.HasColumn(c))continue;
                        var refs=orbs.GetRowRefArray(orb.Value,c);
                        if(refs.Count>0)roles[$"orb:{i}:{c}"]=refs.Select(index=>index is {} a&&a<art.RowCount?Animated(art.GetRowRef(a,"MiscAnimated")):"").ToList();
                    }
                    limits.Add("Orbs carry over the AO model; spawn settings, sounds and object count stay original.");
                }
            string name=buffs.HasColumn("BuffName")?buffs.GetString(r,"BuffName"):"";
            if(labels.TryGetValue(r,out var aliases))name=string.Join(", ",aliases.Prepend(name).Where(n=>!string.IsNullOrWhiteSpace(n)).Distinct());
            if (roles.Count == 0) continue;
            var appearance = auraArt.GetValueOrDefault(r);
            string? buffIcon = buffs.HasColumn("BuffDDSFile") ? buffs.GetString(r, "BuffDDSFile") : null;
            result.Add(new(id, name, roles, limits.Distinct().ToList())
            {
                SkillGroups = labels.TryGetValue(r, out var groupNames) ? groupNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray() : [],
                CosmeticName = appearance?.Name,
                IconPath = appearance?.Icon ?? buffIcon,
            });
        }
        var definitions = result;
        var auraGroups = BuildSkills(definitions).Where(s=>s.AllowsGlobalAuras).SelectMany(s=>s.VisualGroups).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return definitions.Select(b=>b with {AllowsGlobalAuras=b.SkillGroups.Any(auraGroups.Contains)})
            .OrderBy(b=>b.DisplayName,StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private Dictionary<int, MtxArt> ReadAuraArt(DatTable? variations)
    {
        var result = new Dictionary<int, MtxArt>();
        var skills = table("SkillArtVariations");
        if (variations == null || !variations.HasColumn("Variant") || !variations.HasColumn("Buff") || !variations.HasColumn("BuffVisual") ||
            skills == null || !skills.HasColumn("Id") || !skills.HasColumn("Stat")) return result;

        int auraRow = Enumerable.Range(0, skills.RowCount).FirstOrDefault(row => skills.GetString(row, "Id") == "global_auras", -1);
        if (auraRow < 0 || skills.GetRowRef(auraRow, "Stat") is not int auraStat) return result;

        // Aura Skin items use the global aura stat with the signed variant from BuffVisualsArtVariations.
        var index = MtxArtIndex.Build(table);
        var matches = new Dictionary<int, HashSet<MtxArt>>();
        for (int row = 0; row < variations.RowCount; row++)
        {
            int variant = variations.GetInt32(row, "Variant");
            if (variations.GetString(row, "Buff") != "global_auras" || variant >= 0 ||
                !index.TryGetValue((auraStat, variant), out var art)) continue;
            foreach (int visual in variations.GetRowRefArray(row, "BuffVisual").OfType<int>())
            {
                if (!matches.TryGetValue(visual, out var choices)) matches[visual] = choices = [];
                choices.UnionWith(art.Select(a => a.Art).Where(a => a.Type == "Aura Skin"));
            }
        }
        foreach (var (visual, choices) in matches)
            if (choices.Count == 1) result.Add(visual, choices.Single());
        return result;
    }

    public IReadOnlyList<BuffSkillDefinition> BuildSkills(IReadOnlyList<BuffVisualDefinition> visuals)
    {
        var skills=table("SkillArtVariations");var variations=table("BuffVisualsArtVariations");
        var active=table("ActiveSkills");var types=table("ActiveSkillType");
        if(skills==null||variations==null||active==null||!skills.HasColumn("BuffVisuals")||!skills.HasColumn("ActiveSkill"))return [];
        var gemItems=GemItemsByActiveSkill();
        var auraItems=CommonAuraItems();
        var available=visuals.SelectMany(v=>v.SkillGroups).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result=new List<BuffSkillDefinition>();
        for(int row=0;row<skills.RowCount;row++)
        {
            string id=skills.GetString(row,"Id");
            if(string.IsNullOrWhiteSpace(id)||id=="global_auras"||DataCatalogBuilder.IsInternal(id))continue;
            var groups=skills.GetRowRefArray(row,"BuffVisuals").OfType<int>().Where(r=>r<variations.RowCount)
                .Select(r=>variations.GetString(r,"Buff")).Where(available.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(groups.Length==0)continue;
            var gems=skills.GetRowRefArray(row,"ActiveSkill").OfType<int>().Where(r=>r<active.RowCount&&gemItems.ContainsKey(r)).ToArray();
            if(gems.Length==0)continue;
            bool aura=gems.All(g=>gemItems[g].Any(auraItems.Contains))&&types!=null&&active.HasColumn("ActiveSkillTypes")&&gems.All(g=>active.GetRowRefArray(g,"ActiveSkillTypes").OfType<int>()
                .Any(t=>t<types.RowCount&&types.GetString(t,"Id").Equals("Aura",StringComparison.OrdinalIgnoreCase)));
            string name=active.GetString(gems[0],"DisplayedName");
            result.Add(new(id,string.IsNullOrWhiteSpace(name)?id:name+" · "+id,groups,aura)
                {IconPath=active.HasColumn("Icon_DDSFile")?active.GetString(gems[0],"Icon_DDSFile"):null});
        }
        return result.OrderBy(s=>s.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private Dictionary<int,HashSet<int>> GemItemsByActiveSkill()
    {
        var gems=table("SkillGems");var variants=table("GemEffects");var granted=table("GrantedEffects");
        var result=new Dictionary<int,HashSet<int>>();
        if(gems==null||variants==null||granted==null||!gems.HasColumn("BaseItemTypesKey")||!gems.HasColumn("GemVariants")||!granted.HasColumn("ActiveSkill"))return result;
        for(int row=0;row<gems.RowCount;row++)
        {
            int? item=gems.GetRowRef(row,"BaseItemTypesKey");if(item==null)continue;
            foreach(int variant in gems.GetRowRefArray(row,"GemVariants").OfType<int>().Where(i=>i<variants.RowCount))
                foreach(string column in new[]{"GrantedEffect","GrantedEffect2"})
                {
                    if(!variants.HasColumn(column)||variants.GetRowRef(variant,column) is not {} effect||effect>=granted.RowCount
                        ||granted.GetRowRef(effect,"ActiveSkill") is not {} skill)continue;
                    if(!result.TryGetValue(skill,out var items))result[skill]=items=[];
                    items.Add(item.Value);
                }
        }
        return result;
    }

    private HashSet<int> CommonAuraItems()
    {
        var itemised=table("ItemisedVisualEffect");var gameTypes=table("MtxTypeGameSpecific");var types=table("MtxTypes");
        if(itemised==null||gameTypes==null||types==null||!itemised.HasColumn("Mtx")||!itemised.HasColumn("BaseItemTypes")||!gameTypes.HasColumn("Type")||!types.HasColumn("Type"))return [];
        HashSet<int>? common=null;
        for(int row=0;row<itemised.RowCount;row++)
        {
            if(itemised.GetRowRef(row,"Mtx") is not {} game||game>=gameTypes.RowCount
                ||gameTypes.GetRowRef(game,"Type") is not {} type||type>=types.RowCount||types.GetString(type,"Type")!="Aura Skin")continue;
            var items=itemised.GetRowRefArray(row,"BaseItemTypes").OfType<int>().ToHashSet();
            // Use the intersection: an unlinked global visual must never broaden a gem's explicit MTX whitelist.
            if(common==null)common=items;else common.IntersectWith(items);
        }
        return common??[];
    }

    public static bool Compatible(BuffVisualDefinition original,BuffVisualDefinition donor,BuffSkillDefinition? skill=null)
    {
        if(skill!=null)return skill.Accepts(original)&&skill.Accepts(donor);
        if(original.SkillGroups.Intersect(donor.SkillGroups,StringComparer.OrdinalIgnoreCase).Any(group=>!group.Equals("global_auras",StringComparison.OrdinalIgnoreCase)))return true;
        return original.AllowsGlobalAuras&&donor.SkillGroups.Contains("global_auras",StringComparer.OrdinalIgnoreCase);
    }

    public static SkillVisualVariant Plan(BuffVisualDefinition original,BuffVisualDefinition donor,IResourceSource source,BuffSkillDefinition? skill=null)
    {
        var errors=new List<string>();var notes=original.Limitations.Concat(donor.Limitations).Distinct().ToList();
        var mappings=new Dictionary<string,ReplacementMapping>(StringComparer.OrdinalIgnoreCase);
        if(!Compatible(original,donor,skill))return new(0,donor.DisplayName,[],notes,["According to the game tables, these effects do not belong to one compatible skill. Universal auras need a confirmed Aura type."]);
        foreach(string role in donor.Roles.Keys.Except(original.Roles.Keys))notes.Add("Partial replacement: the additional role "+role+" is not created.");
        foreach(var (role,targets) in original.Roles)
        {
            if(!donor.Roles.TryGetValue(role,out var sources)){notes.Add("Partial replacement: the role "+role+" stays original.");continue;}
            if(targets.Count!=sources.Count){errors.Add(role+": the resource count differs.");continue;}
            for(int i=0;i<targets.Count;i++)
            {
                string target=targets[i],alternative=sources[i];
                if(string.IsNullOrWhiteSpace(target)||string.IsNullOrWhiteSpace(alternative)||!source.Exists(target)||!source.Exists(alternative))
                {errors.Add(role+": resource not found.");continue;}
                if(target.Equals(alternative,StringComparison.OrdinalIgnoreCase))continue;
                var mapping=new ReplacementMapping{TargetPath=target,SourcePath=alternative,SourceCosmeticId="buff-visual:"+donor.Id,Note="buff:"+role+":"+i}.Normalized();
                var check=new MappingValidator(ResourcePolicy.Default).Validate([mapping],source,false);
                if(check.HasErrors){errors.AddRange(check.Issues.Where(issue=>issue.Severity==IssueSeverity.Error).Select(issue=>issue.Message));continue;}
                if(mappings.TryGetValue(mapping.TargetPath,out var previous)&&previous.SourcePath!=mapping.SourcePath)errors.Add("A shared resource needs different replacements: "+mapping.TargetPath);
                else mappings[mapping.TargetPath]=mapping;
            }
        }
        notes.Add("The replacement affects every client object that uses these files. The buff or aura must already be present in game.");
        return new(0,donor.DisplayName,errors.Count==0?mappings.Values.ToList():[],notes,errors.Distinct().ToList());
    }
}
