using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Upstream d88df74 (input_activation_author_accessors.rs): native activation toggles a
/// checkbox or radio without calling author-replaced <c>checked</c>/<c>indeterminate</c>
/// accessors. Chromium 1243 answers <c>[[],true,false,true,false]</c> for this probe.
/// </summary>
public sealed class InputActivationAuthorAccessors
{
    [Fact]
    public void Native_activation_bypasses_author_accessors()
    {
        using var fixture = RuntimeFixture.Setup(
            "<html><body><input type=checkbox id=c><input type=radio name=g id=r1 checked><input type=radio name=g id=r2><input type=checkbox id=x></body></html>");
        string result = fixture.Runtime.Evaluate(
            """
            (() => {
              const log = [];
              const proto = HTMLInputElement.prototype;
              const cd = Object.getOwnPropertyDescriptor(proto, 'checked'), id = Object.getOwnPropertyDescriptor(proto, 'indeterminate');
              Object.defineProperty(proto, 'checked', { configurable: true, get() { log.push('get:' + this.id); return cd.get.call(this); }, set(v) { log.push('set:' + this.id); cd.set.call(this, v); } });
              Object.defineProperty(proto, 'indeterminate', { configurable: true, get() { log.push('iget:' + this.id); return id.get.call(this); }, set(v) { log.push('iset:' + this.id); id.set.call(this, v); } });
              const c = document.getElementById('c'), r2 = document.getElementById('r2'), x = document.getElementById('x');
              c.click(); r2.click();
              x.addEventListener('click', e => e.preventDefault()); x.click();
              Object.defineProperty(proto, 'checked', cd); Object.defineProperty(proto, 'indeterminate', id);
              return JSON.stringify([log, c.checked, document.getElementById('r1').checked, r2.checked, x.checked]);
            })()
            """)?.ToString() ?? "null";
        Assert.Equal("[[],true,false,true,false]", result);
    }
}
