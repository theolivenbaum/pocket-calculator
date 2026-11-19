using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Upstream 35b9448: style-only reads do not copy a large custom-property environment for every
/// element. The values were measured in Chromium (playwright chromium).
/// </summary>
public sealed partial class RuntimeTests
{
    // Rust computed_style_sibling_reads_do_not_repeat_the_ancestor_cascade.
    [Fact]
    public void ComputedStyleSiblingReadsDoNotRepeatTheAncestorCascade()
    {
        var variables = string.Concat(Enumerable.Range(0, 2500).Select(i => $"--token-{i}:{i}px;"));
        using var fixture = RuntimeFixture.Setup(
            "<!doctype html><style>body { " + variables + " color:rgb(12,34,56) }"
            + " .row { display:block; width:var(--token-80); height:1px }"
            + " body[data-active=yes] { color:rgb(78,90,12) }</style><body><main><section>"
            + string.Concat(Enumerable.Repeat("<div class=row></div>", 1024))
            + "</section></main></body>");
        var rt = fixture.Runtime;
        // Prime the prepared render.
        rt.Evaluate("document.body.getBoundingClientRect().width");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        AssertJsonEquals("1024", rt.Evaluate(
            """
            (() => {
                let matched = 0;
                for (const row of document.querySelectorAll('.row')) {
                    const style = getComputedStyle(row);
                    matched += style.display === 'block' && style.color === 'rgb(12, 34, 56)';
                }
                return matched; })()
            """));
        var clean = clock.Elapsed;
        clock.Restart();
        AssertJsonEquals("1024", rt.Evaluate(
            """
            (() => {
                document.body.setAttribute('data-active', 'yes');
                let matched = 0;
                for (const row of document.querySelectorAll('.row')) {
                    const style = getComputedStyle(row);
                    matched += style.display === 'block' && style.color === 'rgb(78, 90, 12)';
                }
                return matched; })()
            """));
        var mutated = clock.Elapsed;
        AssertJsonEquals("\"80px\"", rt.Evaluate("getComputedStyle(document.querySelector('.row')).width"));
        AssertJsonEquals("""["80px", true, true]""", rt.Evaluate(
            """
            (() => {
                const style = getComputedStyle(document.querySelector('.row'));
                return [style.getPropertyValue('--token-80'), style.length > 2500,
                    Array.from({ length: style.length }, (_, i) => style.item(i)).includes('--token-2499')]; })()
            """));
        // Upstream's bound. Each read used to serialise all 2,500 variables (about 1.2 s here).
        Assert.True(clean < TimeSpan.FromSeconds(1), $"style-only reads copied the custom properties: {clean}");
        Assert.True(mutated < TimeSpan.FromSeconds(1), $"sibling reads after a mutation: {mutated}");
    }

    // Rust non_geometric_style_cache_observes_mutations_between_sibling_reads, plus a custom
    // property read and its listing in item()/length.
    [Fact]
    public void NonGeometricStyleReadsObserveMutationsBetweenSiblingReads()
    {
        using var fixture = RuntimeFixture.Setup(
            """
            <!doctype html><style>
            body { --tone:blue; color:var(--tone) }
            body[data-active=yes] { --tone:red }
            section { display:flex }
            .row { display:inline }
            </style><body><section><span class=row id=first></span>
            <span class=row id=second></span></section></body>
            """);
        var rt = fixture.Runtime;
        rt.Evaluate("document.body.getBoundingClientRect().width");
        AssertJsonEquals(
            """["rgb(255, 0, 0)", "rgb(0, 0, 255)", "inline", "rgb(0, 128, 0)", "blue", true, true]""",
            rt.Evaluate(
                """
                (() => {
                    const first = document.getElementById('first');
                    const second = document.getElementById('second');
                    document.body.setAttribute('data-active', 'yes');
                    const before = getComputedStyle(second).color;
                    document.body.removeAttribute('data-active');
                    const after = getComputedStyle(first).color;
                    document.querySelector('section').style.display = 'block';
                    const display = getComputedStyle(second).display;
                    document.querySelector('style').sheet.insertRule('.row { color:green }', 0);
                    const s = getComputedStyle(first);
                    return [before, after, display, s.color, s.getPropertyValue('--tone'),
                        s.length > 100,
                        Array.from({ length: s.length }, (_, i) => s.item(i)).includes('--tone')]; })()
                """));
    }
}
