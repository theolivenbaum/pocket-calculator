using PocketCalculator.Dom;
using PocketCalculator.Render.Css;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// The native form-control widgets the port paints from its own code: a range input's thumb, a
/// checkbox/radio box, and a date/time input's read-only field text. All three are deviations
/// from <c>crates/obscura-render/src/paint.rs</c>, which paints none of them, so the expected
/// values here were read off Chromium 141 rather than off the reference engine.
/// </summary>
public class NativeControlPaintTests
{
    private static DomTree Parse(string html) => HtmlParsing.ParseHtml(html);

    private static NodeId Selector(DomTree tree, string selector) =>
        tree.QuerySelector(selector) ?? throw new InvalidOperationException($"no match for {selector}");

    /// <summary>Count pixels close to <paramref name="colour"/> inside a box.</summary>
    private static int CountNear(
        Pixmap pixmap,
        (int R, int G, int B) colour,
        int x0,
        int y0,
        int x1,
        int y1,
        int tolerance = 40)
    {
        int found = 0;
        for (uint y = (uint)Math.Max(y0, 0); y < (uint)Math.Max(y1, 0) && y < pixmap.Height; y++)
        {
            for (uint x = (uint)Math.Max(x0, 0); x < (uint)Math.Max(x1, 0) && x < pixmap.Width; x++)
            {
                if (pixmap.Pixel(x, y) is not { } pixel)
                {
                    continue;
                }

                if (Math.Abs(pixel.R - colour.R) + Math.Abs(pixel.G - colour.G)
                    + Math.Abs(pixel.B - colour.B) < tolerance)
                {
                    found++;
                }
            }
        }

        return found;
    }

    private static int CountNonWhite(Pixmap pixmap, int x0, int y0, int x1, int y1)
    {
        int found = 0;
        for (uint y = (uint)Math.Max(y0, 0); y < (uint)Math.Max(y1, 0) && y < pixmap.Height; y++)
        {
            for (uint x = (uint)Math.Max(x0, 0); x < (uint)Math.Max(x1, 0) && x < pixmap.Width; x++)
            {
                if (pixmap.Pixel(x, y) is { } pixel
                    && (pixel.R < 245 || pixel.G < 245 || pixel.B < 245))
                {
                    found++;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// A3. Script assigns <c>element.value</c>, which HTML keeps off the content attribute, so
    /// the renderer sees it only through the dirty-value mirror. Chromium paints that value in
    /// the field's own colour; before the mirror existed the control painted its placeholder.
    /// </summary>
    [Fact]
    public void ScriptAssignedValuePaintsInsteadOfThePlaceholder()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                html,body { margin:0; background:white }
                input { display:block; box-sizing:border-box; width:240px; height:40px;
                        padding:0; border:0; font-size:24px; background:white;
                        color:rgb(0,0,0) }
                input::placeholder { color:rgb(255,0,0) }
            </style></head><body>
                <input id="field" placeholder="placeholder">
            </body></html>
            """);
        NodeId field = Selector(tree, "#field");

        RenderResourceCache before = new();
        PreparedRender beforePrepared = RenderPaint.PrepareDom(tree, (260f, 60f), null, before)!;
        Pixmap beforePixmap = RenderPaint.PaintPrepared(tree, beforePrepared, before, (0f, 0f))!;
        Assert.True(
            CountNear(beforePixmap, (255, 0, 0), 0, 0, 240, 40) > 10,
            "with no value the placeholder must paint in the placeholder colour");

        // What `input.value = "typed"` leaves behind. The content attribute stays unset, which
        // is exactly why reading it was not enough.
        tree.SetDirtyFormValue(field, "typed");
        Assert.Null(tree.GetNode(field)!.GetAttribute("value"));

        RenderResourceCache after = new();
        PreparedRender afterPrepared = RenderPaint.PrepareDom(tree, (260f, 60f), null, after)!;
        Pixmap afterPixmap = RenderPaint.PaintPrepared(tree, afterPrepared, after, (0f, 0f))!;
        Assert.Equal(0, CountNear(afterPixmap, (255, 0, 0), 0, 0, 240, 40));
        Assert.True(
            CountNear(afterPixmap, (0, 0, 0), 0, 0, 240, 40) > 10,
            "the value must paint in the field's own colour");
    }

    /// <summary>
    /// A widget-shaped input has no text value to show. The general input arm used to paint the
    /// <c>value</c> attribute for every type, which put "50" beside a slider.
    /// </summary>
    [Fact]
    public void AWidgetInputDoesNotPaintItsValueAsText()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                html,body { margin:0; background:white }
                input { -webkit-appearance:none; display:block; margin:0;
                        width:200px; height:4px; background:white; border:0 }
            </style></head><body>
                <input type="range" min="0" max="100" value="50">
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (220f, 60f), null, resources)!;
        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;

        // The native track and knob are the only ink; no glyphs anywhere near the left edge,
        // which is where a painted "50" landed.
        Assert.Equal(0, CountNear(pixmap, (0, 0, 0), 0, 0, 30, 30, tolerance: 120));
    }

    /// <summary>
    /// A1. <c>::-webkit-slider-thumb</c> is matched and cascaded onto a synthesized thumb box,
    /// so the size, radius, background and border a page writes are the ones painted. The
    /// position along the track comes from <c>value</c> against <c>min</c>/<c>max</c>.
    /// </summary>
    [Theory]
    [InlineData("0", 0)]
    [InlineData("50", 92)]
    [InlineData("100", 184)]
    public void AuthorSliderThumbRulesDecideTheThumbBox(string value, int expectedLeft)
    {
        DomTree tree = Parse(
            $$"""
            <html><head><style>
                html,body { margin:0; background:white }
                .s { -webkit-appearance:none; display:block; margin:0; padding:0; border:0;
                     width:200px; height:4px; background:white }
                .s::-webkit-slider-thumb { -webkit-appearance:none; width:16px; height:24px;
                     border-radius:10px; background-color:rgb(255,255,255);
                     border:2px solid rgb(0,120,212) }
            </style></head><body>
                <input class="s" type="range" min="0" max="100" value="{{value}}">
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (220f, 60f), null, resources)!;

        LayoutStyle thumb = prepared.Layout.Styles[Selector(tree, ".s")].SliderThumbPseudo!;
        Assert.Equal(16f, thumb.Width.Value);
        Assert.Equal(24f, thumb.Height.Value);
        Assert.Equal(new RgbaColor(255, 255, 255, 255), thumb.BackgroundColor);

        // Chromium's UA sheet makes the thumb a border box, so a 16px width is the whole knob.
        Assert.Equal(BoxSizing.BorderBox, thumb.BoxSizing);

        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        int? leftmost = null;
        int? rightmost = null;
        for (uint y = 0; y < 40; y++)
        {
            for (uint x = 0; x < 220; x++)
            {
                if (pixmap.Pixel(x, y) is { } pixel
                    && Math.Abs(pixel.R - 0) + Math.Abs(pixel.G - 120) + Math.Abs(pixel.B - 212) < 60)
                {
                    leftmost = leftmost is { } left ? Math.Min(left, (int)x) : (int)x;
                    rightmost = rightmost is { } right ? Math.Max(right, (int)x) : (int)x;
                }
            }
        }

        Assert.NotNull(leftmost);

        // Within one pixel of Chromium, which places the thumb by
        // trackLeft + fraction * (trackWidth - thumbWidth).
        Assert.InRange(leftmost!.Value, expectedLeft - 1, expectedLeft + 1);
        Assert.InRange(rightmost!.Value - leftmost.Value + 1, 15, 17);
    }

    /// <summary>The thumb is the one part of a slider that is ink when the control has none.</summary>
    [Fact]
    public void AZeroHeightSliderStillPaintsItsThumb()
    {
        // The shape a page gets by drawing its own track: the input is a transparent overlay
        // with no height, inside a clipping ancestor.
        DomTree tree = Parse(
            """
            <html><head><style>
                html,body { margin:0; background:white }
                .box { position:relative; width:200px; height:40px; overflow:hidden }
                .s { -webkit-appearance:none; position:absolute; left:0; top:20px; margin:0;
                     padding:0; border:0; width:200px; height:0; background:transparent }
                .s::-webkit-slider-thumb { -webkit-appearance:none; width:16px; height:24px;
                     border-radius:10px; background-color:rgb(0,120,212); border:0 }
            </style></head><body>
                <div class="box"><input class="s" type="range" min="0" max="100" value="100"></div>
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (220f, 60f), null, resources)!;
        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;

        Assert.True(
            CountNear(pixmap, (0, 120, 212), 184, 8, 200, 32) > 100,
            "the thumb must paint at the right of the track even though the input has no height");
    }

    /// <summary>
    /// A2. A date input is filled with Chromium's read-only field text and is sized from it, so
    /// it neither paints an empty box nor collapses a shrink-to-fit ancestor.
    /// </summary>
    [Fact]
    public void ADateInputIsSizedAndPaintedFromItsFieldText()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                html,body { margin:0; background:white }
                input { display:block; margin:0 }
            </style></head><body>
                <input id="empty" type="date">
                <input id="filled" type="date" value="2024-03-07">
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (400f, 120f), null, resources)!;

        // Chromium 141 reports 125.33px for a date field at the UA font. The port shapes the
        // same text with its own engine and rounds the content width up, so it lands within a
        // pixel or two rather than exactly.
        Rect empty = prepared.Layout.Rects[Selector(tree, "#empty")];
        Assert.InRange(empty.Width, 123f, 128f);

        // Chromium's UA sheet puts the date/time controls in monospace.
        Assert.Equal("monospace", prepared.Layout.Styles[Selector(tree, "#empty")].FontFamily);

        Assert.Equal("mm/dd/yyyy", PaintNativeControls.FieldText("date", null));
        Assert.Equal("03/07/2024", PaintNativeControls.FieldText("date", "2024-03-07"));

        // A value that is not a valid date falls back to the empty-field text, as it does in
        // Chromium, rather than painting the raw attribute.
        Assert.Equal("mm/dd/yyyy", PaintNativeControls.FieldText("date", "not-a-date"));
        Assert.Equal("March 2024", PaintNativeControls.FieldText("month", "2024-03"));
        Assert.Equal("Week 07, 2024", PaintNativeControls.FieldText("week", "2024-W07"));
        Assert.Equal("01:30 PM", PaintNativeControls.FieldText("time", "13:30"));
        Assert.Equal(
            "03/07/2024, 01:30 PM",
            PaintNativeControls.FieldText("datetime-local", "2024-03-07T13:30"));

        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        Assert.True(
            CountNonWhite(pixmap, 0, 0, (int)empty.Width, (int)empty.Height) > 40,
            "the empty date field must paint its mm/dd/yyyy placeholder and picker indicator");
    }

    /// <summary>
    /// The layout half of A2: a percentage-width date control cannot resolve its width while
    /// intrinsic sizes are computed, so without an intrinsic minimum it contributes nothing and
    /// its shrink-to-fit ancestor collapses. Chromium measures 324px for the two-field picker
    /// this reduces.
    /// </summary>
    [Fact]
    public void APercentageWidthDateInputKeepsAShrinkToFitAncestorOpen()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                html,body { margin:0; background:white }
                .picker { display:inline-flex; align-items:center; gap:4px }
                .wrap { position:relative }
                input { width:100%; margin:0; padding:0; border:0; height:36px }
            </style></head><body>
                <div class="picker">
                    <div class="wrap"><input type="date"></div>
                    <span>to</span>
                    <div class="wrap"><input type="date"></div>
                </div>
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (800f, 120f), null, resources)!;

        Rect picker = prepared.Layout.Rects[Selector(tree, ".picker")];
        Rect field = prepared.Layout.Rects[Selector(tree, "input")];
        Assert.InRange(field.Width, 118f, 128f);
        Assert.True(
            picker.Width > (2f * field.Width),
            $"the picker must wrap both date fields, not collapse (was {picker.Width})");
    }

    /// <summary>
    /// The related defect: an unstyled checkbox and radio painted nothing at all, because the
    /// port had no native control painter. Chromium draws a 13x13 box with a grey border, and
    /// fills it with the accent colour when checked.
    /// </summary>
    [Fact]
    public void UnstyledCheckboxAndRadioPaintTheirNativeBox()
    {
        DomTree tree = Parse(
            """
            <html><head><style>html,body { margin:0; background:white }</style></head><body>
                <input id="off" type="checkbox"><input id="on" type="checkbox" checked>
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (120f, 60f), null, resources)!;
        Rect off = prepared.Layout.Rects[Selector(tree, "#off")];
        Rect on = prepared.Layout.Rects[Selector(tree, "#on")];
        Assert.Equal(13f, off.Width);
        Assert.Equal(13f, off.Height);

        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        Assert.True(
            CountNear(pixmap, (118, 118, 118), (int)off.X, (int)off.Y,
                (int)(off.X + off.Width), (int)(off.Y + off.Height)) > 8,
            "an unchecked checkbox paints a grey native border");
        Assert.True(
            CountNear(pixmap, (0, 117, 255), (int)on.X, (int)on.Y,
                (int)(on.X + on.Width), (int)(on.Y + on.Height)) > 40,
            "a checked checkbox paints the accent fill");
    }

    /// <summary>A script-assigned <c>checked</c> is dirty state too, and reaches paint the same way.</summary>
    [Fact]
    public void ScriptAssignedCheckedStatePaintsTheCheckedBox()
    {
        DomTree tree = Parse(
            """
            <html><head><style>html,body { margin:0; background:white }</style></head><body>
                <input id="box" type="checkbox">
            </body></html>
            """);
        NodeId box = Selector(tree, "#box");
        tree.SetDirtyFormChecked(box, true);
        Assert.Null(tree.GetNode(box)!.GetAttribute("checked"));

        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (120f, 60f), null, resources)!;
        Rect rect = prepared.Layout.Rects[box];
        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        Assert.True(
            CountNear(pixmap, (0, 117, 255), (int)rect.X, (int)rect.Y,
                (int)(rect.X + rect.Width), (int)(rect.Y + rect.Height)) > 40,
            "the accent fill must follow the dirty checked state");
    }

    /// <summary>
    /// Port of paint.rs <c>native_input_text_respects_its_box_and_ancestor_clip</c> (upstream
    /// 1fff1f7): Chrome clips single-line text to the content width but lets ink into the
    /// vertical padding, a textarea clips to its padding box, the ancestor overflow clip still
    /// applies, and a short control keeps its centred glyph ink.
    /// </summary>
    [Theory]
    [InlineData("<input id=control value=abcdefghijabcdefghij>")]
    [InlineData("<input id=control placeholder=abcdefghijabcdefghij>")]
    [InlineData("<textarea id=control placeholder=abcdefghijabcdefghij wrap=off></textarea>")]
    public void NativeInputTextRespectsItsBoxAndAncestorClip(string control)
    {
        foreach (float height in new[] { 8f, 40f })
        {
            foreach (bool clipped in new[] { false, true })
            {
                string overflow = clipped ? "hidden" : "visible";
                DomTree tree = Parse($$"""
                    <style>
                    body{margin:0;background:white}
                    #parent{position:absolute;left:20px;top:20px;width:60px;height:40px;overflow:{{overflow}}}
                    #control{position:absolute;left:10px;top:6px;box-sizing:content-box;
                    width:80px;height:{{height}}px;padding:8px;border:3px solid red;
                    font:32px Arial;line-height:{{height}}px;color:blue;background:white;
                    white-space:pre;overflow:visible;resize:none}
                    #control::placeholder{color:blue;opacity:1}
                    </style><div id=parent>{{control}}</div>
                    """);
                RenderResourceCache resources = new();
                PreparedRender prepared = RenderPaint.PrepareDom(tree, (220f, 140f), null, resources)!;
                Rect rect = prepared.DocumentRect(tree.GetElementById("control")!.Value)!.Value;
                bool textarea = control.StartsWith("<textarea", StringComparison.Ordinal);
                Rect allowed = new(
                    rect.X + (textarea ? 3f : 11f),
                    rect.Y + 3f,
                    rect.Width - (textarea ? 6f : 22f),
                    rect.Height - 6f);
                if (clipped)
                {
                    Rect parent = prepared.DocumentRect(tree.GetElementById("parent")!.Value)!.Value;
                    allowed = allowed.Intersect(parent)!.Value;
                }

                ResolvedScrollState scroll =
                    prepared.ResolveScrollState(tree, (0f, 0f), new Dictionary<NodeId, (float, float)>());
                foreach (float scale in new[] { 1f, 2f })
                {
                    using Pixmap pixmap = RenderPaint.PaintPreparedRegionWithScroll(
                        tree, prepared, resources, scroll, CaptureRegion.New(0f, 0f, 220f, 140f, scale)).Pixmap!;
                    int count = 0;
                    uint minY = uint.MaxValue;
                    uint maxY = 0;
                    for (uint y = 0; y < pixmap.Height; y++)
                    {
                        for (uint x = 0; x < pixmap.Width; x++)
                        {
                            PremultipliedColor p = pixmap.Pixel(x, y)!.Value;
                            if (p.Blue > 200 && p.Red < 50 && p.Green < 50)
                            {
                                float cx = (x + 0.5f) / scale;
                                float cy = (y + 0.5f) / scale;
                                Assert.True(
                                    cx >= allowed.X && cx < allowed.X + allowed.Width
                                        && cy >= allowed.Y && cy < allowed.Y + allowed.Height,
                                    $"text outside clip: height={height}, clipped={clipped}, scale={scale}, ({cx},{cy}), {allowed}");
                                count++;
                                minY = Math.Min(minY, y);
                                maxY = Math.Max(maxY, y);
                            }
                        }
                    }

                    Assert.True(count > 30, $"clipping must leave visible text: height={height}, clipped={clipped}, scale={scale}");
                    if (!clipped && height == 8f)
                    {
                        Assert.True(
                            (maxY - minY + 1) / scale >= 20f,
                            $"short controls must retain the centered glyph ink visible in Chrome: scale={scale}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Port of paint.rs <c>native_button_and_select_labels_keep_inline_text_width</c> (merge
    /// 8a4d745): a button or select sized from its label is as wide as the same text in a span.
    /// </summary>
    [Theory]
    [InlineData("Arial", 400, "abcdefghij")]
    [InlineData("Arial", 700, "Create new form")]
    [InlineData("Arial", 400, "AVATAR Toffee")]
    [InlineData("Times New Roman", 700, "abcdefghij")]
    [InlineData("Courier New", 700, "abcdefghij")]
    [InlineData("MissingFont, Arial", 400, "caf\u00e9 \u03a9mega")]
    public void NativeButtonAndSelectLabelsKeepInlineTextWidth(string family, int weight, string text)
    {
        DomTree tree = Parse($$"""
            <style>
            body{margin:0} .q{display:inline-block;padding:0;border:0;
            font:14px {{family}};font-weight:{{weight}};white-space:nowrap;appearance:none}
            </style><span class=q id=reference>{{text}}</span><button class=q id=button>{{text}}</button>
            <select class=q id=select><option>{{text}}</option></select>
            """);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (800f, 120f), null, new RenderResourceCache())!;
        float Width(string id) => prepared.DocumentRect(tree.GetElementById(id)!.Value)!.Value.Width;
        foreach (string id in new[] { "button", "select" })
        {
            Assert.True(
                MathF.Abs(Width(id) - Width("reference")) <= 1f,
                $"{family}, weight={weight}, {text}: {id}={Width(id)}, span={Width("reference")}");
        }
    }

    /// <summary>
    /// Port of paint.rs <c>native_control_glyph_size_matches_shaped_inline_text</c> (merge
    /// 8a4d745): native control text paints at the same glyph size as shaped inline text, bold
    /// included.
    /// </summary>
    [Theory]
    [InlineData("Arial", 400)]
    [InlineData("Times New Roman", 700)]
    [InlineData("Courier New", 700)]
    [InlineData("system-ui", 700)]
    public void NativeControlGlyphSizeMatchesShapedInlineText(string family, int weight)
    {
        foreach (string control in new[]
        {
            "<input value=abcdefghij>",
            "<select><option>abcdefghij</option></select>",
            "<button>abcdefghij</button>",
        })
        {
            DomTree tree = Parse($$"""
                <style>
                body{margin:0;background:white} span,input,select,button{display:block;
                width:380px;height:50px;padding:0;border:0;font:32px {{family}};
                font-weight:{{weight}};line-height:40px;background:transparent;white-space:nowrap}
                span{color:red}input,select,button{color:blue}
                </style><span>abcdefghij</span>{{control}}
                """);
            using Pixmap pixmap = RenderPaint.PaintDom(tree, (420f, 120f), null)!;
            (int W, int H) Bounds(bool blue)
            {
                (uint X0, uint Y0, uint X1, uint Y1) bounds = (uint.MaxValue, uint.MaxValue, 0, 0);
                for (uint y = 0; y < pixmap.Height; y++)
                {
                    // The select disclosure arrow is outside this text region.
                    for (uint x = 0; x < 320; x++)
                    {
                        PremultipliedColor p = pixmap.Pixel(x, y)!.Value;
                        bool hit = blue
                            ? p.Blue > 200 && p.Red < 50 && p.Green < 50
                            : p.Red > 200 && p.Blue < 50 && p.Green < 50;
                        if (hit)
                        {
                            bounds = (Math.Min(bounds.X0, x), Math.Min(bounds.Y0, y), Math.Max(bounds.X1, x), Math.Max(bounds.Y1, y));
                        }
                    }
                }

                Assert.True(bounds.X0 != uint.MaxValue, $"missing glyphs: {family}, {control}");
                return ((int)(bounds.X1 - bounds.X0 + 1), (int)(bounds.Y1 - bounds.Y0 + 1));
            }

            (int W, int H) reference = Bounds(false);
            (int W, int H) native = Bounds(true);
            Assert.True(
                Math.Abs(reference.W - native.W) <= 2 && Math.Abs(reference.H - native.H) <= 2,
                $"{family}, weight={weight}, {control}: inline={reference}, native={native}");
        }
    }
}
