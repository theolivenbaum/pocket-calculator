using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PocketCalculator.Js.Ops;
using PocketCalculator.Net;

namespace PocketCalculator.Cdp;

/// <summary>What the WebSocket reader hands the connection's processor.</summary>
internal abstract record ServerMessage
{
    private ServerMessage()
    {
    }

    internal sealed record Cdp(string Text, ChannelWriter<string> ReplyTx) : ServerMessage;

    internal sealed record NewConnection(ChannelWriter<string> ReplyTx) : ServerMessage;
}

/// <summary>Pure helpers shared by the server's accept, processor and WS paths.</summary>
internal static class ServerSupport
{
    /// <summary>
    /// Written to a connection's reply queue for a message the processor cannot answer
    /// (it does not parse), so the idle timeout stops counting it as a command in
    /// flight. The send loop drops it: nothing goes on the wire. Compared by reference.
    /// </summary>
    internal static readonly string UnansweredMarker = new("\0unanswered".ToCharArray());

    /// <summary>
    /// Whether a raw CDP frame is exactly a <c>Page.navigate</c> call, and so
    /// should take the spawn-and-defer navigation path.
    /// </summary>
    /// <remarks>
    /// Matching on the parsed method rather than a <c>Contains("Page.navigate")</c>
    /// substring avoids catching <c>Page.navigateToHistoryEntry</c> (goBack /
    /// goForward), which has no <c>url</c> param and belongs to its own handler,
    /// or any other frame that merely embeds the literal text (for example a
    /// <c>Runtime.evaluate</c> expression). See issue #363.
    /// </remarks>
    internal static bool IsNavigateMethod(string text) =>
        CdpRequest.TryParse(text) is { } req &&
        string.Equals(req.Method, "Page.navigate", StringComparison.Ordinal);

    /// <summary>
    /// Parse a CDP header list (<c>[{"name":..,"value":..}, ..]</c>, as used by
    /// <c>Fetch.continueRequest</c> / <c>fulfillRequest</c>) into a map. Null when
    /// the <c>headers</c> field is absent, so the caller can leave the request's
    /// headers untouched rather than clearing them.
    /// </summary>
    internal static Dictionary<string, string>? ParseCdpHeaders(JsonNode? parameters)
    {
        if (parameters.Get("headers").AsJsonArray() is not { } array)
        {
            return null;
        }

        Dictionary<string, string> headers = new(StringComparer.Ordinal);
        foreach (var entry in array)
        {
            if (entry.Get("name").AsString() is { } name &&
                entry.Get("value").AsString() is { } value)
            {
                headers[name] = value;
            }
        }

        return headers;
    }

    /// <summary>
    /// The lenient base64 decoder <c>Fetch.fulfillRequest</c> bodies go through:
    /// every character outside the alphabet, padding included, is dropped before
    /// the remaining sextets are regrouped, and invalid UTF-8 in the result is
    /// replaced rather than rejected.
    /// </summary>
    internal static string DecodeBase64(string input)
    {
        static int Value(char c) => c switch
        {
            >= 'A' and <= 'Z' => c - 'A',
            >= 'a' and <= 'z' => c - 'a' + 26,
            >= '0' and <= '9' => c - '0' + 52,
            '+' => 62,
            '/' => 63,
            _ => -1,
        };

        List<byte> sextets = [];
        foreach (var c in input)
        {
            var value = Value(c);
            if (value >= 0)
            {
                sextets.Add((byte)value);
            }
        }

        var output = new List<byte>(sextets.Count * 3 / 4);
        for (var i = 0; i < sextets.Count; i += 4)
        {
            var length = Math.Min(4, sextets.Count - i);
            var b0 = sextets[i];
            var b1 = length > 1 ? sextets[i + 1] : (byte)0;
            var b2 = length > 2 ? sextets[i + 2] : (byte)0;
            var b3 = length > 3 ? sextets[i + 3] : (byte)0;
            output.Add((byte)((b0 << 2) | (b1 >> 4)));
            if (length > 2)
            {
                output.Add((byte)((b1 << 4) | (b2 >> 2)));
            }

            if (length > 3)
            {
                output.Add((byte)((b2 << 6) | b3));
            }
        }

        return Encoding.UTF8.GetString([.. output]);
    }

    /// <summary>
    /// Answer the trivially-constant commands straight off the WebSocket reader,
    /// without waking the connection's processor.
    /// </summary>
    internal static string? FastPathResponse(string text)
    {
        if (CdpRequest.TryParse(text) is not { } req)
        {
            return null;
        }

        JsonNode? result = req.Method switch
        {
            "Network.enable" or "Network.setCacheDisabled" or "Network.setRequestInterception"
                or "Page.setLifecycleEventsEnabled" or "Page.setInterceptFileChooserDialog"
                or "Runtime.runIfWaitingForDebugger" or "Runtime.discardConsoleEntries"
                or "Performance.enable" or "Log.enable" or "Security.enable"
                or "Emulation.setTouchEmulationEnabled"
                or "CSS.enable" or "Accessibility.enable" or "ServiceWorker.enable"
                or "Inspector.enable" or "Debugger.enable" or "Profiler.enable"
                or "HeapProfiler.enable" or "Overlay.enable" or "Storage.enable"
                or "Target.setAutoAttach" => new JsonObject(),
            "Browser.getVersion" => new JsonObject
            {
                ["protocolVersion"] = "1.3",
                ["product"] = "Chrome/145.0.0.0",
                ["revision"] = "@0000000000000000000000000000000000000000",
                ["userAgent"] =
                    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36",
                ["jsVersion"] = "14.5.0.0",
            },
            "Browser.setDownloadBehavior" or "Browser.getWindowBounds" => new JsonObject(),
            _ => null,
        };

        return result is null
            ? null
            : CdpResponse.Success(req.Id, result, req.SessionId).ToJson();
    }

    internal static (string Domain, string Name, string Path, CookiePartitionKey? Partition) CookieKey(CookieInfo cookie) =>
        (cookie.Domain, cookie.Name, cookie.Path, cookie.PartitionKey);

    internal static bool CookieValuesMatch(CookieInfo left, CookieInfo right) =>
        string.Equals(left.Value, right.Value, StringComparison.Ordinal) &&
        left.Secure == right.Secure &&
        left.HttpOnly == right.HttpOnly &&
        string.Equals(left.SameSite, right.SameSite, StringComparison.Ordinal) &&
        left.Expires == right.Expires;

    /// <summary>
    /// Apply only one connection's cookie changes to the persistence template.
    /// </summary>
    /// <remarks>
    /// Unchanged cookies cannot overwrite another connection's updates, while
    /// explicit deletes and replacements still persist.
    /// </remarks>
    internal static void MergeCookieDelta(
        CookieJar destination,
        IReadOnlyList<CookieInfo> initial,
        IReadOnlyList<CookieInfo> current) =>
        MergeCookieDelta(destination, DomainScoped(initial), DomainScoped(current));

    /// <inheritdoc cref="MergeCookieDelta(CookieJar, IReadOnlyList{CookieInfo}, IReadOnlyList{CookieInfo})"/>
    /// <remarks>
    /// Deviation: Rust's <c>merge_cookie_delta</c> writes the changes back through
    /// <c>set_cookies_from_cdp</c>, which widens every host-only cookie a connection set
    /// to its subdomains in the persisted jar. This carries each cookie's host-only
    /// flag through, the same leak upstream 04418a5 closed for isolated copies.
    /// </remarks>
    internal static void MergeCookieDelta(
        CookieJar destination,
        IReadOnlyList<(CookieInfo Cookie, bool HostOnly)> initial,
        IReadOnlyList<(CookieInfo Cookie, bool HostOnly)> current)
    {
        Dictionary<(string, string, string, CookiePartitionKey?), (CookieInfo Cookie, bool HostOnly)> before = [];
        foreach (var scoped in initial)
        {
            before[CookieKey(scoped.Cookie)] = scoped;
        }

        Dictionary<(string, string, string, CookiePartitionKey?), (CookieInfo Cookie, bool HostOnly)> after = [];
        foreach (var scoped in current)
        {
            after[CookieKey(scoped.Cookie)] = scoped;
        }

        foreach (var (key, scoped) in before)
        {
            if (!after.ContainsKey(key))
            {
                destination.DeleteCookiesFiltered(
                    scoped.Cookie.Name, scoped.Cookie.Domain, scoped.Cookie.Path, scoped.Cookie.PartitionKey);
            }
        }

        List<(CookieInfo, bool)> changed = [];
        foreach (var (key, scoped) in after)
        {
            if (before.TryGetValue(key, out var previous)
                && previous.HostOnly == scoped.HostOnly
                && CookieValuesMatch(previous.Cookie, scoped.Cookie))
            {
                continue;
            }

            changed.Add(scoped);
        }

        destination.SetCookiesFromCdpWithScope(changed);
    }

    private static List<(CookieInfo, bool)> DomainScoped(IReadOnlyList<CookieInfo> cookies)
    {
        var scoped = new List<(CookieInfo, bool)>(cookies.Count);
        foreach (var cookie in cookies)
        {
            scoped.Add((cookie, false));
        }

        return scoped;
    }

    /// <summary>Seconds since the Unix epoch, as <c>Duration::as_secs_f64</c> reports them.</summary>
    internal static double UnixSeconds() =>
        (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>
    /// Turn one intercepted request into the <c>Network.requestWillBeSent</c> +
    /// <c>Fetch.requestPaused</c> pair the client resolves, and park its resolver.
    /// </summary>
    /// <remarks>
    /// <paramref name="loader"/> is set for the navigating page's own document request
    /// (upstream b76d966): it is announced under that loader with its URL as the document URL,
    /// and parked as a main-document pause, whose failure the navigation reports itself.
    /// </remarks>
    internal static void EmitInterceptedRequest(
        InterceptedRequest intercepted,
        string frameId,
        string? sessionId,
        ChannelWriter<string> replyTx,
        Dictionary<string, PausedIntercept> interceptedPaused,
        string? loader = null)
    {
        CdpLog.Info(
            $"INTERCEPTION: requestPaused for {intercepted.Method} {intercepted.Url} (sending to client)");
        var now = UnixSeconds();
        JsonObject Request() => new()
        {
            ["url"] = intercepted.Url,
            ["method"] = intercepted.Method,
            ["headers"] = HeadersObject(intercepted.Headers),
            ["initialPriority"] = "High",
            ["referrerPolicy"] = "strict-origin-when-cross-origin",
        };

        var requestWillBeSent = new JsonObject
        {
            ["method"] = "Network.requestWillBeSent",
            ["params"] = new JsonObject
            {
                ["requestId"] = intercepted.RequestId,
                ["loaderId"] = loader ?? "",
                ["documentURL"] = loader is null ? "" : intercepted.Url,
                ["request"] = Request(),
                ["timestamp"] = now,
                ["wallTime"] = now,
                ["initiator"] = new JsonObject { ["type"] = "script" },
                ["type"] = intercepted.ResourceType,
                ["frameId"] = frameId,
            },
            ["sessionId"] = sessionId,
        };
        replyTx.TryWrite(CdpJson.Serialize(requestWillBeSent));

        var requestPaused = new JsonObject
        {
            ["method"] = "Fetch.requestPaused",
            ["params"] = new JsonObject
            {
                ["requestId"] = intercepted.RequestId,
                ["request"] = Request(),
                ["frameId"] = frameId,
                ["resourceType"] = intercepted.ResourceType,
                ["networkId"] = intercepted.RequestId,
                ["responseErrorReason"] = null,
                ["responseStatusCode"] = null,
                ["responseHeaders"] = null,
            },
            ["sessionId"] = sessionId,
        };
        replyTx.TryWrite(CdpJson.Serialize(requestPaused));
        interceptedPaused[intercepted.RequestId] = new PausedIntercept(intercepted.Resolver, loader is not null);
    }

    /// <summary>
    /// A page's own document request, as opposed to a frame document a script fetched: the
    /// page's <c>{pageId}.{n}</c> network-event namespace, not <c>intercept-N</c> (upstream b76d966).
    /// </summary>
    internal static bool IsMainDocumentIntercept(InterceptedRequest request, string pageId) =>
        string.Equals(request.PageId, pageId, StringComparison.Ordinal)
        && string.Equals(request.ResourceType, "Document", StringComparison.Ordinal)
        && request.RequestId.StartsWith(pageId + ".", StringComparison.Ordinal);

    internal static JsonObject HeadersObject(IReadOnlyDictionary<string, string> headers)
    {
        var obj = new JsonObject();
        foreach (var (name, value) in headers)
        {
            obj[name] = value;
        }

        return obj;
    }

    /// <summary>
    /// Resolve a paused request from a <c>Fetch.*</c> frame. True when the frame
    /// named a request this connection had parked, in which case the command's
    /// response has already been sent and dispatch must not run for it.
    /// </summary>
    internal static bool HandleFetchResolution(
        string text,
        CdpContext ctx,
        ChannelWriter<string> replyTx,
        Dictionary<string, PausedIntercept> interceptedPaused)
    {
        _ = ctx;
        if (CdpRequest.TryParse(text) is not { } req)
        {
            return false;
        }

        var method = req.Method;
        var requestId = req.Params.Get("requestId").AsStringOr(string.Empty);
        CdpLog.Info(
            $"INTERCEPTION resolution: {method} for {requestId}, paused_count={interceptedPaused.Count}");

        if (method is not ("Fetch.continueRequest" or "Fetch.fulfillRequest" or "Fetch.failRequest")
            || !interceptedPaused.ContainsKey(requestId))
        {
            return false;
        }

        CdpLog.Info($"INTERCEPTION resolved: {requestId}");

        // upstream 3f9acc2: the resolution is built before the pause is taken, so a malformed
        // command is answered with -32602 and the request stays paused; a corrected command on
        // the same requestId still drives the fetch.
        InterceptResolution resolution;
        string? error = null;
        switch (method)
        {
            case "Fetch.continueRequest":
                // Honor the client's overrides (Playwright route.continue, Puppeteer
                // request.continue). postData is CDP `binary`, so it arrives base64-encoded
                // (upstream b1b3abe); the fetch op re-validates a rewritten URL.
                resolution = new InterceptResolution.Continue(
                    req.Params.Get("url").AsString(),
                    req.Params.Get("method").AsString(),
                    ParseCdpHeaders(req.Params),
                    DecodeOptionalBinary(req.Params, "postData", "continueRequest postData", ref error));
                break;

            case "Fetch.fulfillRequest":
            {
                var status = (int)(req.Params.Get("responseCode").AsU64() ?? 200);
                byte[]? bytes = DecodeOptionalBinary(req.Params, "body", "fulfillRequest body", ref error);
                Dictionary<string, string> headers = new(StringComparer.Ordinal);
                if (req.Params.Get("responseHeaders").AsJsonArray() is { } array)
                {
                    foreach (var header in array)
                    {
                        if (header.Get("name").AsString() is { } name &&
                            header.Get("value").AsString() is { } value)
                        {
                            // upstream b76d966: repeated Set-Cookie headers all survive, joined
                            // by newlines under "set-cookie"; the last of any other name wins.
                            if (name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
                            {
                                headers["set-cookie"] = headers.TryGetValue("set-cookie", out var cookies)
                                    ? cookies + "\n" + value
                                    : value;
                            }
                            else
                            {
                                headers[name] = value;
                            }
                        }
                    }
                }

                // The exact bytes go with the UTF-8 reading, for a fulfilled document.
                resolution = new InterceptResolution.Fulfill(
                    status,
                    headers,
                    bytes is null ? string.Empty : Encoding.UTF8.GetString(bytes),
                    bytes ?? []);
                break;
            }

            default:
                resolution = new InterceptResolution.Fail(
                    req.Params.Get("errorReason").AsStringOr("Failed"));
                break;
        }

        if (error is not null)
        {
            replyTx.TryWrite(CdpResponse.Failure(req.Id, -32602, error, req.SessionId).ToJson());
            return true;
        }

        interceptedPaused.Remove(requestId, out var paused);
        paused!.Resolver.TrySetResult(resolution);
        replyTx.TryWrite(
            CdpResponse.Success(req.Id, new JsonObject(), req.SessionId).ToJson());
        return true;
    }

    /// <summary>
    /// Rust <c>decode_optional_binary</c>: an absent field is null (leave the body alone); a
    /// present one must be a strict standard base64 string, else <paramref name="error"/> is set.
    /// The reason text after the colon is the port's, not the Rust base64 crate's.
    /// </summary>
    internal static byte[]? DecodeOptionalBinary(JsonNode? parameters, string field, string what, ref string? error)
    {
        if (parameters is not JsonObject obj || !obj.TryGetPropertyValue(field, out JsonNode? value))
        {
            return null;
        }

        if (value.AsString() is not { } raw)
        {
            error ??= $"{what} must be a base64 string";
            return null;
        }

        if (raw.Length % 4 != 0)
        {
            error ??= $"{what} is not valid base64: invalid length";
            return null;
        }

        foreach (char c in raw)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
            {
                error ??= $"{what} is not valid base64: invalid symbol";
                return null;
            }
        }

        try
        {
            return Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            error ??= $"{what} is not valid base64: invalid padding";
            return null;
        }
    }

    internal static string Utf8Preview(string text, int maxBytes) =>
        CdpUtil.TruncateOnCharBoundary(text, maxBytes);

    internal static string FormatPort(int port) => port.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A request the client was shown paused, waiting on its resolution.</summary>
/// <param name="Resolver">Completes the waiting fetch or navigation.</param>
/// <param name="MainDocument">
/// The navigating page's own document request (upstream b76d966). Its failure is reported by
/// the navigation, under the navigation's loader, not by <c>Fetch.failRequest</c>.
/// </param>
internal sealed record PausedIntercept(TaskCompletionSource<InterceptResolution> Resolver, bool MainDocument = false);
