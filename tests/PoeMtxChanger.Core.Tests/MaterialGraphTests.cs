using System.Numerics;
using System.Text.Json;
using PoeMtxChanger.Core.Formats.Geometry;

namespace PoeMtxChanger.Core.Tests;

public sealed class MaterialGraphTests
{
    [Fact]
    public void RunsParameterizedAlbedoBranchAndPreservesAlpha()
    {
        var graph=new MaterialGraph("""
            {
              "overriden_blend_mode":"AlphaBlend",
              "nodes":[
                {"type":"InputAlbedoColor","index":0,"stage":"Texturing"},
                {"type":"ConstantPixel3","index":0,"custom_parameter":"Tone","parameters":[{"value":[1,1,1],"srgb":true}]},
                {"type":"Multiply3","index":0},
                {"type":"AlbedoColor","index":0,"stage":"Texturing"}
              ],
              "links":[
                {"src":{"type":"InputAlbedoColor","index":0,"stage":"Texturing","swizzle":"xyz"},"dst":{"type":"Multiply3","index":0,"variable":"a"}},
                {"src":{"type":"ConstantPixel3","index":0},"dst":{"type":"Multiply3","index":0,"variable":"b"}},
                {"src":{"type":"Multiply3","index":0},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing","variable":"input","swizzle":"xyz"}},
                {"src":{"type":"InputAlbedoColor","index":0,"stage":"Texturing","swizzle":"w"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing","variable":"input","swizzle":"w"}}
              ]
            }
            """);
        using var material=JsonDocument.Parse("""
            {"custom_parameters":[{"name":"Tone","parameters":[{"value":[1.0,0.5,0.0]}]}]}
            """);
        Assert.Equal("AlphaBlend",graph.BlendMode);
        Assert.True(graph.TryCompile(MaterialColourChannel.Albedo,material.RootElement,out var program,out _));
        var result=program!.Evaluate(new(.25f,.5f,.75f,.42f));
        Assert.Equal(.25f,result.X,5);
        Assert.Equal(.10702057f,result.Y,5);
        Assert.Equal(0,result.Z,5);
        Assert.Equal(.42f,result.W,5);
    }

    [Fact]
    public void ReportsSceneDependentOperationsInsteadOfBakingThemWithFakeInputs()
    {
        var graph=new MaterialGraph("""
            {
              "nodes":[{"type":"InputAlbedoColor","index":0},{"type":"DepthDistance","index":0},{"type":"AlbedoColor","index":0}],
              "links":[
                {"src":{"type":"InputAlbedoColor","index":0,"swizzle":"xyz"},"dst":{"type":"AlbedoColor","index":0,"variable":"input","swizzle":"xyz"}},
                {"src":{"type":"DepthDistance","index":0},"dst":{"type":"AlbedoColor","index":0,"variable":"input","swizzle":"w"}}
              ]
            }
            """);
        Assert.False(graph.TryCompile(MaterialColourChannel.Albedo,default,out var program,out var unsupported));
        Assert.Null(program);
        Assert.Equal("DepthDistance",unsupported);
    }
}
