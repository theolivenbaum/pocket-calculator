using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-js/tests/html_interface_brands.rs</c> (upstream 831cb29)
/// and of ccdaf10's runtime.rs brand tests. Expected values were measured in Chromium
/// 157.0.8103.78 (Playwright) on the same page and script. ccdaf10's child-realm test is not
/// ported: the port's frames run in separate engines.
/// </summary>
public sealed class HtmlInterfaceBrands
{
    // upstream 831cb29, html_interface_brands.rs: html_interface_brands_follow_tag_namespace_and_real_prototypes.
    [Fact]
    public void HtmlInterfaceBrandsFollowTagNamespaceAndRealPrototypes() => UpstreamScript.AssertChromium(
        """
        <!doctype html><head><base href=/></head><body><table><tbody><tr><td>cell</td></tr></tbody></table></body>
        """,
        """
        (() => {
            const pairs = [['base','HTMLBaseElement'],['td','HTMLTableCellElement'],['th','HTMLTableCellElement'],
              ['tbody','HTMLTableSectionElement'],['thead','HTMLTableSectionElement'],['tfoot','HTMLTableSectionElement'],
              ['tr','HTMLTableRowElement'],['col','HTMLTableColElement'],['colgroup','HTMLTableColElement'],
              ['picture','HTMLPictureElement'],['source','HTMLSourceElement'],['title','HTMLTitleElement'],['time','HTMLTimeElement']];
            const failures=[];
            for (const [tag,name] of pairs) {
              const C=globalThis[name],element=document.createElement(tag),unrelated=document.createElement(tag==='div'?'span':'div');
              if(typeof C!=='function'||!(element instanceof C)||unrelated instanceof C||Object.getPrototypeOf(element)!==C.prototype
                 ||Object.getPrototypeOf(C.prototype)!==HTMLElement.prototype||Object.prototype.toString.call(element)!=='[object '+name+']') failures.push(name);
              if(!(element.cloneNode() instanceof C)) failures.push('clone '+name);
              if(document.createElementNS('urn:other',tag) instanceof C) failures.push('namespace '+name);
              const prefixed=document.createElementNS('http://www.w3.org/1999/xhtml','x:'+tag);
              for(const node of [prefixed,prefixed.cloneNode()]) {
                if(!(node instanceof C)||node.localName!==tag||node.prefix!=='x'||node.tagName!==('x:'+tag).toUpperCase()) failures.push('prefix '+name);
              }
              const literal=document.createElement('x:'+tag);
              for(const node of [literal,literal.cloneNode()]) {
                if(node instanceof C||node.localName!=='x:'+tag||node.prefix!==null||node.tagName!==('x:'+tag).toUpperCase()) failures.push('literal colon '+name);
              }
              const upper=document.createElementNS('http://www.w3.org/1999/xhtml',tag.toUpperCase());
              if(upper instanceof C || upper.localName!==tag.toUpperCase() || upper.cloneNode() instanceof C) failures.push('case '+name);
              let illegal=false;try{new C();}catch(e){illegal=e instanceof TypeError;}if(!illegal)failures.push('constructor '+name);
              if(Object.getOwnPropertyDescriptor(globalThis,name).enumerable)failures.push('enumerable '+name);
            }
            const cell=document.querySelector('td');cell.id='brand-cache-cell';cell.marker='retained';
            const sameWrapper=document.getElementById('brand-cache-cell')===cell
              &&cell.parentNode.childNodes[0]===cell&&document.querySelector('td').marker==='retained';
            return [failures,sameWrapper,document.querySelector('base') instanceof HTMLBaseElement,
              document.querySelector('td') instanceof HTMLTableCellElement,HTMLBaseElement!==HTMLTableCellElement,
              HTMLBaseElement.prototype!==HTMLTableCellElement.prototype];
        })()
        """,
        """
        [[],true,true,true,true,true]
        """);

    // upstream ccdaf10, runtime.rs: html_interface_brands_use_distinct_native_prototypes.
    [Fact]
    public void HtmlInterfaceBrandsUseDistinctNativePrototypes() => UpstreamScript.AssertChromium(
        """
        <html><head><style></style><link></head><body><div></div><span></span><a></a><p></p><button></button><h2></h2><template></template></body></html>
        """,
        """
        (() => {
            const pairs = [
                ['a','HTMLAnchorElement'], ['br','HTMLBRElement'], ['body','HTMLBodyElement'],
                ['button','HTMLButtonElement'], ['datalist','HTMLDataListElement'], ['details','HTMLDetailsElement'],
                ['dialog','HTMLDialogElement'], ['div','HTMLDivElement'], ['fieldset','HTMLFieldSetElement'],
                ['hr','HTMLHRElement'], ['head','HTMLHeadElement'], ['h2','HTMLHeadingElement'],
                ['html','HTMLHtmlElement'], ['label','HTMLLabelElement'], ['legend','HTMLLegendElement'],
                ['link','HTMLLinkElement'], ['p','HTMLParagraphElement'], ['pre','HTMLPreElement'],
                ['progress','HTMLProgressElement'], ['script','HTMLScriptElement'], ['span','HTMLSpanElement'],
                ['style','HTMLStyleElement'], ['template','HTMLTemplateElement'], ['ul','HTMLUListElement'],
            ];
            const xhtml = 'http://www.w3.org/1999/xhtml';
            const nodes = pairs.map(([tag]) => document.createElement(tag));
            const correct = pairs.map(([tag,name], index) => {
                const C = globalThis[name];
                const node = nodes[index];
                let rejected = false;
                try { new C(); } catch (error) { rejected = error instanceof TypeError; }
                return node instanceof C && node.constructor === C
                    && Function.prototype[Symbol.hasInstance].call(C, node)
                    && Object.getPrototypeOf(node) === C.prototype
                    && Object.prototype.toString.call(node) === '[object '+name+']'
                    && C.length === 0 && rejected
                    && node.cloneNode().constructor === C
                    && document.createElementNS(xhtml, 'x:'+tag).constructor === C
                    && !(document.createElementNS(xhtml, tag.toUpperCase()) instanceof C)
                    && !(document.createElementNS('urn:foreign', tag) instanceof C)
                    && nodes.every((other, otherIndex) => (other instanceof C) === (index === otherIndex));
            });
            const parsed = pairs.filter(([tag]) => document.querySelector(tag)).every(([tag,name]) => {
                const node = document.querySelector(tag);
                return node.constructor === globalThis[name]
                    && Object.getPrototypeOf(node) === globalThis[name].prototype;
            });
            HTMLDivElement.prototype.__brandProbe = 42;
            const isolated = document.querySelector('div').__brandProbe === 42
                && document.createElement('div').__brandProbe === 42
                && document.querySelector('span').__brandProbe === undefined
                && document.querySelector('a').__brandProbe === undefined
                && Element.prototype.__brandProbe === undefined;
            delete HTMLDivElement.prototype.__brandProbe;
            return {correct, parsed, isolated};
        })()
        """,
        """
        {"correct":[true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true],"parsed":true,"isolated":true}
        """);

    // upstream ccdaf10, runtime.rs: html_interface_brands_preserve_existing_specialized_behavior.
    [Fact]
    public void HtmlInterfaceBrandsPreserveExistingSpecializedBehavior() => UpstreamScript.AssertChromium(
        """
        <html><head></head><body><input><select><option>one</option></select><textarea></textarea><form></form><iframe></iframe><div></div></body></html>
        """,
        """
        (() => {
            const input = document.querySelector('input');
            input.value = 'typed';
            const select = document.querySelector('select');
            const textarea = document.querySelector('textarea');
            textarea.value = 'text';
            const style = document.createElement('style');
            style.textContent = 'body {color: red}';
            let target = document.head;
            if (target instanceof HTMLIFrameElement) target = target.contentDocument.head;
            target.appendChild(style);
            return [input instanceof HTMLInputElement, input.value,
                select instanceof HTMLSelectElement, select.item(0).index,
                textarea instanceof HTMLTextAreaElement, textarea.value,
                document.querySelector('form') instanceof HTMLFormElement,
                document.querySelector('iframe') instanceof HTMLIFrameElement,
                !(document.querySelector('div') instanceof HTMLIFrameElement),
                style.parentNode === document.head, style.constructor === HTMLStyleElement];
        })()
        """,
        """
        [true,"typed",true,0,true,"text",true,true,true,true,true]
        """);
}
