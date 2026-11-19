using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// <c>:scope</c> is the element a query or <c>matches()</c> runs on, as crates/obscura-dom
/// (selector.rs) binds it. Chromium 1243 answers ["x",2,1,true,"body",null] for the same
/// queries through element.querySelector / querySelectorAll / matches.
/// </summary>
public sealed class ScopeSelectorTests
{
    [Fact]
    public void Scope_binds_to_the_element_the_query_runs_on()
    {
        var tree = HtmlParsing.ParseHtml("<section id=r><b class=h id=x></b><i><b class=h id=y></b></i></section>");
        var r = tree.QuerySelector("#r")!.Value;
        var x = tree.QuerySelector("#x")!.Value;

        Assert.Equal(x, tree.QuerySelectorFrom(r, ":scope > .h"));
        Assert.Equal(2, tree.QuerySelectorAllFrom(r, ":scope .h").Count);
        Assert.Single(tree.QuerySelectorAllFrom(r, ":scope > i > .h"));
        Assert.True(tree.MatchesSelector(r, ":scope"));
        // From the document, :scope is the document element.
        Assert.NotNull(tree.QuerySelector(":scope > body"));
        // A query never returns its own root.
        Assert.Null(tree.QuerySelectorFrom(r, ":scope"));
    }
}
