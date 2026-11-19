using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-js/tests/window_event.rs</c> (upstream 414e649). The
/// port already had window.event (<c>_currentEvent</c>); these pin it. Expected values were
/// measured in Chromium 157.0.8103.78 (Playwright) on the same page and script.
/// </summary>
public sealed class WindowEvent
{
    // upstream 414e649, window_event.rs: global_event_is_visible_to_functions_called_from_inline_handlers_and_listeners.
    [Fact]
    public void GlobalEventIsVisibleToFunctionsCalledFromInlineHandlersAndListeners() => UpstreamScript.AssertChromium(
        """
        <body><button id="b" onclick="probe()">x</button></body>
        """,
        """
        (() => {
            const out = {};
            globalThis.probe = function () {
                out.free = [typeof event, event.type, event === window.event];
            };
            const b = document.getElementById('b');
            b.addEventListener('click', e => { out.listener = window.event === e; });
            b.click();
            out.after = typeof window.event;
            return out;
        })()
        """,
        """
        {"free":["object","click",true],"listener":true,"after":"undefined"}
        """);

    // upstream 414e649, window_event.rs: nested_dispatch_restores_the_outer_event.
    [Fact]
    public void NestedDispatchRestoresTheOuterEvent() => UpstreamScript.AssertChromium(
        """
        <body><button id="b" onclick="probe()">x</button></body>
        """,
        """
        (() => {
            const b = document.getElementById('b'), seen = [];
            b.addEventListener('ping', () => {
                seen.push(window.event.type);
                b.dispatchEvent(new Event('pong'));
                seen.push(window.event.type);
            });
            b.addEventListener('pong', () => seen.push(window.event.type));
            b.dispatchEvent(new Event('ping'));
            seen.push(typeof window.event);
            return seen;
        })()
        """,
        """
        ["ping","pong","ping","undefined"]
        """);

    // upstream 414e649, window_event.rs: event_is_restored_when_a_listener_throws.
    [Fact]
    public void EventIsRestoredWhenAListenerThrows() => UpstreamScript.AssertChromium(
        """
        <body><button id="b" onclick="probe()">x</button></body>
        """,
        """
        (() => {
            const b = document.getElementById('b');
            let during;
            b.addEventListener('boom', () => {
                during = typeof window.event;
                throw new Error('listener failure');
            });
            b.dispatchEvent(new Event('boom'));
            return [during, typeof window.event];
        })()
        """,
        """
        ["object","undefined"]
        """);

    // upstream 414e649, window_event.rs: window_event_is_replaceable.
    [Fact]
    public void WindowEventIsReplaceable() => UpstreamScript.AssertChromium(
        """
        <body><button id="b" onclick="probe()">x</button></body>
        """,
        """
        (() => {
            const b = document.getElementById('b');
            let during;
            b.addEventListener('x', () => { during = [typeof window.event, window.event === 7]; });
            b.dispatchEvent(new Event('x'));
            const before = during;
            window.event = 7;
            b.dispatchEvent(new Event('x'));
            return [before, during, window.event];
        })()
        """,
        """
        [["object",false],["number",true],7]
        """);

    // upstream 414e649, window_event.rs: event_is_not_exposed_to_listeners_inside_a_shadow_tree.
    [Fact]
    public void EventIsNotExposedToListenersInsideAShadowTree() => UpstreamScript.AssertChromium(
        """
        <body><button id="b" onclick="probe()">x</button></body>
        """,
        """
        (() => {
            const host = document.createElement('div');
            document.body.appendChild(host);
            const root = host.attachShadow({ mode: 'open' });
            const inner = document.createElement('span');
            root.appendChild(inner);
            const seen = {};
            inner.addEventListener('x', () => { seen.shadow = typeof window.event; });
            host.addEventListener('x', () => { seen.light = typeof window.event; });
            inner.dispatchEvent(new Event('x'));
            host.dispatchEvent(new Event('x'));
            return seen;
        })()
        """,
        """
        {"shadow":"undefined","light":"object"}
        """);
}
