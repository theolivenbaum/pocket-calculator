using Xunit;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// <c>measure_text</c> / <c>text_width</c> scale by the em square (upstream 9a3d12e).
/// </summary>
/// <remarks>
/// The reference used to scale by the font's hhea height (ab_glyph's <c>PxScale</c>), which
/// made every advance ~11% short of Chromium's for Liberation Sans (2048/2288), and the port
/// reproduced that as a deviation. Upstream now measures em-based, matching Chromium and the
/// shaped inline path, and so does the port.
/// </remarks>
public sealed class DomTextMeasureScaleTests
{
    /// <summary>Port of <c>measure_text_matches_browser_advance_widths</c>: Chromium 147,
    /// Liberation Sans (its Arial substitute) at 14px.</summary>
    [Theory]
    [InlineData("abcdefghij", 63.8f)]
    [InlineData("ab", 15.6f)]
    [InlineData("Create new form", 103.0f)]
    public void MeasureTextMatchesBrowserAdvanceWidths(string text, float chromium)
    {
        float width = DomTextMeasure.MeasureText(text, 14f, isBold: false, "Arial");
        Assert.True(MathF.Abs(width - chromium) < 1f, $"MeasureText({text}) = {width}, Chromium gives {chromium}");
    }

    /// <summary>
    /// Port of paint.rs <c>native_text_metrics_match_css_em_sizes_and_font_weights</c> (merge
    /// 8a4d745): bold measures the real bold face, no longer the regular one plus a synthetic
    /// pixel per glyph, and <c>kern</c> pairs apply ("AVATAR Toffee"). Widths re-measured on
    /// Chromium 1243 (2026-11-18) at 14px with the bundled faces loaded as web fonts.
    /// </summary>
    [Theory]
    [InlineData("Arial", false, "abcdefghij", 63.828125f)]
    [InlineData("Arial", false, "Create new form", 103.484375f)]
    [InlineData("Arial", false, "AVATAR Toffee", 93.390625f)]
    [InlineData("Arial", false, "café Ωmega", 75.84375f)]
    [InlineData("Arial", true, "abcdefghij", 70.015625f)]
    [InlineData("Arial", true, "Create new form", 109.703125f)]
    [InlineData("Arial", true, "AVATAR Toffee", 99.03125f)]
    [InlineData("Arial", true, "Launch simulated demo", 160.25f)]
    [InlineData("Times New Roman", false, "abcdefghij", 59.09375f)]
    [InlineData("Times New Roman", true, "abcdefghij", 63.0f)]
    [InlineData("Courier New", false, "abcdefghij", 84.015625f)]
    [InlineData("Courier New", true, "abcdefghij", 84.015625f)]
    [InlineData("system-ui", false, "abcdefghij", 73.140625f)]
    [InlineData("system-ui", true, "abcdefghij", 82.96875f)]
    [InlineData("MissingFont, Arial", false, "abcdefghij", 63.828125f)]
    public void NativeTextMetricsMatchCssEmSizesAndFontWeights(
        string family, bool bold, string text, float chromium)
    {
        float width = DomTextMeasure.MeasureText(text, 14f, bold, family);
        Assert.True(
            MathF.Abs(width - chromium) < 0.5f,
            $"{family}, bold={bold}, {text}: {width}, Chromium gives {chromium}");
    }

    /// <summary>The scale is linear in the CSS size for every bundled fallback face.</summary>
    [Theory]
    [InlineData("sans-serif")]
    [InlineData("serif")]
    [InlineData("monospace")]
    [InlineData("system-ui")]
    public void ScaleIsLinearInTheCssSize(string family)
    {
        float at16 = DomTextMeasure.MeasureText("Launch simulated demo", 16f, isBold: false, family);
        float at32 = DomTextMeasure.MeasureText("Launch simulated demo", 32f, isBold: false, family);
        Assert.Equal(at16 * 2f, at32, 0.05f);
    }

    /// <summary>An empty or degenerate request measures zero rather than throwing.</summary>
    [Theory]
    [InlineData("", 16f)]
    [InlineData("x", 0f)]
    [InlineData("x", -1f)]
    public void DegenerateRequestsMeasureZero(string text, float size)
    {
        Assert.Equal(0f, DomTextMeasure.MeasureText(text, size, isBold: false, "sans-serif"));
    }
}
