using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PocketCalculator.Browser;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Net;

namespace PocketCalculator.Mcp;

/// <summary>
/// The MCP browser-automation tools. Each one is the body of the matching
/// <c>tool_*</c> function in <c>crates/obscura-mcp/src/lib.rs</c>: the reply
/// strings are the tool's observable result and are reproduced exactly.
/// </summary>
internal static partial class Tools
{
    // ===== shared helpers =====

    /// <summary>
    /// Resolve a tool call's element target from either <c>ref</c> (preferred) or
    /// <c>selector</c> (fallback). Agents that called <c>browser_snapshot</c> /
    /// <c>browser_interactive_elements</c> get a ref table they can refer to;
    /// scripted clients can still pass raw CSS selectors.
    /// </summary>
    internal static string ResolveTarget(JsonNode? args, BrowserState state)
    {
        if (args.Get("ref").AsString() is { } reference)
        {
            return state.RefToSelector(reference);
        }

        if (args.Get("selector").AsString() is { } selector)
        {
            return selector;
        }

        throw new ToolException("Missing 'ref' or 'selector' parameter");
    }

    private static string RequireString(JsonNode? args, string name, string error) =>
        args.Get(name).AsString() ?? throw new ToolException(error);

    /// <summary>Rust's <c>n as usize</c> on a <c>u64</c>: a wider value saturates here rather than wrapping.</summary>
    private static int? ClampToInt(ulong? value) =>
        value is { } n ? (int)Math.Min(n, int.MaxValue) : null;

    /// <summary>Rust's <c>f64 as u64</c>, which saturates at both ends and maps NaN to 0.</summary>
    private static ulong SaturatingU64(double value)
    {
        if (double.IsNaN(value) || value <= 0.0)
        {
            return 0;
        }

        return value >= ulong.MaxValue ? ulong.MaxValue : (ulong)value;
    }

    // ===== core tools =====

    /// <summary>
    /// The scheme gate every MCP navigation takes: <c>browser_navigate</c>,
    /// <c>browser_tab_new</c>, back, forward and reload.
    /// </summary>
    /// <remarks>
    /// An MCP caller must not read local files through the browser session: navigate to
    /// file:// and snapshot the text. Rust tests the scheme of <c>Url::parse</c>, so an
    /// unparseable string falls through to the navigation and fails there with its own
    /// error. Deviation from tools.rs, which gates <c>browser_navigate</c> only
    /// (SECURITY.md H3): a new tab or a history step could open a local file too.
    /// Clicks and scripts are covered in <c>Page.ProcessPendingNavigationOutcomeAsync</c>.
    /// </remarks>
    internal static void RefuseFileNavigation(string url)
    {
        if (PocketCalculator.Js.Url.UrlRecord.Parse(url) is { Scheme: "file" })
        {
            throw new ToolException("file:// navigation is disabled for MCP");
        }
    }

    /// <summary>
    /// Port of upstream <c>evaluate_tool_script</c> (a3aea0a): run a tool's host script
    /// under a 5 s watchdog and report a thrown exception or a timeout as a tool error,
    /// where <see cref="Page.EvaluateHost"/> answers null and the tool went on to report
    /// success.
    /// </summary>
    internal static JsonNode? EvaluateToolScript(Page page, string script)
    {
        if (page.Js is not { } js)
        {
            throw new ToolException("Navigate before evaluating JavaScript");
        }
        // Armed through result serialization, which can run page code.
        var watchdog = js.ArmWatchdog(TimeSpan.FromSeconds(5));
        JsonNode? result;
        try
        {
            result = js.EvaluateHostChecked(script);
        }
        catch (PocketCalculator.Js.Runtime.JsRuntimeException error)
        {
            if (js.DisarmWatchdog(watchdog))
            {
                throw new ToolException("JavaScript execution or serialization exceeded the 5000ms deadline");
            }
            throw new ToolException(error.Message);
        }
        if (js.DisarmWatchdog(watchdog))
        {
            throw new ToolException("JavaScript execution or serialization exceeded the 5000ms deadline");
        }
        return result;
    }

    internal static async Task<string> NavigateAsync(JsonNode? args, BrowserState state)
    {
        var url = RequireString(args, "url", "Missing url parameter");
        RefuseFileNavigation(url);

        var waitUntil = args.Get("waitUntil").AsString() ?? "load";

        var condition = WaitUntilExtensions.ParseWaitUntil(waitUntil);
        var ua = state.UserAgent;
        var page = state.PageMut();
        if (ua is not null)
        {
            page.HttpClient.SetUserAgent(ua);
        }

        try
        {
            await page.NavigateWithWaitAsync(url, condition).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ToolException)
        {
            throw new ToolException(state.OperatorNetworkHints ? OperatorHint.WithHint(error.Message) : error.Message);
        }

        var summary = $"Navigated to {page.UrlString()} — \"{page.Title}\"";
        // DOM changed - invalidate the ref table. Next snapshot will rebuild.
        state.InteractiveRefs.Clear();
        return summary;
    }

    /// <summary>
    /// URL, title, the actionable controls with their refs and state, then the visible body
    /// text, within one character budget (upstream 3c56f59). A control line is shown whole or
    /// not at all, so a ref is never cut; URL and title are capped at 256 characters each.
    /// </summary>
    internal static string Snapshot(JsonNode? args, BrowserState state)
    {
        var maxChars = ClampToInt(args.Get("max_chars").AsU64()) ?? McpServer.DefaultTextLimit;
        RebuildInteractiveRefs(state);
        var page = state.PageMut();
        var url = TextExtraction.Truncate(page.UrlString(), 256);
        var title = TextExtraction.Truncate(page.Title, 256);
        if (maxChars == 0)
        {
            return $"URL: {url}\nTitle: {title}";
        }

        var context = AccessibilityContext.ForPage(page);
        var bodyText = page.WithDom(dom =>
            TryQuery(dom, "body") is { } body ? TextExtraction.ExtractText(dom, body, context) : string.Empty)
            ?? string.Empty;

        List<string> lines = context is null ? [] : InteractiveLines(state, context, int.MaxValue);
        var content = new StringBuilder();
        var remaining = maxChars;
        var shown = 0;
        foreach (var line in lines)
        {
            var cost = TextExtraction.CharCount(line) + 1;
            if (cost > remaining)
            {
                break;
            }

            content.Append(line).Append('\n');
            remaining -= cost;
            shown++;
        }

        if (shown < lines.Count)
        {
            content.Append(CultureInfo.InvariantCulture, $"...({lines.Count - shown} controls omitted; use browser_interactive_elements)\n");
        }

        content.Append(TextExtraction.Truncate(bodyText.Trim(), remaining));
        return $"URL: {url}\nTitle: {title}\n\n{content}";
    }

    internal static async Task<string> ClickAsync(JsonNode? args, BrowserState state)
    {
        var selector = ResolveTarget(args, state);

        // Every snippet in this file reaches the DOM through __obscura_host.dom, the
        // built-ins as bootstrap.js left them (SECURITY.md L10). DEVIATION from
        // crates/obscura-mcp, whose snippets call document.querySelector, el.click and
        // new Event on whatever the page has put there, so a page could send an agent's
        // click or fill to another element, or answer for it.
        var js = $$"""
            (function(){
                var h = __obscura_host.dom;
                var el = h.querySelector(h.document(), {{McpJson.String(selector)}});
                if (!el) return "error:element not found";
                h.call(el, 'click', []);
                return "ok";
            })()
            """;

        // The agent's click stands for a user's, so it gives the page user activation:
        // a link it follows reports Sec-Fetch-User: ?1, as a real click in Chromium does.
        state.PageMut().NoteUserActivation();
        var result = EvaluateToolScript(state.PageMut(), js);
        if (result.AsString() == "error:element not found")
        {
            throw new ToolException($"Element not found: {selector}");
        }

        // A click can navigate or rewrite the DOM; the old ref table may no longer
        // match. Conservative: invalidate. Next snapshot rebuilds.
        state.InteractiveRefs.Clear();
        await state.SettleSyntheticNavigationAsync().ConfigureAwait(false);
        return $"Clicked '{selector}'";
    }

    internal static async Task<string> FillAsync(JsonNode? args, BrowserState state)
    {
        var selector = ResolveTarget(args, state);
        var value = RequireString(args, "value", "Missing value parameter");

        var js = $$"""
            (function(){
                var h = __obscura_host.dom;
                var el = h.querySelector(h.document(), {{McpJson.String(selector)}});
                if (!el) return "error:element not found";
                __obscura_host.setFieldValue(el, "value", {{McpJson.String(value)}});
                h.dispatch(el, h.event("Event", "input", {__proto__:null,bubbles:true}, true));
                h.dispatch(el, h.event("Event", "change", {__proto__:null,bubbles:true}, true));
                return "ok";
            })()
            """;

        // EvaluateHost: markTrusted and setFieldValue are host helpers here, where
        // crates/obscura-mcp names them as page-visible __obscura_* globals.
        var result = EvaluateToolScript(state.PageMut(), js);
        if (result.AsString() == "error:element not found")
        {
            throw new ToolException($"Element not found: {selector}");
        }

        await state.SettleSyntheticNavigationAsync().ConfigureAwait(false);
        return $"Filled '{selector}' with value";
    }

    internal static async Task<string> TypeAsync(JsonNode? args, BrowserState state)
    {
        var selector = ResolveTarget(args, state);
        var text = RequireString(args, "text", "Missing text parameter");

        var js = $$"""
            (function(){
                var h = __obscura_host.dom;
                var el = h.querySelector(h.document(), {{McpJson.String(selector)}});
                if (!el) return "error:element not found";
                __obscura_host.setFieldValue(el, "value", (h.get(el, "value") || "") + {{McpJson.String(text)}});
                h.dispatch(el, h.event("Event", "input", {__proto__:null,bubbles:true}, true));
                return "ok";
            })()
            """;

        var result = EvaluateToolScript(state.PageMut(), js);
        if (result.AsString() == "error:element not found")
        {
            throw new ToolException($"Element not found: {selector}");
        }

        await state.SettleSyntheticNavigationAsync().ConfigureAwait(false);
        return $"Typed into '{selector}'";
    }

    internal static async Task<string> PressKeyAsync(JsonNode? args, BrowserState state)
    {
        var key = RequireString(args, "key", "Missing key parameter");
        var selector = args.Get("selector").AsString();

        var target = selector is not null
            ? $"h.querySelector(h.document(), {McpJson.String(selector)})"
            : "h.document()";

        // Untrusted, as upstream: only the CDP Input domain marks key events trusted.
        var js = $$"""
            (function(){
                var h = __obscura_host.dom;
                var t = {{target}};
                if (!t) return "error:element not found";
                h.dispatch(t, h.event("KeyboardEvent", "keydown", {__proto__:null,key:{{McpJson.String(key)}},bubbles:true}, false));
                h.dispatch(t, h.event("KeyboardEvent", "keyup", {__proto__:null,key:{{McpJson.String(key)}},bubbles:true}, false));
                return "ok";
            })()
            """;

        state.PageMut().NoteUserActivation();
        if (EvaluateToolScript(state.PageMut(), js).AsString() == "error:element not found")
        {
            throw new ToolException("Element not found for key press");
        }
        await state.SettleSyntheticNavigationAsync().ConfigureAwait(false);
        return $"Pressed key '{key}'";
    }

    internal static string SelectOption(JsonNode? args, BrowserState state)
    {
        var selector = RequireString(args, "selector", "Missing selector parameter");
        var value = RequireString(args, "value", "Missing value parameter");

        var js = $$"""
            (function(){
                var h = __obscura_host.dom;
                var el = h.querySelector(h.document(), {{McpJson.String(selector)}});
                if (!el) return "error:element not found";
                var opts = h.get(el, "options") || [];
                var opt = null;
                for (var i = 0; i < opts.length; i++) {
                    var o = opts[i];
                    if (h.get(o, "value") === {{McpJson.String(value)}} || h.get(o, "text") === {{McpJson.String(value)}}) { opt = o; break; }
                }
                if (!opt) return "error:option not found";
                h.set(el, "value", h.get(opt, "value"));
                h.dispatch(el, h.event("Event", "change", {__proto__:null,bubbles:true}, false));
                return "ok";
            })()
            """;

        var result = EvaluateToolScript(state.PageMut(), js);
        return result.AsString() switch
        {
            "error:element not found" => throw new ToolException($"Element not found: {selector}"),
            "error:option not found" => throw new ToolException($"Option not found: {value}"),
            _ => $"Selected '{value}' in '{selector}'",
        };
    }

    /// <summary>
    /// <c>browser_evaluate</c> (upstream 7500e6d, a3aea0a): a promise the expression returns is
    /// awaited for up to <c>timeoutMs</c>, through the same host-side await as CDP's
    /// <c>Runtime.evaluate</c>, which keeps the outcome where page script cannot forge it and
    /// drives the page's tasks while it waits. A throw or rejection is a tool error.
    /// </summary>
    internal static async Task<string> EvaluateAsync(JsonNode? args, BrowserState state)
    {
        var expression = RequireString(args, "expression", "Missing expression parameter");
        var timeoutMs = Math.Max(args.Get("timeoutMs").AsU64() ?? 5_000, 1);

        var page = state.PageMut();
        if (page.Js is null)
        {
            throw new ToolException("Navigate before evaluating JavaScript");
        }

        PocketCalculator.Js.Runtime.RemoteObjectInfo info;
        try
        {
            info = await page.EvaluateForCdpWithTimeoutAsync(expression, true, true, timeoutMs).ConfigureAwait(false);
        }
        catch (PocketCalculator.Js.Runtime.JsRuntimeException error)
        {
            throw new ToolException(error.Message);
        }
        finally
        {
            // MCP never exposes remote handles. Release results, including exceptions.
            page.ReleaseObjectGroup();
        }

        if (info.Thrown)
        {
            throw new ToolException(info.Description);
        }

        var result = info.Value;
        await state.SettleSyntheticNavigationAsync().ConfigureAwait(false);
        return result.AsString() ?? (result.IsNull() ? "null" : McpJson.SerializePretty(result));
    }

    /// <summary>
    /// <c>browser_wait_for_function</c> (upstream 7500e6d, b13604b): poll a predicate until it is
    /// truthy, driving the page's own tasks between polls so a condition that a timer or a
    /// network callback makes true can come true.
    /// </summary>
    internal static async Task<string> WaitForFunctionAsync(JsonNode? args, BrowserState state)
    {
        var expression = (args.Get("function").AsString() ?? args.Get("expression").AsString())
            ?? throw new ToolException("Missing function parameter");
        var timeoutMs = args.Get("timeout").AsU64() ?? 30_000;
        var pollMs = Math.Max(args.Get("pollIntervalMs").AsU64() ?? 100, 10);

        var deadline = Environment.TickCount64 + (long)Math.Min(timeoutMs, (ulong)(long.MaxValue / 4));
        ulong attempts = 0;
        var probe = "JSON.stringify((function(){ try { return (function(){ return !!(" + expression
            + "); })(); } catch (e) { return false; } })())";
        while (true)
        {
            attempts++;
            var value = state.PageMut().Evaluate(probe);
            var truthy = value.AsString() is { } raw ? raw == "true" : value.AsBool() ?? false;
            if (truthy)
            {
                return $"condition met after {attempts.ToString(CultureInfo.InvariantCulture)} poll(s)";
            }

            if (Environment.TickCount64 >= deadline)
            {
                throw new ToolException(
                    $"condition not met within {timeoutMs.ToString(CultureInfo.InvariantCulture)} ms after {attempts.ToString(CultureInfo.InvariantCulture)} poll(s): {expression}");
            }

            var pollDeadline = Math.Min(Environment.TickCount64 + (long)Math.Min(pollMs, (ulong)int.MaxValue), deadline);
            await state.AdvanceActivePageTasksAsync().ConfigureAwait(false);
            var rest = pollDeadline - Environment.TickCount64;
            if (rest > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(rest)).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<string> WaitForAsync(JsonNode? args, BrowserState state)
    {
        var selector = RequireString(args, "selector", "Missing selector parameter");
        var timeoutSecs = SaturatingU64(args.Get("timeout").AsF64() ?? 30.0);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSecs);
        // Exponential backoff: 5 -> 10 -> 20 -> ... -> 200 ms. The old fixed 200ms
        // tick added up to a full poll cycle of latency every time; a selector that
        // appears in 30ms now returns in ~35ms instead of the next 200ms tick.
        ulong tickMs = 5;
        while (true)
        {
            var found = state.PageMut().WithDom(dom => TryQuery(dom, selector) is not null);
            if (found)
            {
                return $"Found '{selector}'";
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new ToolException($"Timeout waiting for '{selector}'");
            }

            await PumpForAsync(state, tickMs).ConfigureAwait(false);
            if (tickMs < 200)
            {
                tickMs = Math.Min(tickMs * 2, 200);
            }
        }
    }

    /// <summary>
    /// <c>tokio::time::timeout(tick, advance_active_page_tasks())</c>: give the page
    /// one pump turn before the poll loop rechecks its predicate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tick is deliberately not enforced. <c>tokio::time::timeout</c> bounds the
    /// wait by dropping the future, which cancels the turn; .NET cannot cancel a
    /// running turn, so bounding the wait here would leave a live event-loop turn
    /// running against the same V8 isolate and DOM that the next iteration's
    /// predicate reads - a data race the Rust code never has. Awaiting the turn is
    /// the same wait in every case except a turn slower than the tick, where the
    /// loop simply notices the deadline one turn later.
    /// </para>
    /// <para>
    /// It costs no responsiveness on an idle page: a turn that reaches idle returns
    /// immediately, and <c>timeout</c> returns as soon as its future completes too,
    /// so both engines re-poll at the same rate.
    /// </para>
    /// </remarks>
    private static Task PumpForAsync(BrowserState state, ulong tickMs)
    {
        _ = tickMs;
        return state.AdvanceActivePageTasksAsync();
    }

    internal static PocketCalculator.Dom.NodeId? TryQuery(PocketCalculator.Dom.DomTree dom, string selector) =>
        dom.TryQuerySelector(selector, out var result, out _) ? result : null;

    internal static string NetworkRequests(BrowserState state)
    {
        var page = state.PageMut();
        var events = page.NetworkEvents;

        if (events.Count == 0)
        {
            return "No network requests recorded.";
        }

        var lines = new List<string>(events.Count);
        foreach (var e in events)
        {
            lines.Add(
                $"[{e.Status.ToString(CultureInfo.InvariantCulture)}] {e.Method} {e.Url} ({e.BodySize.ToString(CultureInfo.InvariantCulture)}B)");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// <c>browser_console_messages</c>: the active page's console calls since the last read are
    /// appended to the session's list (at most 1024, oldest dropped), as upstream does (#951,
    /// c6402a7). Nothing used to feed the list, so it always answered "No console messages."
    /// </summary>
    internal static string ConsoleMessages(BrowserState state)
    {
        state.ConsoleMessages.AddRange(state.PageMut().TakePendingConsoleMessages());
        if (state.ConsoleMessages.Count > 1024)
        {
            state.ConsoleMessages.RemoveRange(0, state.ConsoleMessages.Count - 1024);
        }

        return state.ConsoleMessages.Count == 0
            ? "No console messages."
            : string.Join("\n", state.ConsoleMessages);
    }

    internal static string Close(BrowserState state)
    {
        // Drop the one live isolate (if any) via suspend_js before clearing, so the
        // map drop disposes no isolate and the LIFO rule holds regardless of the
        // map's ascending drop order (#258).
        foreach (var page in state.Tabs.Values)
        {
            page.SuspendJs();
            page.Dispose();
        }

        state.Tabs.Clear();
        state.ActiveTab = null;
        state.ConsoleMessages.Clear();
        state.InteractiveRefs.Clear();
        return "All browser tabs closed.";
    }

    // ===== Tier 1 agent-UX additions =====

    /// <summary>
    /// Convert the rendered page to Markdown by running the JS-side converter
    /// already used by <c>obscura fetch --dump markdown</c>. More token-dense than
    /// <c>browser_snapshot</c> for content-heavy pages (article bodies, docs sites).
    /// </summary>
    internal static string Markdown(JsonNode? args, BrowserState state)
    {
        var maxChars = ClampToInt(args.Get("max_chars").AsU64()) ?? McpServer.DefaultTextLimit;
        var page = state.PageMut();
        var result = EvaluateToolScript(page, MarkdownScript.HtmlToMarkdown);
        var md = result.AsString() ?? string.Empty;
        return TextExtraction.Truncate(md, maxChars);
    }

    /// <summary>
    /// Enumerate every <c>&lt;a href&gt;</c> on the page. One JSON object per line so
    /// the agent can grep / split without round-tripping to a JSON parser.
    /// </summary>
    internal static string Links(JsonNode? args, BrowserState state)
    {
        var limit = ClampToInt(args.Get("limit").AsU64()) ?? 100;
        var internalOnly = args.Get("internal_only").AsBool() ?? false;
        var page = state.PageMut();
        var baseOrigin = TupleOrigin(page.UrlString());

        const string js = """
            (function(){
                var h = __obscura_host.dom;
                var out = [];
                var seen = h.record();
                var as = h.querySelectorAll(h.document(), 'a[href]');
                for (var i = 0; i < as.length; i++) {
                    var a = as[i];
                    var href = h.get(a, 'href') || '';
                    if (!href || href === '#' || h.slice(href, 0, 11) === 'javascript:') continue;
                    if (h.hasOwn(seen, href)) continue;
                    seen[href] = true;
                    var t = h.squash(h.get(a, 'innerText') || h.get(a, 'textContent') || '', 200);
                    out[out.length] = {__proto__: null, text: t, href: href};
                }
                return out;
            })()
            """;
        var val = EvaluateToolScript(page, js);
        var arr = val.ArrayOrNull();
        var lines = new List<string>();
        if (arr is not null)
        {
            foreach (var item in arr)
            {
                if (internalOnly)
                {
                    var href = item.Get("href").AsString();
                    var origin = href is null ? null : TupleOrigin(href);
                    if (origin is null || baseOrigin is null
                        || !string.Equals(origin, baseOrigin, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                if (lines.Count >= limit)
                {
                    break;
                }

                lines.Add(McpJson.Serialize(item));
            }
        }

        return lines.Count == 0 ? "No links found." : string.Join("\n", lines);
    }

    /// <summary>
    /// <c>url::Url::origin()</c> reduced to what a comparison needs: the tuple
    /// origin's serialization for the schemes that have one, and null for an opaque
    /// origin.
    /// </summary>
    /// <remarks>
    /// The <c>url</c> crate gives <c>data:</c>, <c>about:</c> and <c>file:</c> a
    /// fresh opaque origin per parse, and two opaque origins are never equal, so a
    /// null here must never compare equal to anything - including another null.
    /// That is why <c>internal_only</c> filters everything out on an
    /// <c>about:blank</c> page, exactly as the Rust fallback
    /// (<c>Url::parse("about:blank").origin()</c>) does.
    /// </remarks>
    private static string? TupleOrigin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme is not ("http" or "https" or "ws" or "wss" or "ftp"))
        {
            return null;
        }

        return $"{uri.Scheme}://{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// List every interactable element with a stable ref ID, the kind of element,
    /// and a one-line description. Agents pass <c>ref</c> to click/fill/type instead
    /// of crafting selectors. Also assigns <c>data-obscura-ref</c> to each element so
    /// the ref survives until the next navigation.
    /// </summary>
    internal static string InteractiveElements(JsonNode? args, BrowserState state)
    {
        var limit = (int)Math.Min(args.Get("limit").AsU64() ?? 100, int.MaxValue);
        RebuildInteractiveRefs(state);
        var context = AccessibilityContext.ForPage(state.PageMut());
        List<string> lines = context is null ? [] : InteractiveLines(state, context, limit);
        return lines.Count == 0 ? "No interactive elements on this page." : string.Join("\n", lines);
    }

    /// <summary>
    /// One line per visible ref'd control in document order: ref, tag, accessible name (or its
    /// visible text), role, the <c>type</c> / <c>name</c> attributes, the live value (never a
    /// password's) and its checked/disabled/required/expanded/selected/pressed states. Port of
    /// <c>interactive_lines</c> (upstream 3c56f59). Managed code throughout: nothing here
    /// reads a page-replaceable global (SECURITY.md L10).
    /// </summary>
    private static List<string> InteractiveLines(BrowserState state, AccessibilityContext context, int limit)
    {
        var refs = new Dictionary<uint, string>();
        foreach (var (reference, id) in state.InteractiveRefs)
        {
            refs[id.Raw] = reference;
        }

        if (refs.Count == 0 || limit <= 0)
        {
            return [];
        }

        return state.PageMut().WithDom(dom =>
        {
            var lines = new List<string>();
            foreach (var id in dom.Descendants(dom.Document))
            {
                if (lines.Count >= limit)
                {
                    break;
                }

                if (!refs.TryGetValue(id.Raw, out var reference)
                    || context.DescribeNode(dom, id) is not { } ax
                    || dom.GetNode(id) is not { } node
                    || node.AsElement() is not { } element)
                {
                    continue;
                }

                var label = ax["name"]?["value"]?.GetValue<string>()
                    ?? TextExtraction.ExtractText(dom, id, context).Trim();
                var role = ax["role"]?["value"]?.GetValue<string>() ?? "generic";
                var line = new StringBuilder();
                line.Append("ref=").Append(reference).Append(' ').Append(element.Name.Local).Append(' ')
                    .Append(TextExtraction.RustDebugString(Preview(label))).Append(" role=").Append(role);
                if (node.GetAttribute("type") is { } kind)
                {
                    line.Append(" type=").Append(TextExtraction.RustDebugString(Preview(kind)));
                }

                if (node.GetAttribute("name") is { } name)
                {
                    line.Append(" name=").Append(TextExtraction.RustDebugString(Preview(name)));
                }

                if (ax["value"]?["value"] is JsonValue axValue
                    && (axValue.TryGetValue(out string? textValue) ? textValue : axValue.ToJsonString()) is { } value)
                {
                    line.Append(" value=").Append(TextExtraction.RustDebugString(Preview(value)));
                }

                if (ax["properties"] is JsonArray properties)
                {
                    foreach (var property in properties)
                    {
                        var propertyName = property?["name"]?.GetValue<string>() ?? string.Empty;
                        if (propertyName is "checked" or "disabled" or "required" or "expanded" or "selected" or "pressed")
                        {
                            // serde_json's Display of upstream's values: true, false, or "mixed"
                            // with its quotes. The CDP tristate is now Chromium's string, so
                            // "true"/"false" print bare to keep the line format.
                            var propertyValue = property!["value"]?["value"];
                            line.Append(' ').Append(propertyName).Append('=')
                                .Append(propertyValue is JsonValue tri && tri.TryGetValue(out string? text) && text is "true" or "false"
                                    ? text
                                    : propertyValue?.ToJsonString() ?? "null");
                        }
                    }
                }

                lines.Add(line.ToString());
            }

            return lines;
        }) ?? [];

        // Rust's value.chars().take(80).
        static string Preview(string value) => TextExtraction.TakeChars(value, 80);
    }

    /// <summary>
    /// Rebuild the ref table: walk the DOM, find every interactable, assign a stable
    /// <c>data-obscura-ref="eN"</c> attribute, remember the nid for later validation.
    /// Called on every snapshot / interactive-elements call so the agent always sees
    /// fresh refs.
    /// </summary>
    internal static void RebuildInteractiveRefs(BrowserState state)
    {
        state.InteractiveRefs.Clear();
        state.RefGeneration = checked(state.RefGeneration + 1);
        var prefix = "e" + state.RefGeneration.ToString(CultureInfo.InvariantCulture) + "-";
        var page = state.PageMut();
        // Tag every interactable with data-obscura-ref in DOM order. Never reuse tokens
        // (upstream a449a80): an old snapshot must not name a different action.
        var tagJs = $$"""
            (function(){
                var sel = 'a[href], button, input:not([type=hidden]), select, textarea, [role=button], [role=link], [role=checkbox], [role=tab], [role=menuitem], [role=option], [onclick], [tabindex]:not([tabindex="-1"])';
                var h = __obscura_host.dom;
                var els = h.querySelectorAll(h.document(), sel);
                var refs = [];
                for (var i = 0; i < els.length; i++) {
                    var ref = '{{prefix}}' + (i + 1);
                    h.call(els[i], 'setAttribute', ['data-obscura-ref', ref]);
                    refs[refs.length] = ref;
                }
                return refs;
            })()
            """;
        var val = EvaluateToolScript(page, tagJs);
        var refs = new List<string>();
        if (val.ArrayOrNull() is { } array)
        {
            foreach (var entry in array)
            {
                if (entry.AsString() is { } text)
                {
                    refs.Add(text);
                }
            }
        }

        // Map ref -> nid via a second pass so ref_to_selector can sanity-check.
        foreach (var reference in refs)
        {
            var selector = $"[data-obscura-ref=\"{reference}\"]";
            var current = state.PageMut();
            var nid = current.WithDom(dom => TryQuery(dom, selector));
            if (nid is { } found)
            {
                state.InteractiveRefs[reference] = found;
            }
        }
    }

    internal static async Task<string> BackAsync(BrowserState state)
    {
        // We track simple page history on the Page itself; navigate to the entry
        // before the cursor.
        var page = state.PageMut();
        if (page.History.Count < 2 || page.HistoryIndex == 0)
        {
            throw new ToolException("No previous page in history.");
        }

        var prevIdx = page.HistoryIndex - 1;
        var url = page.History[prevIdx];
        RefuseFileNavigation(url);
        page.SetHistoryIndex(prevIdx);
        var stash = (History: new List<string>(page.History), Index: page.HistoryIndex);
        try
        {
            await page.NavigateWithWaitAsync(url, WaitUntil.DomContentLoaded).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ToolException)
        {
            throw new ToolException(error.Message);
        }

        RestoreHistory(state.PageMut(), stash.History, stash.Index);
        state.InteractiveRefs.Clear();
        return $"Back to {url}";
    }

    internal static async Task<string> ForwardAsync(BrowserState state)
    {
        var page = state.PageMut();
        if (page.HistoryIndex + 1 >= page.History.Count)
        {
            throw new ToolException("No forward page in history.");
        }

        var nextIdx = page.HistoryIndex + 1;
        var url = page.History[nextIdx];
        RefuseFileNavigation(url);
        page.SetHistoryIndex(nextIdx);
        var stash = (History: new List<string>(page.History), Index: page.HistoryIndex);
        try
        {
            await page.NavigateWithWaitAsync(url, WaitUntil.DomContentLoaded).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ToolException)
        {
            throw new ToolException(error.Message);
        }

        RestoreHistory(state.PageMut(), stash.History, stash.Index);
        state.InteractiveRefs.Clear();
        return $"Forward to {url}";
    }

    /// <summary>
    /// <c>page.history = stash.0; page.history_index = stash.1;</c>. Back / forward
    /// navigate through the normal path, which appends to history, so the stash is
    /// put back to keep the cursor where the agent moved it.
    /// </summary>
    private static void RestoreHistory(Page page, List<string> history, int index)
    {
        page.History.Clear();
        page.History.AddRange(history);
        page.HistoryIndex = index;
    }

    internal static async Task<string> ReloadAsync(BrowserState state)
    {
        var url = state.PageMut().UrlString();
        if (string.Equals(url, "about:blank", StringComparison.Ordinal))
        {
            throw new ToolException("Nothing to reload.");
        }

        RefuseFileNavigation(url);
        try
        {
            await state.PageMut().NavigateWithWaitAsync(url, WaitUntil.DomContentLoaded)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ToolException)
        {
            throw new ToolException(error.Message);
        }

        state.InteractiveRefs.Clear();
        return $"Reloaded {url}";
    }

    internal static string GetCookies(JsonNode? args, BrowserState state)
    {
        var domainFilter = args.Get("domain").AsString();
        var cookies = state.Context.CookieJar.GetAllCookies();
        var lines = new List<string>();
        foreach (var c in cookies)
        {
            if (domainFilter is not null
                && !string.Equals(c.Domain, CookieJar.CanonicalDomain(domainFilter), StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add(McpJson.Serialize(new JsonObject
            {
                ["name"] = c.Name,
                ["value"] = c.Value,
                ["domain"] = c.Domain,
                ["path"] = c.Path,
                ["secure"] = c.Secure,
                ["http_only"] = c.HttpOnly,
            }));
        }

        return lines.Count == 0 ? "No cookies." : string.Join("\n", lines);
    }

    internal static string SetCookie(JsonNode? args, BrowserState state)
    {
        var name = RequireString(args, "name", "Missing name parameter");
        var value = RequireString(args, "value", "Missing value parameter");
        var domain = RequireString(args, "domain", "Missing domain parameter");
        var path = args.Get("path").AsString() ?? "/";
        var secure = args.Get("secure").AsBool() ?? false;
        var httpOnly = args.Get("http_only").AsBool() ?? false;
        var cookie = new CookieInfo
        {
            Name = name,
            Value = value,
            Domain = domain,
            Path = path,
            Secure = secure,
            HttpOnly = httpOnly,
            SameSite = string.Empty,
            Expires = null,
        };
        state.Context.CookieJar.SetCookiesFromCdp([cookie]);
        return $"Set cookie {name} on {domain}{path}";
    }

    internal static string ClearCookies(BrowserState state)
    {
        state.Context.CookieJar.Clear();
        return "Cleared all cookies.";
    }

    internal static async Task<string> WaitForTextAsync(JsonNode? args, BrowserState state)
    {
        var needle = RequireString(args, "text", "Missing text parameter");
        var timeoutSecs = SaturatingU64(args.Get("timeout").AsF64() ?? 30.0);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSecs);
        var escaped = McpJson.String(needle);
        var js = $$"""
            (function(){
                var h = __obscura_host.dom;
                var body = h.body();
                var t = (body && (h.get(body, 'innerText') || h.get(body, 'textContent'))) || '';
                return h.indexOf(t, {{escaped}}) >= 0;
            })()
            """;
        // Exponential backoff like browser_wait_for (see comment there).
        ulong tickMs = 5;
        while (true)
        {
            var found = EvaluateToolScript(state.PageMut(), js).AsBool() ?? false;
            if (found)
            {
                return $"Found text {TextExtraction.RustDebugString(needle)}";
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new ToolException($"Timeout waiting for text {TextExtraction.RustDebugString(needle)}");
            }

            await PumpForAsync(state, tickMs).ConfigureAwait(false);
            if (tickMs < 200)
            {
                tickMs = Math.Min(tickMs * 2, 200);
            }
        }
    }
}
