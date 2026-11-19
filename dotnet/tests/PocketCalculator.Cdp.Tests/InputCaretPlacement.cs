using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// Where typed text lands in a field, as Chromium 141 puts it: after <c>focus()</c> the caret
/// is at the start (Puppeteer's <c>type()</c> writes "Xpre"), and a mouse press focuses the
/// field and a click past its text puts the caret at the end (click, then keys, writes
/// "preX"). The port used to append in both cases, and a press never moved focus.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class InputCaretPlacement
{
    private const string Page = "data:text/html,<html><body style='margin:0'>"
        + "<input id=a value=pre style='display:block;width:200px;height:20px'>"
        + "<input id=b value=pre style='display:block;width:200px;height:20px'></body></html>";

    private static async Task<string> ValueAsync(CdpContext ctx, string session, string id) =>
        (await CoreCdp.EvalAsync(ctx, 80, $"document.getElementById('{id}').value", session))["result"]!["value"]!.GetValue<string>();

    [Fact]
    public async Task TypingAfterFocusInsertsAtTheStart()
    {
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(Page);
        using IDisposable owned = CoreCdp.Owned(ctx);
        await CoreCdp.EvalAsync(ctx, 2, "document.getElementById('a').focus()", session);
        await CoreCdp.CdpAsync(ctx, 3, "Input.insertText", new JsonObject { ["text"] = "X" }, session);
        await CoreCdp.CdpAsync(ctx, 4, "Input.insertText", new JsonObject { ["text"] = "Y" }, session);
        Assert.Equal("XYpre", await ValueAsync(ctx, session, "a"));
    }

    [Fact]
    public async Task AClickFocusesTheFieldWithTheCaretAtTheEnd()
    {
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(Page);
        using IDisposable owned = CoreCdp.Owned(ctx);
        foreach (string type in new[] { "mousePressed", "mouseReleased" })
        {
            await CoreCdp.CdpAsync(ctx, 5, "Input.dispatchMouseEvent", new JsonObject
            {
                ["type"] = type, ["x"] = 150, ["y"] = 30, ["button"] = "left", ["clickCount"] = 1,
            }, session);
        }

        JsonNode active = await CoreCdp.EvalAsync(ctx, 6, "document.activeElement.id", session);
        Assert.Equal("b", active["result"]!["value"]!.GetValue<string>());
        await CoreCdp.CdpAsync(ctx, 7, "Input.insertText", new JsonObject { ["text"] = "X" }, session);
        Assert.Equal("preX", await ValueAsync(ctx, session, "b"));
        Assert.Equal("pre", await ValueAsync(ctx, session, "a"));
    }

    private static async Task KeyAsync(CdpContext ctx, string session, string key)
    {
        await CoreCdp.CdpAsync(ctx, 9, "Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyDown", ["key"] = key, ["code"] = key }, session);
        await CoreCdp.CdpAsync(ctx, 10, "Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyUp", ["key"] = key, ["code"] = key }, session);
    }

    private static Task TypeAsync(CdpContext ctx, string session, string text) =>
        CoreCdp.CdpAsync(ctx, 11, "Input.insertText", new JsonObject { ["text"] = text }, session);

    /// <summary>
    /// Port addition: ArrowLeft, ArrowRight, Home, End and Delete move and edit at the caret as in
    /// Chromium. The reference implements Backspace and Enter alone, so the arrows left typed text
    /// going in at the end.
    /// </summary>
    [Fact]
    public async Task ArrowsHomeEndAndDeleteEditAtTheCaret()
    {
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(Page);
        using IDisposable owned = CoreCdp.Owned(ctx);
        await CoreCdp.EvalAsync(ctx, 2, "document.getElementById('a').focus()", session);

        await KeyAsync(ctx, session, "End");
        await TypeAsync(ctx, session, "X");
        Assert.Equal("preX", await ValueAsync(ctx, session, "a"));

        await KeyAsync(ctx, session, "ArrowLeft");
        await KeyAsync(ctx, session, "ArrowLeft");
        await TypeAsync(ctx, session, "Y");
        Assert.Equal("prYeX", await ValueAsync(ctx, session, "a"));

        await KeyAsync(ctx, session, "Home");
        await TypeAsync(ctx, session, "Z");
        Assert.Equal("ZprYeX", await ValueAsync(ctx, session, "a"));

        await KeyAsync(ctx, session, "Delete");
        Assert.Equal("ZrYeX", await ValueAsync(ctx, session, "a"));

        await KeyAsync(ctx, session, "ArrowRight");
        await KeyAsync(ctx, session, "Delete");
        Assert.Equal("ZreX", await ValueAsync(ctx, session, "a"));

        await KeyAsync(ctx, session, "End");
        await KeyAsync(ctx, session, "Delete");
        Assert.Equal("ZreX", await ValueAsync(ctx, session, "a"));
    }

    private static async Task<Domains.Page.RgbaImage> ScreenshotAsync(CdpContext ctx, string session)
    {
        JsonNode shot = await CoreCdp.CdpAsync(ctx, 12, "Page.captureScreenshot", new JsonObject { ["format"] = "png" }, session);
        byte[] png = Convert.FromBase64String(shot["data"]!.GetValue<string>());
        return Domains.Page.RgbaImage.Decode(png) ?? throw new InvalidOperationException("the screenshot must decode");
    }

    /// <summary>
    /// Port addition: a focused text field paints its caret, a one-pixel bar the height of the
    /// line after the text before it, and stops painting it when it loses focus. The reference
    /// paints no caret, so a field being typed into looked like any other.
    /// </summary>
    [Fact]
    public async Task TheFocusedFieldPaintsItsCaretAndOnlyWhileFocused()
    {
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(Page);
        using IDisposable owned = CoreCdp.Owned(ctx);
        Domains.Page.RgbaImage blurred = await ScreenshotAsync(ctx, session);

        await CoreCdp.EvalAsync(ctx, 2, "document.getElementById('a').focus(); document.getElementById('a').setSelectionRange(3, 3)", session);
        Domains.Page.RgbaImage focused = await ScreenshotAsync(ctx, session);

        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (int y = 0; y < (int)focused.Height; y++)
        {
            for (int x = 0; x < (int)focused.Width; x++)
            {
                int i = ((y * (int)focused.Width) + x) * 4;
                if (focused.Pixels.AsSpan(i, 4).SequenceEqual(blurred.Pixels.AsSpan(i, 4)))
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        Assert.True(maxX >= 0, "focusing the field and placing the caret must change what is painted");
        Assert.True(maxX - minX <= 1, $"the caret is a bar one pixel wide, got columns {minX}..{maxX}");
        Assert.True(maxY - minY >= 8, $"the caret is as tall as the line, got rows {minY}..{maxY}");
        Assert.InRange(minY, 0, 20);
        Assert.InRange(minX, 10, 120);

        await CoreCdp.EvalAsync(ctx, 3, "document.getElementById('a').blur()", session);
        Domains.Page.RgbaImage again = await ScreenshotAsync(ctx, session);
        Assert.True(again.Pixels.AsSpan().SequenceEqual(blurred.Pixels), "a field that lost focus paints no caret");
    }

    /// <summary>
    /// Port addition: an input command answers without rendering a screencast frame and leaves the
    /// frame to the autonomous pump, which runs once the queued input is read. The reference renders
    /// one inline after every input command, which on a slow page held each key a render behind the
    /// one before.
    /// </summary>
    [Fact]
    public async Task InputCommandsLeaveTheirScreencastFrameToThePump()
    {
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(Page);
        using IDisposable owned = CoreCdp.Owned(ctx);
        await CoreCdp.CdpAsync(ctx, 2, "Page.startScreencast", new JsonObject { ["format"] = "jpeg" }, session);
        Assert.Contains(ctx.PendingEvents, e => e.Method == "Page.screencastFrame");
        ctx.PendingEvents.Clear();
        ctx.Screencasts[session].FramesInFlight = 0;
        ctx.Screencasts[session].AutonomousFramePending = false;

        await CoreCdp.EvalAsync(ctx, 3, "document.getElementById('a').focus()", session);
        ctx.PendingEvents.Clear();
        ctx.Screencasts[session].FramesInFlight = 0;
        ctx.Screencasts[session].AutonomousFramePending = false;

        await TypeAsync(ctx, session, "X");
        await KeyAsync(ctx, session, "Backspace");
        Assert.DoesNotContain(ctx.PendingEvents, e => e.Method == "Page.screencastFrame");
        Assert.True(ctx.Screencasts[session].AutonomousFramePending);

        await Domains.Page.PumpScreencastFramesAsync(ctx);
        Assert.Single(ctx.PendingEvents, e => e.Method == "Page.screencastFrame");
    }
}
