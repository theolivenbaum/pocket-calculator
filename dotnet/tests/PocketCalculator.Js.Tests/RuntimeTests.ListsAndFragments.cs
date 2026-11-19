using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Upstream runtime.rs tests for 790a77d (a constructed DocumentFragment is its node's
/// wrapper), 2f302d9 (ordered-list and list-item reflection) and 24adea2 (DOMImplementation,
/// already in the port). Expected values were measured in Chromium 157.0.8103.78
/// (Playwright) on the same page and script.
/// </summary>
public sealed partial class RuntimeTests
{
    // upstream 790a77d, runtime.rs: constructed_document_fragment_keeps_its_identity_as_parent.
    [Fact]
    public void ConstructedDocumentFragmentKeepsItsIdentityAsParent() => UpstreamScript.AssertChromium(
        """
        <html><body></body></html>
        """,
        """
        (function() {
            const check = (fragment) => {
                const child = document.createElement("div");
                fragment.appendChild(child);
                const scratch = document.createElement("span");
                document.body.appendChild(scratch);
                scratch.remove();
                const parent = child.parentNode;
                let removed;
                try { fragment.removeChild(child); removed = "ok"; } catch (error) { removed = error.name; }
                return [parent === fragment, parent && parent.nodeType, removed, child.parentNode];
            };
            return {
                constructed: check(new DocumentFragment()),
                created: check(document.createDocumentFragment())
            };
        })()
        """,
        """
        {"constructed":[true,11,"ok",null],"created":[true,11,"ok",null]}
        """);

    // upstream 2f302d9, runtime.rs: list_element_brands_survive_parsing_creation_and_cloning.
    [Fact]
    public void ListElementBrandsSurviveParsingCreationAndCloning() => UpstreamScript.AssertChromium(
        """
        <html><body><ol><li>item</li></ol></body></html>
        """,
        """
        (function() {
            return [['ol', HTMLOListElement], ['li', HTMLLIElement]].map(([tag, C]) => {
                const parsed = document.querySelector(tag);
                const created = document.createElement(tag);
                const nodes = [parsed, created, parsed.cloneNode(true), created.cloneNode(),
                    document.importNode(parsed, true)];
                return [nodes.map(node => node instanceof C && node instanceof HTMLElement
                    && node.constructor === C), Object.prototype.toString.call(created),
                    [document.createElement('div'), document.createElement('ul'),
                        document.createElementNS('urn:test', tag),
                        document.createElementNS('urn:test', tag.toUpperCase()).cloneNode(),
                        document.createElementNS('http://www.w3.org/2000/svg', tag)]
                        .map(node => node instanceof C)];
            });
        })()
        """,
        """
        [[[true,true,true,true,true],"[object HTMLOListElement]",[false,false,false,false,false]],[[true,true,true,true,true],"[object HTMLLIElement]",[false,false,false,false,false]]]
        """);

    // upstream 2f302d9, runtime.rs: list_element_properties_reflect_boolean_and_string_attributes.
    [Fact]
    public void ListElementPropertiesReflectBooleanAndStringAttributes() => UpstreamScript.AssertChromium(
        """
        <html><body><ol reversed start='-2' type='A'><li value='-4'>item</li></ol></body></html>
        """,
        """
        (function() {
            const parsed = document.querySelector('ol');
            const list = document.createElement('ol');
            const initial = [parsed.reversed, parsed.start, parsed.type,
                document.querySelector('li').value, list.reversed, list.start, list.type];
            const states = [];
            list.reversed = true;
            states.push([list.reversed, list.getAttribute('reversed')]);
            list.reversed = false;
            states.push([list.reversed, list.getAttribute('reversed')]);
            list.setAttribute('reversed', 'false');
            states.push([list.reversed, list.getAttribute('reversed')]);
            list.removeAttribute('reversed');
            states.push([list.reversed, list.getAttribute('reversed')]);
            list.type = 'i';
            const written = [list.type, list.getAttribute('type')];
            list.setAttribute('type', 'invalid');
            const changed = list.type;
            list.removeAttribute('type');
            const removed = list.type;
            list.type = null;
            return [initial, states, written, changed, removed, list.getAttribute('type'),
                'reversed' in document.createElement('div'),
                'start' in document.createElement('li'),
                ['reversed', 'start', 'type'].every(name =>
                    Object.getOwnPropertyDescriptor(HTMLOListElement.prototype, name)?.enumerable),
                Object.getOwnPropertyDescriptor(HTMLLIElement.prototype, 'value')?.enumerable];
        })()
        """,
        """
        [[true,-2,"A",-4,false,1,""],[[true,""],[false,null],[true,"false"],[false,null]],["i","i"],"invalid","","null",false,false,true,true]
        """);

    // upstream 2f302d9, runtime.rs: list_element_integer_reflection_defaults_parses_and_wraps.
    [Fact]
    public void ListElementIntegerReflectionDefaultsParsesAndWraps() => UpstreamScript.AssertChromium(
        """
        <html><body></body></html>
        """,
        """
        (function() {
            const list = document.createElement('ol');
            const item = document.createElement('li');
            const attributes = [null, '', 'invalid', '  +12tail', '-3.8',
                '2147483648', '-2147483649', '99999999999999999999', '0x10'].map(value => {
                if (value === null) { list.removeAttribute('start'); item.removeAttribute('value'); }
                else { list.setAttribute('start', value); item.setAttribute('value', value); }
                return [list.start, item.value];
            });
            const setters = [4294967297, 2147483648, -2147483649, NaN, Infinity,
                -0, 3.8, null].map(value => {
                list.start = value;
                item.value = value;
                return [list.start, list.getAttribute('start'), item.value, item.getAttribute('value')];
            });
            const bigints = [[list, 'start'], [item, 'value'], [list, 'tabIndex']].map(([node, key]) => {
                try { node[key] = 1n; return 'accepted'; } catch (error) { return error.name; }
            });
            return [attributes, setters, bigints];
        })()
        """,
        """
        [[[1,0],[1,0],[1,0],[12,12],[-3,-3],[1,0],[1,0],[1,0],[0,0]],[[1,"1",1,"1"],[-2147483648,"-2147483648",-2147483648,"-2147483648"],[2147483647,"2147483647",2147483647,"2147483647"],[0,"0",0,"0"],[0,"0",0,"0"],[0,"0",0,"0"],[3,"3",3,"3"],[0,"0",0,"0"]],["TypeError","TypeError","TypeError"]]
        """);

    // upstream 24adea2, runtime.rs: document_implementation_is_a_dom_implementation_interface.
    [Fact]
    public void DocumentImplementationIsADomImplementationInterface() => UpstreamScript.AssertChromium(
        """
        <!DOCTYPE html><html><body></body></html>
        """,
        """
        (function() {
            const impl = document.implementation;
            let illegal = false;
            try { new DOMImplementation(); } catch (e) { illegal = e instanceof TypeError; }
            const doc = impl.createHTMLDocument("t");
            return [typeof DOMImplementation, impl instanceof DOMImplementation,
                impl === document.implementation, illegal,
                Object.prototype.toString.call(impl),
                Object.getOwnPropertyDescriptor(globalThis, "DOMImplementation").enumerable,
                doc.implementation instanceof DOMImplementation,
                doc.implementation !== impl,
                impl.createDocumentType("html", "", "").ownerDocument === document];
        })()
        """,
        """
        ["function",true,true,true,"[object DOMImplementation]",false,true,true,true]
        """);

    // upstream 24adea2, runtime.rs: document_adopted_stylesheets_is_configurable.
    [Fact]
    public void DocumentAdoptedStylesheetsIsConfigurable() => UpstreamScript.AssertChromium(
        """
        <html><body></body></html>
        """,
        """
        (function() {
            const desc = Object.getOwnPropertyDescriptor(Document.prototype, "adoptedStyleSheets");
            Object.defineProperty(Document.prototype, "adoptedStyleSheets", {
                get: desc.get, set: desc.set, configurable: true,
            });
            return [desc.configurable, Array.isArray(document.adoptedStyleSheets)];
        })()
        """,
        """
        [true,true]
        """);
}
