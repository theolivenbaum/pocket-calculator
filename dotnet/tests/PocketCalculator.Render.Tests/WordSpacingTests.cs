// upstream bf10c46 (#955): word-spacing reaches cascade, inheritance, shaping, the word-leaf
// fallback, native control widths and paint. Ports layout_test.rs `word_spacing_*` and
// word_spacing_shaping.rs; the shaping tests run against the HarfBuzz shaper, which stands in
// for the vendored cosmic-text the Rust tests drive.
using PocketCalculator.Dom;
using SkiaSharp;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public sealed class WordSpacingTests
{
    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException(id);

    [Fact]
    public void WordSpacingChangesIntrinsicWidth()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <style>
            span { display:inline-block; font:40px/60px monospace; white-space:nowrap }
            #spaced { word-spacing:30px }
            </style><span id="normal">A B C</span><span id="spaced">A B C</span>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (600f, 200f));
        float Width(string id) => layout.Rects[Id(tree, id)].Width;
        Assert.True(MathF.Abs(Width("spaced") - Width("normal") - 60f) < 0.1f);
    }

    /// <remarks>
    /// Rust also rejects <c>10%</c> and <c>1e99px</c>, and so does the port (no percentage
    /// resolution, no clamping of huge lengths); Chromium accepts both.
    /// </remarks>
    [Theory]
    [InlineData("bogus")]
    [InlineData("30")]
    [InlineData("10%")]
    [InlineData("3 px")]
    [InlineData("min(30px,bogus)")]
    [InlineData("calc(30px) bogus")]
    [InlineData("calc(30)")]
    [InlineData("calc(2px * 3px)")]
    [InlineData("min(1px,max(2px,bogus))")]
    [InlineData("1px 2px")]
    [InlineData("1e99px")]
    public void WordSpacingRejectsInvalidAndUnsupportedLengths(string value)
    {
        DomTree tree = HtmlParsing.ParseHtml(
            $"<!doctype html><div id='copy' style='word-spacing:3px;word-spacing:{value}'>A B</div>");
        DomLayout layout = RenderDom.LayoutDom(tree, (400f, 200f));
        Assert.Equal(3f, layout.Styles[Id(tree, "copy")].WordSpacing);
        Assert.False(ComputedStyle.SupportsDeclaration("word-spacing", value), value);
    }

    /// <remarks>
    /// The function rows are a deviation: Rust rejects every function, Chromium accepts them
    /// (measured: <c>calc(1em + 2px)</c> is 18px at 16px).
    /// </remarks>
    [Theory]
    [InlineData("normal")]
    [InlineData("0")]
    [InlineData("-3px")]
    [InlineData(".75em")]
    [InlineData("2rem")]
    [InlineData("calc(30px)")]
    [InlineData("calc(1em + 2px)")]
    [InlineData("min(10px, 1em)")]
    public void WordSpacingSupportsValidLengths(string value) =>
        Assert.True(ComputedStyle.SupportsDeclaration("word-spacing", value), value);

    [Fact]
    public void WordSpacingResolvesFunctionsAgainstTheOwnFont()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <div id="calc" style="font-size:20px;word-spacing:calc(1em + 2px)">A B</div>
            <div id="min" style="font-size:20px;word-spacing:min(30px, 1em)">A B</div>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (400f, 200f));
        Assert.Equal(22f, layout.Styles[Id(tree, "calc")].WordSpacing);
        Assert.Equal(20f, layout.Styles[Id(tree, "min")].WordSpacing);
    }

    [Fact]
    public void WordSpacingResolvesBeforeInheritanceAndPreservesCascade()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <style>
            section { font-size:20px; word-spacing:1.5em }
            span { display:inline-block; font-size:40px; white-space:nowrap }
            #normal { word-spacing:normal }
            #inherit { word-spacing:3px; word-spacing:inherit }
            #unset { word-spacing:3px; word-spacing:unset }
            #invalid { word-spacing:3px; word-spacing:bogus; word-spacing:calc(bogus) }
            #negative { word-spacing:-.25em }
            #revert { word-spacing:3px; word-spacing:revert }
            </style><section>
            <span id="inherited">A B C</span><span id="normal">A B C</span>
            <span id="inherit">A B C</span><span id="unset">A B C</span>
            <span id="invalid">A B C</span><span id="negative">A B C</span>
            <span id="revert">A B C</span></section>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (1800f, 300f));
        float Width(string id) => layout.Rects[Id(tree, id)].Width;
        foreach ((string id, float spacing) in new[]
        {
            ("inherited", 30f), ("normal", 0f), ("inherit", 30f), ("unset", 30f),
            ("invalid", 3f), ("negative", -10f), ("revert", 30f),
        })
        {
            Assert.Equal(spacing, layout.Styles[Id(tree, id)].WordSpacing);
            Assert.True(
                MathF.Abs(Width(id) - Width("normal") - (2f * spacing)) <= 1f,
                $"{id}: {Width(id)} vs {Width("normal")}");
        }
    }

    [Fact]
    public void WordSpacingChangesWrappingAndNativeControlWidths()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <style>
            div { width:140px; font:40px/60px monospace }
            .spaced { word-spacing:30px }
            button, select { font:40px/60px monospace }
            </style><div id="normal">A B C</div><div id="spaced" class="spaced">A B C</div>
            <button id="button">A B C</button><button id="button-spaced" class="spaced">A B C</button>
            <select id="select"><option>A B C</option></select>
            <select id="select-spaced" class="spaced"><option>A B C</option></select>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (1000f, 500f));
        Rect R(string id) => layout.Rects[Id(tree, id)];
        Assert.Equal(60f, R("normal").Height);
        Assert.Equal(120f, R("spaced").Height);
        foreach ((string normal, string spaced) in new[] { ("button", "button-spaced"), ("select", "select-spaced") })
        {
            Assert.True(
                MathF.Abs(R(spaced).Width - R(normal).Width - 60f) <= 1f,
                $"{normal}: {R(normal)} -> {R(spaced)}");
        }
    }

    [Theory]
    [InlineData("  A   B  C  ", "", 60f)]
    [InlineData("A&nbsp;B&nbsp;C", "", 60f)]
    [InlineData("A  B", "white-space:pre", 60f)]
    [InlineData("A\tB", "white-space:pre", 0f)]
    [InlineData("A B C", "letter-spacing:2px", 60f)]
    [InlineData("A<span> B</span> C", "", 60f)]
    [InlineData("אב גד", "direction:rtl", 30f)]
    public void WordSpacingHandlesWhitespaceAndNestedSpans(string content, string extraCss, float delta)
    {
        DomTree tree = HtmlParsing.ParseHtml($$"""
            <style>
            div { display:inline-block; font:40px/60px monospace; white-space:nowrap; {{extraCss}} }
            #spaced { word-spacing:30px }
            </style><div id="normal">{{content}}</div><div id="spaced">{{content}}</div>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (1000f, 300f));
        float Width(string id) => layout.Rects[Id(tree, id)].Width;
        Assert.True(
            MathF.Abs(Width("spaced") - Width("normal") - delta) <= 1f,
            $"{content}, {extraCss}: {Width("normal")} -> {Width("spaced")}");
    }

    [Fact]
    public void WordSpacingAppliesToGeneratedContent()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <style>
            div { display:inline-block; font:40px/60px monospace; white-space:nowrap }
            #spaced { word-spacing:30px }
            div::before { content:'A B C'; font-size:20px }
            div::after { content:'A B C'; word-spacing:normal }
            </style><div id="normal"></div><div id="spaced"></div>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (1000f, 300f));
        NodeId id = Id(tree, "spaced");
        Assert.Equal(30f, layout.Styles[id].BeforePseudo!.WordSpacing);
        Assert.Equal(0f, layout.Styles[id].AfterPseudo!.WordSpacing);
        float normal = layout.Rects[Id(tree, "normal")].Width;
        Assert.True(MathF.Abs(layout.Rects[id].Width - normal - 60f) <= 1f, $"{normal} -> {layout.Rects[id].Width}");
    }

    [Fact]
    public void WordSpacingMovesPaintedGlyphs()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <style>
            body { margin:0; background:white; color:black }
            div { height:80px; font:40px/60px monospace; white-space:nowrap }
            #spaced { word-spacing:30px }
            </style><div>A B C</div><div id="spaced">A B C</div>
            """);
        byte[] png = RenderPaint.ScreenshotPng(tree, (400f, 180f), null)!;
        using SKBitmap bitmap = SKBitmap.Decode(png);
        List<int> Starts(int top)
        {
            List<int> result = [];
            bool previous = false;
            for (int x = 0; x < 400; x++)
            {
                bool ink = false;
                for (int y = top; y < top + 70 && !ink; y++)
                {
                    SKColor pixel = bitmap.GetPixel(x, y);
                    ink = pixel.Red < 128 && pixel.Green < 128 && pixel.Blue < 128;
                }

                if (ink && !previous)
                {
                    result.Add(x);
                }

                previous = ink;
            }

            return result;
        }

        List<int> normal = Starts(0);
        List<int> spaced = Starts(80);
        Assert.Equal(3, normal.Count);
        Assert.Equal(3, spaced.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(
                Math.Abs(spaced[i] - normal[i] - (i * 30)) <= 1,
                $"painted positions: [{string.Join(",", normal)}] -> [{string.Join(",", spaced)}]");
        }
    }

    [Fact]
    public void WordSpacingWidensStaticTextMeasure()
    {
        float normal = DomTextMeasure.TextWidth("A B C", 40f, false, "monospace", 0f);
        float spaced = DomTextMeasure.TextWidth("A B C", 40f, false, "monospace", 0f, 30f);
        Assert.Equal(60f, spaced - normal, 0.001f);
    }

    // ---- word_spacing_shaping.rs -------------------------------------------------------

    private static TextAttrs MonoAttrs(float? wordSpacingEm = null) => new()
    {
        Family = "Liberation Mono",
        Metrics = new TextMetrics(16f, 18f),
        WordSpacingEm = wordSpacingEm,
    };

    private static List<float> Advances(TextShaper shaper, string text, AttrsList attrs)
    {
        List<float> result = [];
        foreach (ShapeSpan span in shaper.ShapeParagraph(text, attrs, 8).Spans)
        {
            foreach (ShapeWord word in span.Words)
            {
                foreach (ShapeGlyph glyph in word.Glyphs)
                {
                    result.Add(glyph.XAdvance);
                }
            }
        }

        return result;
    }

    [Fact]
    public void WordSpacingUsesSeparatorAttributesAndCacheIdentity()
    {
        var engine = new TextEngine();
        var shaper = new TextShaper(engine.Database);
        List<float> normal = Advances(shaper, "A B C", new AttrsList(MonoAttrs()));
        foreach (float spacing in new[] { 0.75f, -0.25f, -1f, 0f, 0.75f })
        {
            var attrs = new AttrsList(MonoAttrs());
            attrs.AddSpan(1, 2, MonoAttrs(spacing));
            attrs.AddSpan(3, 4, MonoAttrs(spacing * 2f));
            List<float> actual = Advances(shaper, "A B C", attrs);
            Assert.Equal(normal.Count, actual.Count);
            for (int i = 0; i < actual.Count; i++)
            {
                float extra = i switch { 1 => spacing, 3 => spacing * 2f, _ => 0f };
                Assert.True(MathF.Abs(actual[i] - normal[i] - extra) < 0.00001f, $"glyph {i}: {actual[i]} vs {normal[i]}");
            }
        }
    }

    [Theory]
    [InlineData("A B", 1)]
    [InlineData("A\tB", 0)]
    [InlineData("A B", 0)]
    [InlineData("A  B", 2)]
    [InlineData("office", 0)]
    public void WordSpacingPreservesTabsAndNonSeparatorGlyphs(string text, int separators)
    {
        var engine = new TextEngine();
        var shaper = new TextShaper(engine.Database);
        List<float> normal = Advances(shaper, text, new AttrsList(MonoAttrs()));
        List<float> spaced = Advances(shaper, text, new AttrsList(MonoAttrs(0.75f)));
        Assert.Equal(normal.Count, spaced.Count);
        float delta = spaced.Sum() - normal.Sum();
        Assert.True(MathF.Abs(delta - (separators * 0.75f)) < 0.00001f, $"{text}: {delta}");
    }
}
