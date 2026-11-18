using PocketCalculator.Render.Css;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// The emulated media of one page (CDP <c>Emulation.setEmulatedMedia</c>), shared by every
/// document in it: the page's own and its frames' (upstream c9978c3 / c7ec60a, whose
/// <c>media_preferences</c> is an <c>Rc&lt;Cell&lt;(bool, bool)&gt;&gt;</c>).
/// </summary>
/// <remarks>
/// <see cref="MediaType"/> is the port's addition: upstream ignores CDP's <c>media</c>
/// parameter, which Chromium applies to CSS and <c>matchMedia()</c> alike.
/// </remarks>
public sealed class MediaPreferences
{
    /// <summary>The emulated <c>prefers-reduced-motion: reduce</c>.</summary>
    public bool ReducedMotion { get; set; }

    /// <summary>The emulated <c>prefers-color-scheme: dark</c>.</summary>
    public bool DarkColorScheme { get; set; }

    /// <summary>The emulated media type, lowercased; empty for none (screen).</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>The medium <see cref="MediaType"/> selects for CSS.</summary>
    public CssMediaType Medium => MediaType switch
    {
        "" or "screen" => CssMediaType.Screen,
        "print" => CssMediaType.Print,
        _ => CssMediaType.OtherMedium,
    };

    /// <summary><paramref name="media"/>'s medium with these preferences.</summary>
    public CssMediaType Apply(CssMediaType media) => media.WithPreferences(ReducedMotion, DarkColorScheme);
}
