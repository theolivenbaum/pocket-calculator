using System.Text.Json.Nodes;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Ports from <c>crates/obscura-js/src/webcrypto_tests.rs</c>. AES-KW (upstream d57489c) is not
/// implemented in the port yet, so the wrapping tests run over AES-GCM, AES-CBC and AES-CTR.
/// </summary>
public sealed class WebcryptoTests
{
    private static async Task<JsonNode?> Probe(string body)
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var info = await fixture.Runtime.CallFunctionOnForCdpAsync(
            "async () => { const s = crypto.subtle;"
            + " const errorName = async (f) => { try { await f(); return 'ok'; } catch (e) { return e.name; } };"
            + " const hex = (b) => Array.from(new Uint8Array(b)).map((x) => x.toString(16).padStart(2, '0')).join('');"
            + body + " }",
            null, [], returnByValue: true, awaitPromise: true);
        Assert.False(info.Thrown, info.Description);
        return info.Value;
    }

    private const string Algorithms = """
        const algorithmFor = (name) => name === 'AES-GCM' ? {name, iv: new Uint8Array(12)}
            : name === 'AES-CBC' ? {name, iv: new Uint8Array(16)}
            : {name, counter: new Uint8Array(16), length: 64};
        """;

    // upstream d853a73: key_wrapping_checks_the_wrapping_keys_algorithm_and_usage
    [Fact]
    public async Task KeyWrappingChecksTheWrappingKeysAlgorithmAndUsage()
    {
        JsonNode? result = await Probe(Algorithms + """
            const bytes = new Uint8Array(16);
            const payload = await s.importKey('raw', bytes, 'AES-GCM', true, ['encrypt']);
            const results = [];
            for (const name of ['AES-GCM', 'AES-CBC', 'AES-CTR']) {
                const alg = algorithmFor(name);
                const unwrapOnly = await s.importKey('raw', bytes, name, false, ['unwrapKey']);
                const wrongAlgorithm = await s.importKey('raw', bytes, {name:'HMAC', hash:'SHA-256'}, false, ['sign']);
                results.push(await errorName(() => s.wrapKey('raw', payload, unwrapOnly, alg)));
                results.push(await errorName(() => s.wrapKey('raw', payload, wrongAlgorithm, alg)));
                wrongAlgorithm.algorithm.name = name;
                results.push(await errorName(() => s.wrapKey('raw', payload, wrongAlgorithm, alg)));
                const wrapOnly = await s.importKey('raw', bytes, name, false, ['wrapKey']);
                wrapOnly.algorithm.name = 'HMAC';
                results.push((await s.wrapKey('raw', payload, wrapOnly, alg)).byteLength > 0);
            }
            return results;
            """);
        Assert.Equal(
            string.Concat(Enumerable.Repeat("\"InvalidAccessError\",\"InvalidAccessError\",\"InvalidAccessError\",true,", 3)).TrimEnd(','),
            result!.ToJsonString()[1..^1]);
    }

    // upstream d853a73: key_unwrapping_checks_the_unwrapping_keys_algorithm_and_usage
    [Fact]
    public async Task KeyUnwrappingChecksTheUnwrappingKeysAlgorithmAndUsage()
    {
        JsonNode? result = await Probe(Algorithms + """
            const bytes = new Uint8Array(16);
            const payload = await s.importKey('raw', bytes, 'AES-GCM', true, ['encrypt']);
            const results = [];
            for (const name of ['AES-GCM', 'AES-CBC', 'AES-CTR']) {
                const alg = algorithmFor(name);
                const wrapOnly = await s.importKey('raw', bytes, name, false, ['wrapKey']);
                const wrapped = await s.wrapKey('raw', payload, wrapOnly, alg);
                const wrongAlgorithm = await s.importKey('raw', bytes, {name:'HMAC', hash:'SHA-256'}, false, ['sign']);
                const unwrap = key => s.unwrapKey('raw', wrapped, key, alg, 'AES-GCM', true, ['encrypt']);
                results.push(await errorName(() => unwrap(wrapOnly)));
                results.push(await errorName(() => unwrap(wrongAlgorithm)));
                wrongAlgorithm.algorithm.name = name;
                results.push(await errorName(() => unwrap(wrongAlgorithm)));
                const unwrapOnly = await s.importKey('raw', bytes, name, false, ['unwrapKey']);
                unwrapOnly.algorithm.name = 'HMAC';
                const restored = await unwrap(unwrapOnly);
                results.push(hex(await s.exportKey('raw', restored)));
            }
            return results;
            """);
        Assert.Equal(
            string.Concat(Enumerable.Repeat("\"InvalidAccessError\",\"InvalidAccessError\",\"InvalidAccessError\",\"00000000000000000000000000000000\",", 3)).TrimEnd(','),
            result!.ToJsonString()[1..^1]);
    }

    // upstream d853a73: unsupported_wrapping_algorithms_still_report_not_supported
    [Fact]
    public async Task UnsupportedWrappingAlgorithmsStillReportNotSupported()
    {
        JsonNode? result = await Probe("""
            const key = await s.importKey('raw', new Uint8Array(16), {name:'HMAC', hash:'SHA-256'}, true, ['sign']);
            return [
                await errorName(() => s.wrapKey('raw', key, key, 'HMAC')),
                await errorName(() => s.unwrapKey('raw', new Uint8Array(24), key, 'HMAC', 'AES-GCM', false, ['encrypt'])),
            ];
            """);
        Assert.Equal("""["NotSupportedError","NotSupportedError"]""", result!.ToJsonString());
    }

    // upstream d853a73: cloned_wrapping_keys_keep_their_internal_algorithm (AES-GCM for AES-KW)
    [Fact]
    public async Task ClonedWrappingKeysKeepTheirInternalAlgorithm()
    {
        JsonNode? result = await Probe("""
            const key = await s.importKey('raw', new Uint8Array(16), 'AES-GCM', false, ['wrapKey', 'unwrapKey']);
            key.algorithm.name = 'HMAC';
            const copy = structuredClone({first:key, second:key});
            const payload = await s.importKey('raw', new Uint8Array(16), 'AES-GCM', true, ['encrypt']);
            const alg = {name: 'AES-GCM', iv: new Uint8Array(12)};
            const wrapped = await s.wrapKey('raw', payload, copy.first, alg);
            const restored = await s.unwrapKey('raw', wrapped, copy.first, alg, 'AES-GCM', true, ['encrypt']);
            return [copy.first.algorithm.name, copy.first === copy.second, hex(await s.exportKey('raw', restored))];
            """);
        Assert.Equal("""["AES-GCM",true,"00000000000000000000000000000000"]""", result!.ToJsonString());
    }
}
