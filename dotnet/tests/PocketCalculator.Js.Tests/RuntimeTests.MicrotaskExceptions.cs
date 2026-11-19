using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Upstream aea9bca (runtime.rs microtask_exception_reports_error_without_discarding_queued_work):
/// a queueMicrotask callback that throws is reported as an ErrorEvent at the window and later
/// work still runs. Expected order measured in Chromium 157.0.8103.78 (Playwright) for the
/// same script: ["first","error:boom:true","second","promise","timer"].
/// </summary>
public sealed partial class RuntimeTests
{
    [Fact]
    public async Task MicrotaskExceptionReportsErrorWithoutDiscardingQueuedWork()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "microtask-throw",
            """
            window.L = [];
            addEventListener('error', e => L.push('error:' + (e.error && e.error.message) + ':' + (e instanceof ErrorEvent)));
            addEventListener('unhandledrejection', () => L.push('unhandled'));
            queueMicrotask(() => { L.push('first'); throw new Error('boom'); });
            queueMicrotask(() => L.push('second'));
            Promise.resolve().then(() => L.push('promise'));
            setTimeout(() => L.push('timer'), 0);
            """);
        await RunEventLoopUntilAsync(rt, "L.includes('timer')");
        Assert.Equal(
            """["first","error:boom:true","second","promise","timer"]""",
            rt.Evaluate("JSON.stringify(L)")!.GetValue<string>());
        Assert.Equal("TypeError", rt.Evaluate("(() => { try { queueMicrotask(1); return 'ok'; } catch (e) { return e.name; } })()")!.GetValue<string>());
    }
}
