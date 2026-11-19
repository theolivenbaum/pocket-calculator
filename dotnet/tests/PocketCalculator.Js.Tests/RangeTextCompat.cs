using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-js/tests/range_text_compat.rs</c> (upstream 7451e39 and
/// 48538d6), plus a port check of selectionDirection and setSelectionRange clamping. Expected
/// values were measured in Chromium 157.0.8103.78 on Linux (Playwright) on the same page and script.
/// </summary>
public sealed class RangeTextCompat
{
    // upstream 7451e39, range_text_compat.rs: set_range_text_matches_chromium_for_empty_and_nonempty_controls.
    [Fact]
    public void SetRangeTextMatchesChromiumForEmptyAndNonemptyControls() => UpstreamScript.AssertChromium(
        """
        <textarea id="t"></textarea><input id="i"><input id="n" type="number">
        """,
        """
        (() => {
            const t = document.getElementById('t');
            const out = {};
            const run = (name, initial, selection, replacement, args) => {
                t.value = initial;
                t.setSelectionRange(...selection);
                try {
                    t.setRangeText(replacement, ...args);
                    out[name] = [t.value, t.selectionStart, t.selectionEnd, t.selectionDirection];
                } catch (e) { out[name] = e.name; }
            };
            run('empty', '', [0, 0], 'a', []);
            run('before', 'abcdef', [0, 0], 'XYZ', [1, 3]);
            run('overlap', 'abcdef', [1, 2], 'XYZ', [1, 3]);
            run('after', 'abcdef', [3, 5], 'XYZ', [1, 3]);
            run('preserve', 'abcdef', [1, 5], 'XYZ', [2, 4, 'preserve']);
            run('undefinedMode', 'abcdef', [1, 5], 'XYZ', [2, 4, undefined]);
            run('select', 'abcdef', [0, 0], 'X', [1, 3, 'select']);
            run('start', 'abcdef', [0, 0], 'X', [1, 3, 'start']);
            run('end', 'abcdef', [0, 0], 'X', [1, 3, 'end']);
            run('invalid', 'abcdef', [0, 0], 'X', [1, 3, 'invalid']);
            run('nullMode', 'abcdef', [0, 0], 'X', [1, 3, null]);
            run('reversed', 'abcdef', [0, 0], 'X', [5, 1]);
            run('beyond', 'abc', [0, 0], 'X', [6, 9]);
            const i = document.getElementById('i');
            i.value = 'abc';
            out.inputAssignment = [i.selectionStart, i.selectionEnd];
            i.setRangeText('X', 1, 2);
            out.input = [i.value, i.selectionStart, i.selectionEnd];
            i.setRangeText('Y', 0, 1, undefined);
            out.inputUndefinedMode = [i.value, i.selectionStart, i.selectionEnd];
            out.fallbackInputs = [
                'future-state', ' text ', 'constructor', '__proto__', 'datetime', 'TeXt',
                'search', 'tel', 'url', 'password', '',
            ].map(type => {
                const input = document.createElement('input');
                input.setAttribute('type', type);
                input.value = 'abc';
                const assigned = [input.selectionStart, input.selectionEnd];
                try {
                    input.setRangeText('X', 0, 1);
                    return [...assigned, input.value, input.selectionStart, input.selectionEnd];
                } catch (e) { return e.name; }
            });
            out.unsupportedInputs = [
                'hidden', 'email', 'date', 'month', 'week', 'time', 'datetime-local',
                'NuMbEr', 'range', 'color', 'checkbox', 'radio', 'file',
                'submit', 'image', 'reset', 'button',
            ].map(type => {
                const input = document.createElement('input');
                input.setAttribute('type', type);
                try { input.setRangeText('X', 0, 0); }
                catch (e) { return e.name; }
                return 'no exception';
            });
            try { document.getElementById('n').setRangeText('X', 1, 2); }
            catch (e) { out.number = e.name; }
            return out;
        })()
        """,
        """
        {"empty":["a",0,0,"forward"],"before":["aXYZdef",0,0,"forward"],"overlap":["aXYZdef",1,4,"forward"],"after":["aXYZdef",1,6,"forward"],"preserve":["abXYZef",1,6,"forward"],"undefinedMode":["abXYZef",1,6,"forward"],"select":["aXdef",1,2,"forward"],"start":["aXdef",1,1,"forward"],"end":["aXdef",2,2,"forward"],"invalid":"TypeError","nullMode":"TypeError","reversed":"IndexSizeError","beyond":["abcX",0,0,"forward"],"inputAssignment":[3,3],"input":["aXc",3,3],"inputUndefinedMode":["YXc",3,3],"fallbackInputs":[[3,3,"Xbc",3,3],[3,3,"Xbc",3,3],[3,3,"Xbc",3,3],[3,3,"Xbc",3,3],[3,3,"Xbc",3,3],[3,3,"Xbc",3,3],[3,3,"Xbc",3,3],[3,3,"Xbc",3,3],[3,3,"Xbc",3,3],[3,3,"Xbc",3,3],[3,3,"Xbc",3,3]],"unsupportedInputs":["InvalidStateError","InvalidStateError","InvalidStateError","InvalidStateError","InvalidStateError","InvalidStateError","InvalidStateError","InvalidStateError","InvalidStateError","InvalidStateError","InvalidStateError","InvalidStateError","InvalidStateError","InvalidStateError","InvalidStateError","InvalidStateError","InvalidStateError"],"number":"InvalidStateError"}
        """);

    // upstream 48538d6, range_text_compat.rs: value_assignment_preserves_unchanged_selection_without_calling_page_methods.
    [Fact]
    public void ValueAssignmentPreservesUnchangedSelectionWithoutCallingPageMethods() => UpstreamScript.AssertChromium(
        """
        <input id='i' value='abc'><textarea id='t'>abc</textarea>
        """,
        """
        (() => {
            return ['i', 't'].map(id => {
                const element = document.getElementById(id);
                element.value = 'abc';
                const initial = [element.selectionStart, element.selectionEnd];
                element.value = '';
                const empty = [element.selectionStart, element.selectionEnd];
                element.value = 'abc';
                element.setSelectionRange(1, 2, 'backward');
                element.value = 'abc';
                const unchanged = [element.selectionStart, element.selectionEnd, element.selectionDirection];
                element.setSelectionRange = () => { throw new Error('page method called'); };
                let error = null;
                try { element.value = 'abcd'; } catch (e) { error = e.message; }
                return [initial, empty, unchanged, element.value, element.selectionStart, element.selectionEnd, error];
            });
        })()
        """,
        """
        [[[0,0],[0,0],[1,2,"backward"],"abcd",4,4,null],[[0,0],[0,0],[1,2,"backward"],"abcd",4,4,null]]
        """);

    // upstream 48538d6, range_text_compat.rs: value_assignment_does_not_read_page_attribute_or_text_content_getters.
    [Fact]
    public void ValueAssignmentDoesNotReadPageAttributeOrTextContentGetters() => UpstreamScript.AssertChromium(
        """
        <body></body>
        """,
        """
        (() => {
            return ['input', 'textarea'].map(tag => {
                const element = document.createElement(tag);
                if (tag === 'textarea') {
                    const descriptor = Object.getOwnPropertyDescriptor(Node.prototype, 'textContent');
                    Object.defineProperty(element, 'textContent', {
                        get() { throw new Error('page getter called'); }, set: descriptor.set
                    });
                } else {
                    const getAttribute = element.getAttribute;
                    element.getAttribute = function(name) {
                        if (name === 'value') throw new Error('page getter called');
                        return getAttribute.call(this, name);
                    };
                }
                try { element.value = 'abc'; return element.value; }
                catch (e) { return e.message; }
            });
        })()
        """,
        """
        ["abc","abc"]
        """);

    // upstream 48538d6, range_text_compat.rs: set_range_text_converts_unsigned_offsets_before_clamping_to_value_length.
    [Fact]
    public void SetRangeTextConvertsUnsignedOffsetsBeforeClampingToValueLength() => UpstreamScript.AssertChromium(
        """
        <input id='i'>
        """,
        """
        (() => {
            const input = document.getElementById('i');
            return [Infinity, -Infinity, NaN, 4294967296, 4294967297, -1, -1.5, 0.9, '0x1', 1n].map(offset => {
                input.value = 'abc';
                try { input.setRangeText('X', offset, offset); return input.value; }
                catch (e) { return e.name; }
            });
        })()
        """,
        """
        ["Xabc","Xabc","Xabc","Xabc","aXbc","abcX","abcX","Xabc","aXbc","TypeError"]
        """);

    // port check (Chromium 157, Linux): selection_direction_and_range_clamping_match_chromium.
    [Fact]
    public void SelectionDirectionAndRangeClampingMatchChromium() => UpstreamScript.AssertChromium(
        """
        <html><body></body></html>
        """,
        """
        (() => {
            const i = document.createElement('input'); i.value = 'abcdef'; document.body.append(i);
            const t = document.createElement('textarea'); t.value = 'abcdef';
            const out = [i.selectionDirection, t.selectionDirection];
            i.setSelectionRange(1, 2); out.push(i.selectionDirection);
            i.setSelectionRange(1, 2, 'none'); out.push(i.selectionDirection);
            i.setSelectionRange(1, 2, 'bogus'); out.push(i.selectionDirection);
            i.value = 'xyz'; out.push(i.selectionDirection);
            i.selectionDirection = 'none'; out.push(i.selectionDirection);
            i.setSelectionRange(5, 1); out.push([i.selectionStart, i.selectionEnd]);
            i.setSelectionRange(-1, 99); out.push([i.selectionStart, i.selectionEnd]);
            i.select(); out.push(i.selectionDirection);
            return out;
        })()
        """,
        """
        ["forward","forward","forward","forward","forward","forward","forward",[1,1],[3,3],"forward"]
        """);
}
