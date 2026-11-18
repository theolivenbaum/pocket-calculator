using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// upstream 22eb6b4 + 335f929 (#1231): a text/plain or text/markdown response is a plain-text
/// document. The markup is Chromium 1243's, measured: a color-scheme meta and one pre-wrap
/// pre holding the text, in standards mode, with document.contentType the response's type.
/// </summary>
public sealed class PlainTextDocumentTests
{
    [Theory]
    [InlineData("text/plain; charset=utf-8", "text/plain", "RFC 9110 <URI-reference>: see <https://example.com/>\n2nd line")]
    [InlineData("text/markdown", "text/markdown", "# Title\n\nSee <Component prop={1} /> and 3 < 4.")]
    public async Task PlainTextResponseBuildsAPreDocument(string header, string contentType, string body)
    {
        using TestHttpServer server = TestHttpServer.Start(_ => TestResponse.Text(body) with { ContentType = header });
        using Page page = PageFixtures.NewPage("plain-text");
        await page.NavigateAsync($"{server.Origin}/doc.txt");
        PageFixtures.AssertJson(
            $$"""["{{contentType}}","PRE",1,{{System.Text.Json.JsonSerializer.Serialize(body)}},"CSS1Compat","pre-wrap"]""",
            page.Evaluate("[document.contentType, document.body.firstElementChild.tagName, document.body.childNodes.length,"
                + " document.body.textContent, document.compatMode, document.querySelector('pre').style.whiteSpace]"));
    }
}
