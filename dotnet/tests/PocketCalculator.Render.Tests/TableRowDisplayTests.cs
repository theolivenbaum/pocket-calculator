// upstream 2b61abf (hidden rows and sections leave the table model) and 7bf782b (table row and
// group display keywords). Ports the layout_test.rs facts of the same names.
using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public sealed class TableRowDisplayTests
{
    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException(id);

    [Fact]
    public void HiddenTableRowsDoNotGenerateCellsTracksOrRowspanSlots()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <style>
            html,body{margin:0}table{border-spacing:0}td{width:30px;height:20px;padding:0}
            .gone{display:none}.wide{width:500px;height:90px}
            </style><table id="table"><tbody>
            <tr><td id="span" rowspan="2"></td><td></td></tr>
            <tr class="gone" id="gone"><td class="wide" id="gone-cell"></td></tr>
            <tr><td id="next"></td></tr><tr><td id="last"></td></tr>
            </tbody></table>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (600f, 400f));
        Rect R(string id) => layout.Rects[Id(tree, id)];
        foreach (string id in new[] { "gone", "gone-cell" })
        {
            Assert.False(layout.Rects.ContainsKey(Id(tree, id)), $"hidden {id} generated a box");
        }

        Assert.Equal((60f, 60f), (R("table").Width, R("table").Height));
        Assert.Equal(40f, R("span").Height);
        Assert.Equal((30f, 20f), (R("next").X, R("next").Y));
        Assert.Equal((0f, 40f), (R("last").X, R("last").Y));
    }

    [Theory]
    [InlineData("thead")]
    [InlineData("tbody")]
    [InlineData("tfoot")]
    public void HiddenTableSectionsDoNotContributeRowsOrCellWidths(string section)
    {
        DomTree tree = HtmlParsing.ParseHtml($"""
            <style>
            html,body{"{"}margin:0{"}"}table{"{"}border-spacing:0{"}"}td{"{"}width:30px;height:20px;padding:0{"}"}
            </style><table id="table"><{section} id="gone" style="display:none">
            <tr id="gone-row"><td id="gone-cell" style="width:500px;height:90px;visibility:visible"></td></tr>
            </{section}><tbody><tr style="visibility:hidden"><td id="invisible"></td></tr>
            <tr><td id="visible"></td></tr></tbody></table>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (600f, 400f));
        foreach (string id in new[] { "gone", "gone-row", "gone-cell" })
        {
            Assert.False(layout.Rects.ContainsKey(Id(tree, id)), $"display:none {section} generated a box for {id}");
        }

        Rect R(string id) => layout.Rects[Id(tree, id)];
        Assert.Equal((30f, 40f), (R("table").Width, R("table").Height));
        Assert.Equal(20f, R("invisible").Height);
        Assert.Equal(20f, R("visible").Y);
    }

    [Theory]
    [InlineData("tr", "table-row")]
    [InlineData("tbody", "table-row-group")]
    [InlineData("thead", "table-header-group")]
    [InlineData("tfoot", "table-footer-group")]
    public void TableDisplayKeywordsRestoreHiddenNativeRowsAndGroups(string tag, string display)
    {
        string content = tag == "tr" ? "<td id='cell'></td>" : "<tr><td id='cell'></td></tr>";
        DomTree tree = HtmlParsing.ParseHtml($"""
            <style>html,body{"{"}margin:0{"}"}
            table{"{"}border-spacing:0{"}"}td{"{"}width:40px;height:20px;padding:0{"}"}</style>
            <table id="table"><{tag} id="restored" style="display:none;display:{display}">
            {content}</{tag}></table>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (600f, 400f));
        foreach (string id in new[] { "table", "restored", "cell" })
        {
            Assert.True(layout.Rects.TryGetValue(Id(tree, id), out Rect rect), $"{display}: missing {id} box");
            Assert.Equal((40f, 20f), (rect.Width, rect.Height));
        }
    }

    [Fact]
    public void CssTableRowsShareTracksAndGenerateRowAndGroupBoxes()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <style>html,body{margin:0}.table{display:table;border-spacing:0}
            .group{display:table-row-group}.row{display:table-row}.cell{display:table-cell;height:20px}
            .a{width:30px}.b{width:40px}</style><div id="table" class="table"><div id="group" class="group">
            <div id="row1" class="row"><div id="a1" class="cell a"></div><div id="b1" class="cell b"></div></div>
            <div id="row2" class="row"><div id="a2" class="cell a"></div><div id="b2" class="cell b"></div></div>
            </div></div>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (600f, 400f));
        Rect R(string id) => layout.Rects[Id(tree, id)];
        foreach (string id in new[] { "table", "group" })
        {
            Assert.Equal((70f, 40f), (R(id).Width, R(id).Height));
        }

        foreach ((string id, float x, float y, float w, float h) in new[]
        {
            ("row1", 0f, 0f, 70f, 20f), ("row2", 0f, 20f, 70f, 20f),
            ("a1", 0f, 0f, 30f, 20f), ("b1", 30f, 0f, 40f, 20f),
            ("a2", 0f, 20f, 30f, 20f), ("b2", 30f, 20f, 40f, 20f),
        })
        {
            Rect r = R(id);
            Assert.True((r.X, r.Y, r.Width, r.Height) == (x, y, w, h), $"{id}: {r}");
        }
    }

    [Theory]
    [InlineData("table-row")]
    [InlineData("table-row-group")]
    [InlineData("table-header-group")]
    [InlineData("table-footer-group")]
    public void TableDisplayKeywordsComputeInheritResetAndBlockify(string display)
    {
        DomTree tree = HtmlParsing.ParseHtml($"""
            <div id="parent" style="display:{display}">
            <span id="inherited" style="display:inherit"></span></div>
            <div style="display:flex"><div id="item" style="display:{display}"></div></div>
            <div id="reset" style="display:{display};display:block"></div>
            <div id="invalid" style="display:{display};display:invalid"></div>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (600f, 400f), null, resources)
            ?? throw new InvalidOperationException("layout did not prepare");
        foreach ((string id, string expected) in new[]
        {
            ("parent", display), ("inherited", display), ("item", "block"),
            ("reset", "block"), ("invalid", display),
        })
        {
            Assert.True(
                prepared.ComputedStyle(Id(tree, id), null)!["display"] == expected,
                $"{display}: {id} = {prepared.ComputedStyle(Id(tree, id), null)!["display"]}");
        }

        Assert.True(ComputedStyle.SupportsDeclaration("display", display));
    }

    [Fact]
    public void CssTableHeaderAndFooterGroupsUseVisualOrder()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <style>html,body{margin:0}.table{display:table;border-spacing:0}
            .row{display:table-row}.cell{display:table-cell;width:30px;height:20px}</style>
            <div class="table"><div style="display:table-footer-group"><div class="row"><div id="footer" class="cell"></div></div></div>
            <div class="row"><div id="body" class="cell"></div></div>
            <div style="display:table-header-group"><div class="row"><div id="header" class="cell"></div></div></div></div>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (600f, 400f));
        foreach ((string id, float y) in new[] { ("header", 0f), ("body", 20f), ("footer", 40f) })
        {
            Assert.True(layout.Rects[Id(tree, id)].Y == y, $"{id}: {layout.Rects[Id(tree, id)]}");
        }
    }

    /// <remarks>Measured on Chromium (Playwright, 2026-11-18): only the first thead and tfoot move.</remarks>
    [Fact]
    public void NativeHeaderAndFooterGroupsUseVisualOrder()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <style>html,body{margin:0}table{border-spacing:0}td{width:30px;height:20px;padding:0}</style>
            <table><tfoot><tr><td id=f></td></tr></tfoot><tbody><tr><td id=b></td></tr></tbody>
            <thead><tr><td id=h></td></tr></thead><thead><tr><td id=h2></td></tr></thead></table>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (600f, 400f));
        foreach ((string id, float y) in new[] { ("h", 0f), ("b", 20f), ("h2", 40f), ("f", 60f) })
        {
            Assert.True(layout.Rects[Id(tree, id)].Y == y, $"{id}: {layout.Rects[Id(tree, id)]}");
        }
    }

    [Fact]
    public void NativeRowGeometryRetainsCellsWithAuthoredBlockDisplay()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <style>html,body{margin:0}table{border-spacing:0}
            td{display:block;width:40px;height:20px;padding:0}</style><table><tr id="row"><td></td></tr></table>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (600f, 400f));
        Assert.True(layout.Rects.TryGetValue(Id(tree, "row"), out Rect row), "native row box");
        Assert.Equal((40f, 20f), (row.Width, row.Height));
    }

    [Fact]
    public void TableRowDisplayDoesNotUnhideNativeHiddenInputs()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <div style="display:flex">
            <input id="flex" type="hidden" style="display:table-row;width:40px;height:20px"></div>
            <input id="absolute" type="hidden" style="display:table-row-group;position:absolute;width:40px;height:20px">
            <table style="display:flex"><tbody id="attribute" hidden><tr><td></td></tr></tbody></table>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (600f, 400f));
        foreach (string id in new[] { "flex", "absolute", "attribute" })
        {
            Assert.False(layout.Rects.ContainsKey(Id(tree, id)), $"{id} became visible");
        }
    }

    [Theory]
    [InlineData("<div class='row'><div class='cell'></div></div><div id='keep'></div>")]
    [InlineData("<div class='group'><div class='row'><div class='cell'></div></div><div id='keep'></div></div>")]
    [InlineData("<div class='row'><div class='cell'></div><div id='keep'></div></div>")]
    public void CssTableMixedChildrenAreNotDiscarded(string content)
    {
        DomTree tree = HtmlParsing.ParseHtml($"""
            <style>.table{"{"}display:table{"}"}.group{"{"}display:table-row-group{"}"}
            .row{"{"}display:table-row{"}"}.cell{"{"}display:table-cell;width:30px;height:20px{"}"}
            #keep{"{"}width:40px;height:17px{"}"}</style><div class="table">{content}</div>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (600f, 400f));
        Assert.True(layout.Rects.TryGetValue(Id(tree, "keep"), out Rect kept), "mixed child box");
        Assert.Equal((40f, 17f), (kept.Width, kept.Height));
    }
}
