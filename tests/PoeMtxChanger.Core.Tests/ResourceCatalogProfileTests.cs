using PoeMtxChanger.Core.Catalog;
using PoeMtxChanger.Core.Mapping;
using PoeMtxChanger.Core.Profiles;
using PoeMtxChanger.Core.Resources;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.Core.Tests;

public class ResourceTests
{
    [Fact]
    public void Extracts_quoted_and_bare_references_from_utf16_text()
    {
        var data = SyntheticGame.Utf16("version 2\r\nskeleton = \"Art/Models/a b/c.ast\"\r\nfx Metadata/Effects/x.epk\r\nnot a path: Foo/bar.dds\r\n");
        var refs = ReferenceExtractor.Extract(data);
        Assert.Contains("Art/Models/a b/c.ast", refs);
        Assert.Contains("Metadata/Effects/x.epk", refs);
        Assert.DoesNotContain(refs, r => r.StartsWith("Foo/"));
    }

    [Fact]
    public void Binary_data_yields_no_references()
        => Assert.Empty(ReferenceExtractor.Extract(Enumerable.Range(0, 512).Select(i => (byte)(i % 7)).ToArray()));

    [Fact]
    public void Dependency_analysis_reports_missing_resources()
    {
        var src = new InMemoryResourceSource();
        foreach (var (k, v) in SyntheticGame.SampleFiles()) src.Add(k, v);
        var ok = new DependencyAnalyzer(src).Analyze("Metadata/Effects/Microtransactions/portals/emberportal/emberportal.aoc");
        Assert.True(ok.IsComplete);
        Assert.Equal(5, ok.Nodes.Count); // aoc → ast, epk → pet → dds
        var bad = new DependencyAnalyzer(src).Analyze("Metadata/Pet/Wisp/wisp.aoc");
        Assert.Equal(["Art/Models/Pets/wisp/missing.ast"], bad.Missing);
    }

    [Theory]
    [InlineData("a/b.aoc", ResourceKind.AnimatedObjectClient, ResourceDomain.Visual)]
    [InlineData("Data/Mods.dat64", ResourceKind.DataTable, ResourceDomain.Gameplay)]
    [InlineData("x/y.OT", ResourceKind.ObjectType, ResourceDomain.Gameplay)]
    [InlineData("Audio/a.bank", ResourceKind.SoundBank, ResourceDomain.Audio)]
    [InlineData("q/weird.xyz", ResourceKind.Unknown, ResourceDomain.Other)]
    public void Classifies_by_extension(string path, ResourceKind kind, ResourceDomain domain)
    {
        var info = ResourceClassifier.Classify(path);
        Assert.Equal(kind, info.Kind);
        Assert.Equal(domain, info.Domain);
    }

    [Fact]
    public void Policy_blocks_gameplay_and_readability_paths()
    {
        var p = ResourcePolicy.Default;
        Assert.NotNull(p.WhyTargetDenied("Data/Mods.dat64"));
        Assert.NotNull(p.WhyTargetDenied("Metadata/Characters/Character.ot"));
        Assert.NotNull(p.WhyTargetDenied("Metadata/Monsters/Boss/boss.aoc"));
        Assert.NotNull(p.WhyTargetDenied("Metadata/Terrain/fog.epk"));
        Assert.NotNull(p.WhyTargetDenied("Audio/Sound.bank"));
        Assert.Null(p.WhyTargetDenied("Metadata/Effects/Microtransactions/portals/x.aoc"));
    }

    [Fact]
    public void Validator_rejects_empty_sources_and_duplicate_targets()
    {
        var src = new InMemoryResourceSource().Add("Art/a.epk", "x").Add("Art/b.epk", []).Add("Art/c.epk", "y");
        var report = new MappingValidator(ResourcePolicy.Default).Validate(
        [
            new ReplacementMapping { TargetPath = "Art/a.epk", SourcePath = "Art/b.epk" },
            new ReplacementMapping { TargetPath = "Art/a.epk", SourcePath = "Art/c.epk" },
        ], src);
        Assert.Contains(report.Issues, i => i.Code == "EMPTY_SOURCE");
        Assert.Contains(report.Issues, i => i.Code == "DUP_TARGET");
        Assert.Equal(CompatibilityStatus.Unsupported, report.Status);
    }

    [Fact]
    public void Validator_warns_about_dependencies_missing_from_the_game_itself()
    {
        var src = new InMemoryResourceSource();
        foreach (var (k, v) in SyntheticGame.SampleFiles()) src.Add(k, v);
        src.Add("Metadata/Effects/Microtransactions/pets/target.aoc", "version 2");
        var report = new MappingValidator(ResourcePolicy.Default).Validate(
            [new ReplacementMapping { TargetPath = "Metadata/Effects/Microtransactions/pets/target.aoc", SourcePath = "Metadata/Pet/Wisp/wisp.aoc" }], src);
        Assert.False(report.HasErrors);
        Assert.Equal(CompatibilityStatus.Experimental, report.Status);
        Assert.Contains(report.Issues, i => i.Code == "DEP_MISSING" && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void Validator_still_blocks_dependencies_it_could_not_inspect()
    {
        var src = new InMemoryResourceSource();
        src.Add("Metadata/Effects/Microtransactions/pets/target.aoc", "version 2");
        // Deep chain beyond the analyzer depth: completeness cannot be confirmed → error, not a warning.
        for (int i = 0; i < 12; i++) src.Add($"Metadata/Effects/chain/n{i}.epk", $"\"Metadata/Effects/chain/n{i + 1}.epk\"");
        src.Add("Metadata/Effects/chain/n12.epk", "1");
        src.Add("Metadata/Effects/Microtransactions/pets/source.aoc", "\"Metadata/Effects/chain/n0.epk\"");
        var report = new MappingValidator(ResourcePolicy.Default).Validate(
            [new ReplacementMapping { TargetPath = "Metadata/Effects/Microtransactions/pets/target.aoc", SourcePath = "Metadata/Effects/Microtransactions/pets/source.aoc" }], src);
        Assert.True(report.HasErrors);
        Assert.Contains(report.Issues, i => i.Code == "DEP_TRUNCATED" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Depth_limit_does_not_report_a_partial_graph_as_complete()
    {
        var source = new InMemoryResourceSource().Add("Art/a.epk", "\"Art/b.epk\"").Add("Art/b.epk", "\"Art/c.epk\"").Add("Art/c.epk", "end");
        var report = new DependencyAnalyzer(source, maxDepth: 1).Analyze("Art/a.epk");
        Assert.True(report.Truncated);
        Assert.False(report.IsComplete);
    }

    private sealed class UnreadableDependency : IResourceSource
    {
        public bool Exists(string path) => true;
        public byte[] Read(string path) => path.EndsWith("source.epk") ? SyntheticGame.Utf16("\"Art/broken.epk\"") : throw new IOException("corrupt chunk");
        public IEnumerable<string> EnumeratePaths() => [];
    }

    [Fact]
    public void An_unreadable_dependency_is_an_error_instead_of_an_empty_successful_leaf()
    {
        var source = new UnreadableDependency();
        var dependency = new DependencyAnalyzer(source).Analyze("Art/source.epk");
        Assert.False(dependency.IsComplete);
        Assert.Single(dependency.ReadErrors);
        var report = new MappingValidator(ResourcePolicy.Default).Validate(
            [new ReplacementMapping { TargetPath = "Art/target.epk", SourcePath = "Art/source.epk" }], source);
        Assert.Contains(report.Issues, i => i.Code == "DEP_READ" && i.Severity == IssueSeverity.Error);
    }
}

public class CatalogTests
{
    [Fact]
    public void Builds_grouped_categorized_items_from_paths()
    {
        var src = new InMemoryResourceSource();
        foreach (var (k, v) in SyntheticGame.SampleFiles()) src.Add(k, v);
        src.Add("Art/Microtransactions/wings/CarrionWings/wings.sm", "x");
        src.Add("Art/Microtransactions/wings/CarrionWings/wings.dds", [1]);
        var items = new CatalogBuilder(CatalogRules.LoadDefault()).Build(src);

        var wings = Assert.Single(items, i => i.RootPath == "Art/Microtransactions/wings/CarrionWings");
        Assert.Equal(CosmeticCategory.WingsAndBack, wings.Category);
        Assert.Equal("Carrion Wings", wings.DisplayName);
        Assert.Equal(["Art/Microtransactions/wings/CarrionWings/wings.sm"], wings.PrimaryResources);
        Assert.False(wings.IsMock);

        var portal = Assert.Single(items, i => i.RootPath == "Metadata/Effects/Microtransactions/portals/emberportal");
        Assert.Equal(CosmeticCategory.Portal, portal.Category);
        Assert.Equal(CompatibilityStatus.Experimental, portal.Status);
        Assert.Equal("Metadata/Effects/Microtransactions/portals/emberportal/emberportal.aoc", portal.PrimaryResources[0]);
        Assert.True(portal.HasVisualEffects);

        Assert.DoesNotContain(items, i => i.RootPath.StartsWith("Data/"));
        Assert.Single(items, i => i.Category == CosmeticCategory.Pet);
    }

    [Fact]
    public void Mock_catalog_is_always_flagged()
    {
        var mock = MockCatalog.Create();
        Assert.NotEmpty(mock);
        Assert.All(mock, i => { Assert.True(i.IsMock); Assert.StartsWith("[MOCK]", i.DisplayName); Assert.StartsWith("mock/", i.Id); });
    }
}

public class ProfileTests
{
    [Fact]
    public void Save_load_export_import_roundtrip()
    {
        using var tmp = new TempDir();
        var pm = new ProfileManager(tmp.Combine("profiles"));
        var p = new CosmeticProfile { Name = "Ember set" };
        p.GetOrAddEntry(ProfileEntryKind.EquipmentSlot, "portal").Mappings.Add(new ReplacementMapping { TargetPath = "\\Art\\a.aoc", SourcePath = "Art/b.aoc" });
        pm.Save(p);

        var loaded = Assert.Single(pm.LoadAll());
        Assert.Equal("Ember set", loaded.Name);
        Assert.Equal("Art/a.aoc", loaded.Entries[0].Mappings[0].TargetPath);

        var exported = tmp.Combine("export.json");
        pm.Export(loaded, exported);
        var imported = pm.Import(exported);
        Assert.NotEqual(loaded.Id, imported.Id);
        Assert.Equal(2, pm.LoadAll().Count);
        Assert.Single(imported.ActiveMappings());
    }

    [Theory]
    [InlineData("../../Windows/system32/x.aoc")]
    [InlineData("C:/Windows/x.aoc")]
    [InlineData("")]
    public void Import_rejects_unsafe_paths(string bad)
    {
        var json = $$"""{"schemaVersion":1,"name":"x","entries":[{"kind":"Manual","key":"k","mappings":[{"targetPath":"{{bad}}","sourcePath":"Art/a.aoc"}]}]}""";
        Assert.Throws<ProfileImportException>(() => ProfileManager.Parse(json));
    }

    [Fact]
    public void Import_rejects_future_schema_versions_and_garbage()
    {
        Assert.Throws<ProfileImportException>(() => ProfileManager.Parse("""{"schemaVersion":99,"name":"x"}"""));
        Assert.Throws<ProfileImportException>(() => ProfileManager.Parse("not json"));
    }
}
