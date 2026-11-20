using PoeMtxChanger.Core.Formats.Shaders;
using PoeMtxChanger.Core.Rendering;

namespace PoeMtxChanger.Core.Tests;

/// <summary>Parser and composer of the game's shader fragment library, on synthetic sources only.</summary>
public sealed class ShaderLibraryTests
{
    private const string Source = """
        DECLARATIONS user_decl
        	include helpers
        {{
        	float3 UseHelper(float3 v) { return Helper(v) * 2.0f; } // mentions Other() in a comment
        }}

        DECLARATIONS helpers
        {{
        	float3 Helper(float3 v)
        	{
        		return saturate(v);
        	}
        }}

        DECLARATIONS late_user
        {{
        	float3 Twice(float3 v) { return UseHelper(v); }
        }}

        DECLARATIONS other_decl
        {{
        	float Other() { return 1.0f; }
        	float CallsBack() { return Twice(float3(1, 1, 1)).x; }
        }}

        DECLARATIONS CpuTrails::EmitColor
        	uniform pipeline int cpu_trail_emit_color_r
        {{
        }}

        FRAGMENT Sample
        	cost: low
        	in float2 uv
        	pixel in float const_val
        	uniform texture tex
        	uniform float2 scale "x y" "-1 -1" "1 1" "0.5 0.25" : custom_range
        	uniform pass float time
        	dynamic float4 CharacterFXStatValues
        	custom dynamic int input
        #ifdef VERTEX_HAS_COLOR
        	in float4 color
        #endif
        	out float4 rgba
        {{
        	rgba = float4(uv * scale, 0, 1);
        }}
        """;

    private static FfxLibrary Library() => FfxLibrary.Parse([("test.ffx", Source)]);

    [Fact]
    public void Parses_blocks_including_namespaced_names()
    {
        var lib = Library();
        Assert.Contains("CpuTrails::EmitColor", lib.Declarations.Keys);
        Assert.Equal("cpu_trail_emit_color_r", Assert.Single(lib.Declarations["CpuTrails::EmitColor"].Uniforms).Name);
        // The namespaced block must not leak its ports into the block before it.
        Assert.Empty(lib.Declarations["other_decl"].Ports);
        Assert.Equal(["helpers"], lib.Declarations["user_decl"].Includes);
    }

    [Fact]
    public void Parses_fragment_ports_with_scopes_semantics_and_conditions()
    {
        var frag = Library().Fragments["Sample"];
        var ports = frag.Ports.ToDictionary(p => p.Name);

        Assert.Equal(FfxPortKind.In, ports["const_val"].Kind); // "pixel in" prefix
        Assert.Equal("custom_range", ports["scale"].Semantic);
        Assert.Equal("0.5 0.25", ports["scale"].DefaultValue);
        Assert.False(ports["scale"].IsEngineUniform);
        Assert.True(ports["time"].IsEngineUniform);
        Assert.Equal("dynamic", ports["CharacterFXStatValues"].Scope);
        Assert.True(ports["CharacterFXStatValues"].IsEngineUniform);
        // "custom dynamic int input" is a per-node value, not an engine global.
        Assert.Equal(FfxPortKind.Uniform, ports["input"].Kind);
        Assert.Equal("int", ports["input"].Type);
        Assert.False(ports["input"].IsEngineUniform);
        Assert.Equal("VERTEX_HAS_COLOR", ports["color"].Condition);
        Assert.Equal("low", frag.Attr("cost"));
        Assert.Contains("rgba = float4", frag.Body);
    }

    [Fact]
    public void Orders_declarations_by_actual_use_and_ignores_statements_and_comments()
    {
        var lib = Library();
        var builder = new MaterialShaderBuilder(lib, _ => null);
        string[] input = ["late_user", "other_decl", "user_decl", "helpers"];
        var ordered = builder.OrderByUse(input.Select(n => lib.Declarations[n]).ToList()).Select(b => b.Name).ToList();

        Assert.True(ordered.IndexOf("helpers") < ordered.IndexOf("user_decl"));
        Assert.True(ordered.IndexOf("user_decl") < ordered.IndexOf("late_user"));
        // other_decl calls Twice, so it follows late_user; the comment naming Other() adds no edge back.
        Assert.True(ordered.IndexOf("late_user") < ordered.IndexOf("other_decl"));
        Assert.Equal(4, ordered.Count);
    }

    [Theory]
    [InlineData("float", "[0.15, 1.0]", "(float)(0.15)")]
    [InlineData("float2", "[1, 2, 3]", "float2(1.0, 2.0)")]
    [InlineData("float3", "[1, 2, 3]", "float3(1.0, 2.0, 3.0)")]
    [InlineData("bool", "[0]", "false")]
    public void Literal_takes_as_many_components_as_the_type_has(string type, string json, string expected)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(expected, MaterialShaderBuilder.Literal(type, doc.RootElement, null));
    }

    [Fact]
    public void Literal_falls_back_to_the_port_default()
    {
        Assert.Equal("float2(0.5, 0.25)", MaterialShaderBuilder.Literal("float2", null, "0.5 0.25"));
        Assert.Equal("(int)0", MaterialShaderBuilder.Literal("int", null, null));
    }

    [Fact]
    public void Graph_reads_string_indices_and_skips_editor_groups()
    {
        var g = FxGraph.Parse("t.fxgraph", """
            {"overriden_blend_mode":"Additive","shader_group":["Material"],
             "nodes":[{"type":"<Group>","index":0},{"type":"Sample","index":"3","parameters":[{"path":"a.dds"}]}],
             "links":[{"src":{"type":"Sample","index":"3","variable":"rgba"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing","variable":"color","swizzle":"rgb"}}]}
            """);
        Assert.Equal("Additive", g.BlendMode);
        var node = Assert.Single(g.Nodes);
        Assert.Equal(3, node.Id.Index);
        var link = Assert.Single(g.Links);
        Assert.Equal("Texturing", link.Target.Node.Stage);
        Assert.Equal("rgb", link.Target.Swizzle);
    }

    [Fact]
    public void Emitter_blend_overrides_opaque_default_and_graph_instance_can_force_cutout()
    {
        var force = FxGraph.Parse("cutout.fxgraph", """{"overriden_blend_mode":"AlphaTest","alphatest_threshold":128}""");
        var builder = new MaterialShaderBuilder(Library(), _ => force) { Particle = true };
        var defaultOpaque = FxMaterial.Parse("effect.mat", """{"defaultgraph":{"overriden_blend_mode":"Opaque"}}""");
        var additive = builder.Build(defaultOpaque, "AdditiveBlend");
        Assert.Equal("Additive", additive.BlendMode);
        Assert.Equal(4u, additive.PreviewBlendState);

        var explicitCutout = FxMaterial.Parse("trail.mat", """
            {"defaultgraph":{"overriden_blend_mode":"Opaque"},
             "graphinstances":[{"parent":"cutout.fxgraph","alphatest_threshold":174}]}
            """);
        var cutout = builder.Build(explicitCutout, "AdditiveBlend");
        Assert.Equal("AlphaTest", cutout.BlendMode);
        Assert.Contains("#define HAS_ALPHA_TEST_CLIPPING", cutout.Hlsl);
        Assert.Contains("#define PMC_ALPHA_TEST_THRESHOLD (174.0f / 255.0f)", cutout.Hlsl);
    }

    [Fact]
    public void Shimmer_graph_uses_a_signed_displacement_pass()
    {
        var graph = FxGraph.Parse("heat.fxgraph", """{"overriden_blend_mode":"Shimmer","lighting_disabled":true}""");
        var material = FxMaterial.Parse("heat.mat", """{"graphinstances":[{"parent":"heat.fxgraph"}]}""");
        var shader = new MaterialShaderBuilder(Library(), _ => graph) { Particle = true }.Build(material, "AdditiveBlend");
        Assert.Equal(7u, shader.PreviewBlendState);
        Assert.Contains("#define PMC_SHIMMER", shader.Hlsl);
        Assert.Contains("packedOffset.xy - packedOffset.zw", shader.Hlsl);
        Assert.DoesNotContain("#define ADDITIVE_BLEND", shader.Hlsl);
    }

    // wp_outline_2pass: VertexPosition@WorldTransform = position + normal × .Expand, with the material's override.
    private const string OutlineGraph = """
        {"nodes":[
          {"type":"InputVertexPosition","index":0,"stage":"WorldTransform"},
          {"type":"InputVertexNormal","index":0,"stage":"WorldTransform"},
          {"type":"ConstantVertex","index":0,"parameters":[{"value":0.6}],"custom_parameter":".Expand"},
          {"type":"Multiply3","index":1},{"type":"Add3","index":2},
          {"type":"VertexPosition","index":0,"stage":"WorldTransform"}],
         "links":[
          {"src":{"type":"ConstantVertex","index":0,"variable":"output"},"dst":{"type":"Multiply3","index":1,"variable":"a"}},
          {"src":{"type":"InputVertexNormal","index":0,"stage":"WorldTransform","variable":"output"},"dst":{"type":"Multiply3","index":1,"variable":"b"}},
          {"src":{"type":"Multiply3","index":1,"variable":"output"},"dst":{"type":"Add3","index":2,"variable":"a"}},
          {"src":{"type":"InputVertexPosition","index":0,"stage":"WorldTransform","variable":"output","swizzle":"xyz"},"dst":{"type":"Add3","index":2,"variable":"b"}},
          {"src":{"type":"InputVertexPosition","index":0,"stage":"WorldTransform","variable":"output","swizzle":"w"},"dst":{"type":"VertexPosition","index":0,"stage":"WorldTransform","variable":"input","swizzle":"w"}},
          {"src":{"type":"Add3","index":2,"variable":"output"},"dst":{"type":"VertexPosition","index":0,"stage":"WorldTransform","variable":"input","swizzle":"xyz"}}]}
        """;

    [Theory]
    [InlineData("""{"graphinstances":[{"parent":"outline.fxgraph"}]}""", 0.6f)]
    [InlineData("""{"graphinstances":[{"parent":"outline.fxgraph","custom_parameters":[{"name":".Expand","parameters":[{"value":0.25}]}]}]}""", 0.25f)]
    public void Outline_graph_inflates_vertices_along_the_normal(string mat, float expand)
    {
        var graph = FxGraph.Parse("outline.fxgraph", OutlineGraph);
        var shader = new MaterialShaderBuilder(Library(), _ => graph).Build(FxMaterial.Parse("outline.mat", mat));
        Assert.Equal(expand, shader.Vertex.NormalOffset, 4);
        Assert.DoesNotContain(shader.Notes, n => n.Contains("vertex-stage write not previewed: VertexPosition"));
    }

    [Fact]
    public void Vertex_writes_that_are_not_a_normal_inflation_are_left_alone()
    {
        // Position scaled by a constant is not "position + normal × k": no offset.
        var graph = FxGraph.Parse("scale.fxgraph", OutlineGraph.Replace("\"type\":\"InputVertexNormal\",\"index\":0,\"stage\":\"WorldTransform\",\"variable\"",
            "\"type\":\"InputVertexPosition\",\"index\":0,\"stage\":\"WorldTransform\",\"variable\""));
        var shader = new MaterialShaderBuilder(Library(), _ => graph).Build(FxMaterial.Parse("scale.mat", """{"graphinstances":[{"parent":"scale.fxgraph"}]}"""));
        Assert.Equal(0f, shader.Vertex.NormalOffset);
        Assert.DoesNotContain(shader.Notes, n => n.Contains("vertex offset along normal applied"));
    }
}
