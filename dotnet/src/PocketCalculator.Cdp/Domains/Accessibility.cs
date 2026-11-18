using System.Text.Json.Nodes;
using PocketCalculator.Browser;
using PocketCalculator.Dom;

namespace PocketCalculator.Cdp.Domains;

/// <summary>
/// CDP <c>Accessibility</c> domain: the synthesized accessibility tree. The tree itself is
/// <see cref="AccessibilityTree"/> in PocketCalculator.Browser, shared with the MCP snapshot
/// tools since upstream 3c56f59.
/// </summary>
public static class Accessibility
{
    public static async Task<DomainResult> HandleAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ = parameters;
        await Task.CompletedTask.ConfigureAwait(false);
        switch (method)
        {
            case "enable":
                return DomainResult.Empty();

            case "getFullAXTree":
            {
                if (ctx.GetSessionPage(sessionId) is not { } page)
                {
                    return DomainResult.Err("No page");
                }

                var array = new JsonArray();
                foreach (JsonObject node in AccessibilityTree.BuildAxNodes(page))
                {
                    array.Add(node);
                }

                return DomainResult.Ok(new JsonObject { ["nodes"] = array });
            }

            default:
                return DomainResult.Empty();
        }
    }

    /// <summary>The AX tree of a bare DOM, with no CSS visibility.</summary>
    public static List<JsonObject> BuildAxNodes(DomTree dom) => AccessibilityTree.BuildAxNodes(dom);
}
