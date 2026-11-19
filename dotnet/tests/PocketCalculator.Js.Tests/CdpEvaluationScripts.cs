using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>Ports of upstream 579e1cb's runtime.rs tests: Runtime.evaluate compiles a script.</summary>
public sealed class CdpEvaluationScripts
{
    [Fact]
    public async Task Cdp_evaluation_does_not_call_the_pages_eval()
    {
        using var fixture = RuntimeFixture.Blank();
        fixture.Runtime.ExecuteScript("eval-trap", "globalThis.eval = () => { throw new Error('page eval called'); };");
        foreach (bool awaitPromise in new[] { false, true })
        {
            var result = await fixture.Runtime.EvaluateForCdpAsync("6 * 7", returnByValue: true, awaitPromise: awaitPromise);
            Assert.False(result.Thrown);
            Assert.Equal(42.0, result.Value!.GetValue<double>());
        }
    }

    [Fact]
    public async Task Cdp_evaluation_preserves_global_lexical_bindings()
    {
        using var fixture = RuntimeFixture.Blank();
        foreach ((string name, bool awaitPromise) in new[] { ("syncLexical", false), ("asyncLexical", true) })
        {
            var first = await fixture.Runtime.EvaluateForCdpAsync($"let {name} = 40; {name}", true, awaitPromise);
            Assert.Equal(40.0, first.Value!.GetValue<double>());
            var second = await fixture.Runtime.EvaluateForCdpAsync($"{name} += 2; {name}", true, awaitPromise);
            Assert.False(second.Thrown);
            Assert.Equal(42.0, second.Value!.GetValue<double>());
        }
    }

    // The completion value keeps its type and identity, and statements and throws behave as
    // a script's do (Chromium: a SyntaxError and a thrown value are exceptions, a promise is
    // awaited, a trailing sourceURL comment is fine).
    [Fact]
    public async Task Cdp_evaluation_results_keep_their_shape()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        Assert.True((await rt.EvaluateForCdpAsync("throw new Error('x')", true, false)).Thrown);
        Assert.True((await rt.EvaluateForCdpAsync("function (", true, false)).Thrown);
        Assert.True((await rt.EvaluateForCdpAsync("Promise.reject(new Error('r'))", true, true)).Thrown);
        Assert.Equal(7.0, (await rt.EvaluateForCdpAsync("Promise.resolve(7)\n//# sourceURL=pptr://x", true, true)).Value!.GetValue<double>());
        Assert.Equal("[1,\"a\",null,true]", (await rt.EvaluateForCdpAsync("var v = 1; [v, 'a', null, true]", true, false)).Value!.ToJsonString());
        Assert.Equal("123", (await rt.EvaluateForCdpAsync("(12n * 10n + 3n).toString()", true, false)).Value!.GetValue<string>());
        Assert.Equal("true", (await rt.EvaluateForCdpAsync("globalThis.kept = {}; 0", true, false)).Thrown ? "false" : "true");
        var handle = await rt.EvaluateForCdpAsync("kept", false, false);
        Assert.NotNull(handle.ObjectId);
        Assert.True((await rt.EvaluateForCdpAsync("class Kept {}; typeof Kept === 'function'", true, false)).Value!.GetValue<bool>());
        Assert.Equal("function", (await rt.EvaluateForCdpAsync("typeof Kept", true, false)).Value!.GetValue<string>());
    }
}
