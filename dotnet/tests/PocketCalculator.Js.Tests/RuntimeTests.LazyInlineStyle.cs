using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Upstream 3a9eb06: an element's inline style declaration is made on first use. Expected
/// values measured in Chromium (playwright chromium). Upstream's frame test (a borrowed style
/// getter across realms) does not apply: frames run in separate engines here.
/// </summary>
public sealed partial class RuntimeTests
{
    // Rust element_style_is_not_replaced_by_a_page_expando.
    [Fact]
    public void ElementStyleIsNotReplacedByAPageExpando()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><div id="d" style="color: red">hi</div></body></html>""");
        AssertJsonEquals("""[true, "red", true, [1, "string"]]""", fixture.Runtime.Evaluate(
            """
            (() => {
                const element = document.getElementById('d');
                element._style = { color: 'poisoned' };
                const style = element.style;
                const strict = (() => { 'use strict'; const e = document.createElement('p');
                    e._style = 1; return [e._style, typeof e.style.color]; })();
                return [style instanceof CSSStyleDeclaration, style.color, style === element.style, strict];
            })()
            """));
    }

    // Rust first_element_style_access_ignores_replaced_page_intrinsics.
    [Fact]
    public void FirstElementStyleAccessIgnoresReplacedPageIntrinsics()
    {
        using var fixture = RuntimeFixture.Setup("<html><body>Style</body></html>");
        AssertJsonEquals("[true, true]", fixture.Runtime.Evaluate(
            """
            (() => {
                const element = document.body;
                const original = [globalThis.Proxy, Object.defineProperty,
                    WeakMap.prototype.get, WeakMap.prototype.set, Reflect.apply];
                const fail = () => { throw new Error('page intrinsic called'); };
                let style;
                try {
                    globalThis.Proxy = fail;
                    Object.defineProperty = fail;
                    WeakMap.prototype.get = fail;
                    WeakMap.prototype.set = fail;
                    Reflect.apply = fail;
                    style = element.style;
                } finally {
                    [globalThis.Proxy, Object.defineProperty, WeakMap.prototype.get,
                        WeakMap.prototype.set, Reflect.apply] = original;
                }
                return [style instanceof CSSStyleDeclaration, style === element.style];
            })()
            """));
    }

    // Rust element_style_first_read_observes_prior_attribute_mutations.
    [Fact]
    public void ElementStyleFirstReadObservesPriorAttributeMutations()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        AssertJsonEquals("""["blue", "green", "", true, "color: purple;"]""", fixture.Runtime.Evaluate(
            """
            (() => {
                const element = document.createElement('div');
                element.setAttribute('style', 'color: red');
                element.removeAttributeNS(null, 'style');
                element.setAttributeNS(null, 'style', 'color: blue');
                const style = element.style;
                const before = style.color;
                element.style = 'color: green';
                const assigned = style.color;
                element.removeAttribute('style');
                const removed = style.color;
                style.color = 'purple';
                return [before, assigned, removed, style === element.style,
                    element.getAttribute('style')];
            })()
            """));
    }
}
