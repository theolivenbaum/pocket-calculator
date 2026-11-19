using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// <c>crates/obscura-js/tests/response_streams.rs</c> (upstream a1dd711): a Response over a
/// supplied ReadableStream reads it on demand. The expected values were measured in Chromium
/// (playwright chromium, the same scripts in about:blank); two differ from upstream's, and
/// say so where they do.
/// </summary>
public sealed class ResponseStreamsTests
{
    private static async Task<JsonNode?> EvaluateAsync(string source)
    {
        using var fixture = RuntimeFixture.Setup("<!doctype html><body></body>");
        var result = await fixture.Runtime.EvaluateForCdpAsync(source, returnByValue: true, awaitPromise: true);
        return result.Value;
    }

    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.Equal(JsonNode.Parse(expected)?.ToJsonString() ?? "null", actual?.ToJsonString() ?? "null");

    /// <remarks>
    /// DEVIATION from upstream, whose <c>sourceError</c> is true: Chromium rejects a body read
    /// from an errored stream with TypeError "Failed to fetch", not the stream's own error.
    /// </remarks>
    [Fact]
    public async Task ResponseStreamsPreserveBytesErrorsAndCloneConsumption()
    {
        var value = await EvaluateAsync(
            """
            (async () => {
                const original = new Response('{"ok":true}');
                const json = await new Response(original.body).json();
                const binary = new Response(new ReadableStream({start(c) { c.enqueue(new Uint8Array([0,255,128])); c.close(); }}));
                const copy = binary.clone();
                const unused = [binary.bodyUsed, copy.bodyUsed];
                const a = [...new Uint8Array(await binary.arrayBuffer())];
                const b = [...new Uint8Array(await copy.arrayBuffer())];
                let badChunk = false, sourceError = false, sourceErrorType = false, consumed = false;
                try { await new Response(new ReadableStream({start(c) {c.enqueue('not bytes');c.close();}})).text(); } catch(e) {badChunk=e instanceof TypeError;}
                try { await new Response(new ReadableStream({start(c) {c.error(new Error('source failure'));}})).text(); }
                catch(e) {sourceError=e.message==='source failure'; sourceErrorType=e instanceof TypeError && e.message==='Failed to fetch';}
                try { binary.clone(); } catch(e) { consumed=e instanceof TypeError; }
                return {json,a,b,unused,badChunk,sourceError,sourceErrorType,consumed};
            })()
            """);
        AssertJson(
            """{"json":{"ok":true},"a":[0,255,128],"b":[0,255,128],"unused":[false,false],"badChunk":true,"sourceError":false,"sourceErrorType":true,"consumed":true}""",
            value);
    }

    [Fact]
    public async Task ResponseStreamsKeepDemandAndCancellationWithTheSource()
    {
        var value = await EvaluateAsync(
            """
            (async () => {
                let pulls=0, cancelled=null;
                const source=new ReadableStream({pull(c){pulls++;c.enqueue(new Uint8Array([pulls]));},cancel(reason){cancelled=reason;}});
                const response=new Response(source);
                await new Promise(resolve=>setTimeout(resolve,0));
                const lazy=pulls<=1, same=response.body===source, unused=!response.bodyUsed;
                await response.body.cancel('stop');
                const locked=new ReadableStream(),reader=locked.getReader();let rejected=false;
                try{new Response(locked);}catch(e){rejected=e instanceof TypeError;}reader.releaseLock();
                let count=0;
                const bytes=await new Response(new ReadableStream({pull(c){count++;if(count<=3)c.enqueue(new Uint8Array([count]));else c.close();}})).arrayBuffer();
                return {lazy,same,unused,cancelled,used:response.bodyUsed,rejected,bytes:[...new Uint8Array(bytes)]};
            })()
            """);
        AssertJson(
            """{"lazy":true,"same":true,"unused":true,"cancelled":"stop","used":true,"rejected":true,"bytes":[1,2,3]}""",
            value);
    }

    [Fact]
    public async Task ResponseStreamsRejectLockedBytesAndForwardTeeFailures()
    {
        var value = await EvaluateAsync(
            """
            (async () => {
                const response=new Response('locked bytes'), reader=response.body.getReader();
                let cloneRejected=false,textRejected=false;
                const lockedUsed=response.bodyUsed;
                try{response.clone();}catch(e){cloneRejected=e instanceof TypeError;}
                try{await response.text();}catch(e){textRejected=e instanceof TypeError;}
                reader.releaseLock();const text=await response.text();
                let controller;const branches=new ReadableStream({start(c){controller=c;c.enqueue(new Uint8Array([1]));}}).tee();
                await new Promise(resolve=>setTimeout(resolve,0));
                const branch=branches[0].getReader();
                const closed=branch.closed.then(()=>false,e=>e.message==='upstream error');
                controller.error(new Error('upstream error'));const sourceClosed=await closed;let readError=false;
                try{await branch.read();}catch(e){readError=e.message==='upstream error';}
                const cancellations=new ReadableStream({cancel(){return Promise.reject(new Error('cancel failed'));}}).tee();
                const settled=await Promise.allSettled(cancellations.map(b=>b.cancel()));
                return {lockedUsed,cloneRejected,textRejected,text,closed:sourceClosed,readError,cancelRejected:settled.every(s=>s.status==='rejected'&&s.reason.message==='cancel failed')};
            })()
            """);
        AssertJson(
            """{"lockedUsed":false,"cloneRejected":true,"textRejected":true,"text":"locked bytes","closed":true,"readError":true,"cancelRejected":true}""",
            value);
    }

    /// <remarks>
    /// DEVIATION from upstream, which expects <c>[9]</c>: Fetch clones a body by teeing it
    /// with cloneForBranch2, so the clone's chunk is a copy and Chromium answers <c>[1]</c>.
    /// A plain <c>tee()</c> still shares chunks, as in Chromium.
    /// </remarks>
    [Fact]
    public async Task ResponseStreamClonePreservesDefaultStreamChunkIdentity()
    {
        var value = await EvaluateAsync(
            """
            (async () => {
                const response = new Response(new ReadableStream({start(c) {c.enqueue(new Uint8Array([1])); c.close();}}));
                const copy = response.clone();
                const first = await response.body.getReader().read();
                first.value[0] = 9;
                const cloned = [...new Uint8Array(await copy.arrayBuffer())];
                const [a, b] = new ReadableStream({start(c){c.enqueue(new Uint8Array([1]));c.close();}}).tee();
                const x = await a.getReader().read(); x.value[0] = 9;
                return [cloned, [...(await b.getReader().read()).value]];
            })()
            """);
        AssertJson("[[1], [9]]", value);
    }

    /// <summary>
    /// Port addition, measured in Chromium: locking a body does not use it, reading does; a
    /// stream's desiredSize goes negative past its high-water mark; a zero high-water mark
    /// pulls nothing until a read; a used body cannot be cloned; and cancelling both clones
    /// of a stream body cancels the source once, with both reasons.
    /// </summary>
    [Fact]
    public async Task ResponseStreamsMatchChromiumOnLockingDemandAndCancellation()
    {
        var value = await EvaluateAsync(
            """
            (async () => {
                const out = {};
                let pulls = 0;
                new ReadableStream({pull(c){pulls++; c.enqueue(new Uint8Array([pulls])); if (pulls >= 3) c.close();}}, {highWaterMark: 0});
                await new Promise(r => setTimeout(r, 0));
                out.hwm0Pulls = pulls;
                let cd; new ReadableStream({start(c){cd=c;}}); cd.enqueue(1); cd.enqueue(2); out.desired = cd.desiredSize;
                try { new ReadableStream({}, {highWaterMark: -1}); out.neg = 'none'; } catch (e) { out.neg = e.constructor.name; }
                const r = new Response(new ReadableStream({start(c){c.enqueue(new Uint8Array([65]));c.close();}}));
                const rd = r.body.getReader(); out.lockedUsed = r.bodyUsed; await rd.read(); out.readUsed = r.bodyUsed;
                const bytes = new Response('abc'); const br = bytes.body.getReader(); out.bytesLockedUsed = bytes.bodyUsed;
                await br.read(); out.bytesReadUsed = bytes.bodyUsed;
                const r2 = new Response('x'); await r2.text();
                try { r2.clone(); out.cloneUsed = 'none'; } catch (e) { out.cloneUsed = e.constructor.name + ': ' + e.message; }
                let cancelled = null;
                const r3 = new Response(new ReadableStream({cancel(reason){ cancelled = reason; }}));
                const c3 = r3.clone();
                const p1 = r3.body.cancel('one'); await new Promise(r => setTimeout(r, 10)); out.afterOne = cancelled;
                await c3.body.cancel('two'); out.afterTwo = cancelled; await p1;
                return out;
            })()
            """);
        AssertJson(
            """
            {"hwm0Pulls":0,"desired":-1,"neg":"RangeError","lockedUsed":false,"readUsed":true,
             "bytesLockedUsed":false,"bytesReadUsed":true,
             "cloneUsed":"TypeError: Failed to execute 'clone' on 'Response': Response body is already used",
             "afterOne":null,"afterTwo":["one","two"]}
            """,
            value);
    }
}
