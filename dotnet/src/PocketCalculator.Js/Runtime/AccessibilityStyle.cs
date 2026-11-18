namespace PocketCalculator.Js.Runtime;

/// <summary>
/// The two computed values the accessibility tree needs from the cascade: whether the element
/// is <c>display: none</c>, and its own declared <c>visibility</c> (<c>null</c> inherits).
/// Port of <c>obscura_js::runtime::AccessibilityStyle</c>.
/// </summary>
public readonly record struct AccessibilityStyle(bool DisplayNone, bool? VisibilityHidden);
