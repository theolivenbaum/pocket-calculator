// Port of `text_width` in crates/obscura-render/src/dom.rs and the `measure_text` /
// `fallback_font_bytes` pair it calls in paint.rs.
//
// RECONCILIATION NOTE: paint.rs is not ported yet, and this pair is the only part of it the
// render-tree build needs (native control label widths and the static-font word fallback).
// When paint.rs lands, it should call these rather than adding a second copy.
using System.Buffers;
using SkiaSharp;

namespace PocketCalculator.Render;

internal static class DomTextMeasure
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, SKFont> Fonts = new(StringComparer.Ordinal);

    /// <summary>
    /// Text width for layout, from real glyph metrics of the deterministic bundled face.
    /// </summary>
    internal static float TextWidth(
        string text,
        float size,
        bool isBold,
        string? family,
        float letterSpacing,
        float wordSpacing = 0f)
    {
        // Rust counts Unicode scalar values (`str::chars`). C# strings are UTF-16, so a
        // non-BMP character is two `char` values; enumerate runes to keep letter-spacing
        // from being applied twice per astral code point.
        int glyphCount = 0;
        foreach (System.Text.Rune rune in text.EnumerateRunes())
        {
            if (!System.Text.Rune.IsControl(rune))
            {
                glyphCount++;
            }
        }

        return MeasureText(text, size, isBold, family)
            + (glyphCount * letterSpacing)
            + WordSpacingAdvance(text, wordSpacing);
    }

    /// <summary>
    /// Rust <c>word_spacing_advance</c> (upstream bf10c46): the extra advance of every word
    /// separator (U+0020 and U+00A0) in <paramref name="text"/>.
    /// </summary>
    internal static float WordSpacingAdvance(string text, float spacing)
    {
        if (spacing == 0f)
        {
            return 0f;
        }

        int separators = 0;
        foreach (char character in text)
        {
            if (character is ' ' or '\u00a0')
            {
                separators++;
            }
        }

        return separators * spacing;
    }

    /// <summary>
    /// paint.rs <c>measure_text</c> as of upstream 8a4d745: the bundled face for the family
    /// and weight (a real bold face, not the old one-pixel-per-glyph synthetic advance),
    /// em-scaled, with the face's <c>kern</c> table pairs applied.
    /// </summary>
    internal static float MeasureText(string text, float size, bool isBold, string? family)
    {
        if (text.Length == 0 || !float.IsFinite(size) || size <= 0f)
        {
            return 0f;
        }

        string stem = FaceStem(family, isBold);
        float width;
        lock (Gate)
        {
            if (!Advances.TryGetValue((stem, text), out (float Units, float Divisor) advance))
            {
                advance = UnitAdvances(FallbackFontByStem(stem), text);
                if (Advances.Count >= MaxAdvances)
                {
                    Advances.Clear();
                }

                Advances[(stem, text)] = advance;
            }

            width = advance.Divisor > 0f ? advance.Units * size / advance.Divisor : 0f;
        }

        return width;
    }

    /// <summary>Rust <c>inline::bundled_font_bytes</c>, by asset stem.</summary>
    internal static string FaceStem(string? family, bool isBold)
    {
        string stem = FallbackFaceStem(family);
        return isBold ? BoldStem(stem) : stem;
    }

    private static string BoldStem(string stem) => stem switch
    {
        "liberation-sans" => "liberation-sans-bold",
        "liberation-serif" => "liberation-serif-bold",
        "liberation-mono" => "liberation-mono-bold",
        "dejavu-sans" => "dejavu-sans-bold",
        _ => stem,
    };

    /// <summary>
    /// The unscaled advance sum of each word measured, by face: the per-word fallback lays a
    /// text node out as a leaf per word and measured every word with Skia on every pass (a
    /// sixth of a retained box-tree build on nvidia.com). The width is the same function of
    /// the sum, so a hit gives the same float.
    /// </summary>
    private static readonly Dictionary<(string Stem, string Text), (float Units, float Divisor)> Advances = [];

    private const int MaxAdvances = 1 << 16;

    private static readonly Dictionary<string, string> StemsByFamily = new(StringComparer.Ordinal);

    private static (float Units, float Divisor) UnitAdvances(SKFont font, string text)
    {
        char[]? rented = text.Length > MaxStackChars ? ArrayPool<char>.Shared.Rent(text.Length) : null;
        try
        {
            Span<char> buffer = rented is null ? stackalloc char[MaxStackChars] : rented;
            int length = 0;
            foreach (System.Text.Rune rune in text.EnumerateRunes())
            {
                if (System.Text.Rune.IsControl(rune))
                {
                    continue;
                }

                length += rune.EncodeToUtf16(buffer[length..]);
            }

            if (length == 0)
            {
                return (0f, 0f);
            }

            // Measure in font units and scale once, rather than measuring at the
            // height-scaled size: Skia quantizes advances at a fractional size, which
            // left three of seven calibration cases a pixel off the reference, and
            // ab_glyph's own arithmetic is a single scale applied to unit advances.
            // The caller holds Gate: the font is shared.
            int unitsPerEm = font.Typeface.UnitsPerEm;
            font.Size = unitsPerEm;
            ushort[] glyphs = font.GetGlyphs(buffer[..length]);
            if (glyphs.Length == 0)
            {
                return (0f, 0f);
            }

            float[] widths = font.GetGlyphWidths(glyphs);
            float units = 0f;
            foreach (float advance in widths)
            {
                units += advance;
            }

            // Upstream 8a4d745: ab_glyph's `kern` between consecutive glyphs, which reads the
            // face's legacy `kern` table (as Skia's pair adjustments do), in font units.
            if (glyphs.Length > 1)
            {
                foreach (int adjustment in font.Typeface.GetKerningPairAdjustments(glyphs))
                {
                    units += adjustment;
                }
            }

            // Em-based (upstream 9a3d12e): one CSS pixel of font-size is one em, as the shaped
            // inline path and Chromium measure. Rust used to scale by the font's height
            // (ab_glyph's PxScale), which made every advance ~11% short.
            return (units, unitsPerEm > 0 ? unitsPerEm : 1f);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    private const int MaxStackChars = 256;

    private static SKFont FallbackFontByStem(string stem)
    {
        lock (Gate)
        {
            if (Fonts.TryGetValue(stem, out SKFont? cached))
            {
                return cached;
            }

            using SKData data = SKData.CreateCopy(FontAssets.Load(stem));
            SKTypeface typeface = SKTypeface.FromData(data)
                ?? throw new InvalidOperationException($"bundled face '{stem}' failed to load");
            SKFont font = new(typeface, 16f)
            {
                Subpixel = true,
                Hinting = SKFontHinting.None,
                LinearMetrics = true,
            };
            Fonts[stem] = font;
            return font;
        }
    }

    /// <summary>Port of paint.rs <c>fallback_font_bytes</c>, by asset stem.</summary>
    internal static string FallbackFaceStem(string? family)
    {
        if (family is null)
        {
            return "liberation-sans";
        }

        lock (Gate)
        {
            if (StemsByFamily.TryGetValue(family, out string? known))
            {
                return known;
            }
        }

        string stem = FallbackFaceStemUncached(family);
        lock (Gate)
        {
            if (StemsByFamily.Count >= MaxAdvances)
            {
                StemsByFamily.Clear();
            }

            StemsByFamily[family] = stem;
        }

        return stem;
    }

    private static string FallbackFaceStemUncached(string family)
    {

        foreach (string raw in family.Split(','))
        {
            string token = raw.Trim().Trim('"', '\'').ToLowerInvariant();
            if (token is "system-ui" or "ui-sans-serif")
            {
                return "dejavu-sans";
            }

            if (token is "monospace" or "menlo" or "monaco" or "code"
                || token.Contains("mono", StringComparison.Ordinal)
                || token.Contains("courier", StringComparison.Ordinal)
                || token.Contains("consol", StringComparison.Ordinal))
            {
                return "liberation-mono";
            }

            if (token is "serif" or "georgia" or "cambria" or "roman"
                || token.Contains("times", StringComparison.Ordinal)
                || token.Contains("garamond", StringComparison.Ordinal)
                || token.Contains("liberation serif", StringComparison.Ordinal))
            {
                return "liberation-serif";
            }

            if (token is "sans-serif" or "arial" or "helvetica" or "helvetica neue"
                or "-apple-system" or "roboto" or "segoe ui" or "inter" or "verdana" or "tahoma"
                || token.Contains("sans", StringComparison.Ordinal))
            {
                return "liberation-sans";
            }
        }

        return "liberation-sans";
    }
}
