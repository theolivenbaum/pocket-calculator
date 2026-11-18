// upstream f8a36a4: `<font size>` maps to the keyword scale through the HTML legacy font size
// rules, and the absolute font-size keywords use the default 16px medium scale. Ports dom.rs
// `legacy_font_size_hints_follow_numeric_rules_and_author_cascade`,
// `legacy_font_size_mutations_restyle_inherited_descendants` and
// `absolute_font_size_keywords_use_the_default_medium_scale`.
using System.Text;
using PocketCalculator.Dom;
using PocketCalculator.Render.Css;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public sealed class LegacyFontSizeTests
{
    private static readonly (string Size, float Expected)[] Cases =
    [
        ("1", 10f), ("2", 13f), ("3", 16f), ("4", 18f),
        ("5", 24f), ("6", 32f), ("7", 48f), ("0", 10f), ("8", 48f),
        ("+1", 18f), ("-1", 13f), ("-2", 10f), ("+0", 16f), ("-0", 16f),
        (" \t+2tail", 24f), ("\n2", 13f),
        ("999999999999999999999999", 48f), ("-999999999999999999999999", 10f),
        ("", 20f), ("word", 20f), ("+ 1", 20f), ("- 1", 20f), (" 2", 20f),
    ];

    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException(id);

    [Fact]
    public void LegacyFontSizeHintsFollowNumericRulesAndAuthorCascade()
    {
        var html = new StringBuilder(
            "<!doctype html><style>html{font-size:16px}body{font-size:20px}#sheet{font-size:21px}</style>");
        for (int index = 0; index < Cases.Length; index++)
        {
            html.Append($"<font id=f{index} size='{Cases[index].Size}'><span id=c{index}>x</span></font>");
        }

        html.Append("<font id=sheet size=1>x</font><font id=inline size=1 style='font-size:23px'>x</font><span id=ordinary size=1>x</span>");
        DomTree tree = HtmlParsing.ParseHtml(html.ToString());
        DomLayout laid = RenderDom.LayoutDom(tree, (640f, 480f));
        for (int index = 0; index < Cases.Length; index++)
        {
            foreach (string id in new[] { $"f{index}", $"c{index}" })
            {
                Assert.True(
                    laid.Styles[Id(tree, id)].FontSize == Cases[index].Expected,
                    $"{id}: size={Cases[index].Size}: {laid.Styles[Id(tree, id)].FontSize}");
            }
        }

        foreach ((string id, float expected) in new[] { ("sheet", 21f), ("inline", 23f), ("ordinary", 20f) })
        {
            Assert.Equal(expected, laid.Styles[Id(tree, id)].FontSize);
        }
    }

    [Fact]
    public void LegacyFontSizeMutationsRestyleInheritedDescendants()
    {
        const string html = "<!doctype html><style>body{font-size:20px}</style><font id=font size=1><span id=child>x</span></font><aside>clean</aside>";
        (float, float) viewport = (640f, 480f);
        DomTree tree = HtmlParsing.ParseHtml(html);
        NodeId font = Id(tree, "font");
        NodeId child = Id(tree, "child");
        RenderResourceCache resources = new();
        StylesheetCache cache = new();
        PreparedRender previous = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCache(
            tree, viewport, null, resources, [], cache)!;
        Assert.Equal(10f, previous.Layout.Styles[font].FontSize);

        foreach ((string? old, string? next, float expected) in new (string?, string?, float)[]
        {
            ("1", "6", 32f), ("6", null, 20f),
        })
        {
            if (next is not null)
            {
                tree.GetNode(font)!.SetAttribute("size", next);
            }
            else
            {
                tree.GetNode(font)!.RemoveAttribute("size");
            }

            PreparedRender incremental = RenderPaint.PrepareDomWithRetainedAttributeStyles(
                tree, viewport, null, resources, [], cache, previous,
                [new AttributeStyleMutation(font, "size", old, next)])!;
            foreach (NodeId node in new[] { font, child })
            {
                Assert.Equal(expected, incremental.Layout.Styles[node].FontSize);
            }

            DomLayout full = RenderDom.LayoutDom(tree, viewport);
            foreach (NodeId node in new[] { font, child })
            {
                Assert.Equal(full.Styles[node].FontSize, incremental.Layout.Styles[node].FontSize);
                Assert.Equal(full.Rects[node], incremental.Layout.Rects[node]);
            }

            previous = incremental;
        }
    }

    [Theory]
    [InlineData("xx-small", 9f)]
    [InlineData("x-small", 10f)]
    [InlineData("small", 13f)]
    [InlineData("medium", 16f)]
    [InlineData("large", 18f)]
    [InlineData("x-large", 24f)]
    [InlineData("xx-large", 32f)]
    [InlineData("xxx-large", 48f)]
    public void AbsoluteFontSizeKeywordsUseTheDefaultMediumScale(string keyword, float expected)
    {
        DomTree tree = HtmlParsing.ParseHtml(
            $"<!doctype html><style>html{{font-size:16px}}body{{font-size:20px}}</style><span id=k style='font-size:{keyword}'>x</span>");
        DomLayout laid = RenderDom.LayoutDom(tree, (640f, 480f));
        Assert.Equal(expected, laid.Styles[Id(tree, "k")].FontSize);
    }
}
