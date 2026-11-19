using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Upstream 8310ebf: a select's options are reused while the document is unmutated, and
/// <c>select.options</c> is one live HTMLOptionsCollection. Expected values measured in Chromium
/// (playwright chromium) with the same markup.
/// </summary>
public sealed partial class RuntimeTests
{
    private const string SelectPage =
        "<html><body><select id='s'><option>A</option><optgroup><option>B</option></optgroup></select>"
        + "<select id='other'></select></body></html>";

    // Rust select_length_snapshot_stays_live_and_private. Upstream expects
    // `select.options.length = 0` to leave the select alone (2, then 1 and 1 after the move);
    // Chromium's options is an HTMLOptionsCollection whose length setter truncates the select.
    [Fact]
    public void SelectLengthSnapshotStaysLive()
    {
        using var fixture = RuntimeFixture.Setup(SelectPage);
        AssertJsonEquals(
            """[2, 2, 0, 0, 0, 1, 1, 1, 2, "F", 0, true, 1, "detached", true, true]""",
            fixture.Runtime.Evaluate(
                """
                (() => {
                    const select = document.getElementById('s');
                    const other = document.getElementById('other');
                    const counts = [select.length, select.length];
                    const exposed = select.options;
                    exposed.length = 0;
                    counts.push(select.length);
                    other.append(select.item(0));
                    counts.push(select.length, other.length);
                    const group = select.querySelector('optgroup');
                    group.append(new Option('C'));
                    counts.push(select.length);
                    group.replaceWith(new Option('D'));
                    counts.push(select.length);
                    const nested = document.createElement('select');
                    nested.append(new Option('hidden'));
                    select.append(nested);
                    const datalist = document.createElement('datalist');
                    datalist.append(new Option('suggested'));
                    select.append(datalist);
                    counts.push(select.length);
                    select.innerHTML = '<option>E</option><option>F</option>';
                    counts.push(select.length, select[1].text);
                    select.length = 0;
                    counts.push(select.length, select[0] === undefined);
                    select.remove();
                    select.append(new Option('detached'));
                    counts.push(select.length, select[0].text, select.options === select.options,
                        exposed === select.options);
                    return counts; })()
                """));
    }

    [Fact]
    public void SelectOptionsIsALiveHtmlOptionsCollection()
    {
        using var fixture = RuntimeFixture.Setup(SelectPage);
        AssertJsonEquals(
            """
            ["[object HTMLOptionsCollection]", true, "function", 2, 0, 3, "z", 2, "z", 6, "Y", "", 5,
             null, 1, 1, "TypeError", "add,constructor,length,remove,selectedIndex"]
            """,
            fixture.Runtime.Evaluate(
                """
                (() => { const s = document.getElementById('s'); const o = s.options;
                    const r = [Object.prototype.toString.call(o), o instanceof HTMLCollection,
                        typeof HTMLOptionsCollection, o.length, o.selectedIndex];
                    o.add(new Option('Z', 'z')); r.push(o.length, s[o.length - 1].value);
                    o.selectedIndex = 2; r.push(s.selectedIndex, s.value);
                    o[5] = new Option('Y'); r.push(o.length, o.item(5).text, o[3] && o[3].text);
                    o.remove(0); r.push(o.length, o.namedItem('nope'));
                    o.length = 1; r.push(s.length, [...o].length);
                    try { new HTMLOptionsCollection(); } catch (e) { r.push(e.constructor.name); }
                    r.push(Object.getOwnPropertyNames(HTMLOptionsCollection.prototype).sort().join());
                    return r; })()
                """));
    }
}
