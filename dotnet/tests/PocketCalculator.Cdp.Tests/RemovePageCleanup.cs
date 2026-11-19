using PocketCalculator.Cdp;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

// Port of upstream 875c531's dispatch.rs test: closing a page drops its per-page and
// per-session bookkeeping.
[Collection(CdpDomainCollection.Name)]
public sealed class RemovePageCleanup
{
    [Fact]
    public void Remove_page_drops_navigation_and_binding_entries()
    {
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        ctx.Sessions["s1"] = pageId;
        ctx.NavEventsEmitted.Add(pageId);
        ctx.BindingSessions["only"] = ["s1"];
        ctx.BindingSessions["shared"] = ["s1", "other"];
        ctx.RemovePage(pageId);
        Assert.Empty(ctx.NavEventsEmitted);
        Assert.False(ctx.LifecycleTimestamps.ContainsKey(pageId));
        Assert.False(ctx.BindingSessions.ContainsKey("only"));
        Assert.Equal(["other"], ctx.BindingSessions["shared"]);
    }
}

// Upstream dfc546d (#917): DOM.describeNode and DOM.resolveNode with an objectId that resolves
// to no node are errors, not the document root.
[Collection(CdpDomainCollection.Name)]
public sealed class UnresolvableObjectIds
{
    [Fact]
    public async Task Describe_and_resolve_node_error_on_an_unresolvable_object_id()
    {
        var ctx = CdpContext.New();
        ctx.Sessions["s"] = ctx.CreatePage();
        await Dispatcher.DispatchAsync(new CdpRequest { Id = 1, Method = "Page.navigate", Params = new System.Text.Json.Nodes.JsonObject { ["url"] = "data:text/html,<p>x</p>" }, SessionId = "s" }, ctx);
        var plain = await Dispatcher.DispatchAsync(new CdpRequest
        {
            Id = 2, Method = "Runtime.evaluate", SessionId = "s",
            Params = new System.Text.Json.Nodes.JsonObject { ["expression"] = "({notANode: true})" },
        }, ctx);
        string objectId = plain.Result!["result"]!["objectId"]!.GetValue<string>();
        foreach (string id in new[] { objectId, "{\"injectedScriptId\":1,\"id\":999999}" })
        {
            foreach (string method in new[] { "DOM.describeNode", "DOM.resolveNode" })
            {
                CdpResponse response = await Dispatcher.DispatchAsync(new CdpRequest
                {
                    Id = 3, Method = method, SessionId = "s",
                    Params = new System.Text.Json.Nodes.JsonObject { ["objectId"] = id },
                }, ctx);
                Assert.True(response.Error is not null, $"{method} with {id}: {response.Result?.ToJsonString()}");
            }
        }
    }
}
