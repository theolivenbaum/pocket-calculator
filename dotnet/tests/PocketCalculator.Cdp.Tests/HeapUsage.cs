using System.Text.Json.Nodes;
using PocketCalculator.Cdp;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>Ports of upstream 8bb46be's tests: Runtime.getHeapUsage and HeapProfiler.collectGarbage.</summary>
[Collection(CdpDomainCollection.Name)]
public sealed class HeapUsage
{
    private static async Task<JsonNode> CallAsync(CdpContext ctx, string method, JsonObject? parameters = null)
    {
        CdpResponse response = await Dispatcher.DispatchAsync(
            new CdpRequest { Id = 1, Method = method, Params = parameters ?? new JsonObject(), SessionId = "heap" }, ctx);
        Assert.True(response.Error is null, $"{method}: {response.Error?.Message}");
        return response.Result!;
    }

    [Fact]
    public async Task Heap_usage_reports_live_v8_allocations_without_running_page_tasks()
    {
        var ctx = CdpContext.New();
        ctx.Sessions["heap"] = ctx.CreatePage();
        JsonNode before = await CallAsync(ctx, "Runtime.getHeapUsage");
        await CallAsync(ctx, "Runtime.evaluate", new JsonObject
        {
            ["expression"] = "globalThis.kept = Array.from({length: 200000}, (_, i) => ({i})); setTimeout(() => globalThis.ran = true, 0); 0",
        });
        JsonNode after = await CallAsync(ctx, "Runtime.getHeapUsage");
        Assert.True(after["usedSize"]!.GetValue<ulong>() > before["usedSize"]!.GetValue<ulong>() + (2 * 1024 * 1024));
        Assert.True(after["totalSize"]!.GetValue<ulong>() >= after["usedSize"]!.GetValue<ulong>());
        Assert.Equal(0, after["embedderHeapUsedSize"]!.GetValue<int>());
        // Reading the heap ran no page task: the timer has not fired.
        JsonNode ran = await CallAsync(ctx, "Runtime.evaluate", new JsonObject { ["expression"] = "globalThis.ran === true", ["returnByValue"] = true });
        Assert.False(ran["result"]!["value"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Heap_profiler_collect_garbage_reclaims_unreferenced_objects()
    {
        var ctx = CdpContext.New();
        ctx.Sessions["heap"] = ctx.CreatePage();
        await CallAsync(ctx, "Runtime.evaluate", new JsonObject
        {
            ["expression"] = "globalThis.garbage = Array.from({length: 200000}, (_, i) => ({i})); 0",
        });
        ulong before = (await CallAsync(ctx, "Runtime.getHeapUsage"))["usedSize"]!.GetValue<ulong>();
        await CallAsync(ctx, "Runtime.evaluate", new JsonObject { ["expression"] = "globalThis.garbage = null" });
        await CallAsync(ctx, "HeapProfiler.collectGarbage");
        ulong after = (await CallAsync(ctx, "Runtime.getHeapUsage"))["usedSize"]!.GetValue<ulong>();
        Assert.True(after + (2 * 1024 * 1024) < before, $"explicit GC must reclaim discarded objects: {before} -> {after}");
    }
}
