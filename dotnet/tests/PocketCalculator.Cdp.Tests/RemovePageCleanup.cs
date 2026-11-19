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
