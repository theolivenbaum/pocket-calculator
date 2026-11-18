// upstream 542df14 (the render part): explicit `contain: size / inline-size / strict` applies
// size containment. Ports dom.rs `explicit_size_containment_bounds_a_flex_scroll_viewport` (over
// render-repros/contained-flex-grid.html) and
// `explicit_containment_inheritance_reset_and_query_independence`.
using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public sealed class ExplicitContainmentTests
{
    [Fact]
    public void ExplicitSizeContainmentBoundsAFlexScrollViewport()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <!doctype html>
            <style>
              html, body { margin: 0; }
              #panel { display: flex; flex-direction: column; width: 400px; height: 600px;
                box-sizing: border-box; border: 1px solid; }
              #header { height: 40px; flex-shrink: 0; }
              #content { flex: 1 1 auto; contain: size layout; padding: 0 8px 8px; }
              #wrapper { height: 100%; }
              #grid { display: grid; height: 100%; overflow: auto;
                grid-template-rows: 34px repeat(598, 36px); }
              #after { width: 400px; height: 20px; }
            </style>
            <div id="panel"><div id="header"></div><div id="content">
              <div id="wrapper"><div id="grid"><div id="cell">Visible text</div></div></div>
            </div></div>
            <div id="after"></div>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (800f, 800f));
        Rect R(string id) => layout.Rects[tree.GetElementById(id)!.Value];
        Assert.Equal(600f, R("panel").Height);
        Assert.Equal(558f, R("content").Height);
        Assert.Equal(550f, R("grid").Height);
        Assert.Equal(34f, R("cell").Height);
    }

    [Fact]
    public void ExplicitContainmentInheritanceResetAndQueryIndependence()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <style>
                html,body{margin:0}
                .leaf{height:80px}
                #size{contain:size layout}
                #inherited{contain:inherit}
                #reset{contain:size;contain:none}
                #invalid{contain:size;contain:size inline-size;contain:size size}
                #inline{display:inline-block;contain:inline-size}
                #strict{contain:strict}
                #content{contain:content}
            </style>
            <div id="size"><div id="inherited"><div class="leaf"></div></div>
                <div id="ordinary"><div class="leaf"></div></div></div>
            <div id="reset"><div class="leaf"></div></div>
            <div id="invalid"><div class="leaf"></div></div>
            <div id="inline"><div class="leaf" style="width:300px"></div></div>
            <div id="strict"><div class="leaf"></div></div>
            <div id="content"><div class="leaf"></div></div>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (800f, 800f));
        NodeId Id(string name) => tree.GetElementById(name)!.Value;
        foreach (string name in new[] { "size", "inherited", "invalid", "strict" })
        {
            Assert.True(layout.Rects[Id(name)].Height == 0f, $"{name}: {layout.Rects[Id(name)]}");
            Assert.Equal(ContainerType.Normal, DomStyleFixups.EffectiveContainerType(layout.Styles[Id(name)]));
        }

        foreach (string name in new[] { "ordinary", "reset", "inline", "content" })
        {
            Assert.True(layout.Rects[Id(name)].Height == 80f, $"{name}: {layout.Rects[Id(name)]}");
        }

        Assert.Equal(0f, layout.Rects[Id("inline")].Width);
        Assert.True(layout.Styles[Id("inherited")].EstablishesPositioningContainingBlock());
        Assert.False(layout.Styles[Id("reset")].EstablishesPositioningContainingBlock());
    }
}
