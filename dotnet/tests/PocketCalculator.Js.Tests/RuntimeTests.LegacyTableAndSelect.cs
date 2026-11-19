using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The legacy table and select APIs: upstream d8f1467 (table.rows), 25700b9 (select option
/// ownership and the select legacy methods), 59276a3 (table collections, indices, insert and
/// delete, select indexing) and merge 4c12826 (colSpan/rowSpan), from crates/obscura-js/src/
/// runtime.rs. Every expected value was measured in Chromium 157.0.8103.78 (Playwright)
/// against the same page and script; where the port deliberately differs, the test says so.
/// Upstream's child-realm variants are not ported: the port's frames run in separate engines.
/// </summary>
public sealed partial class RuntimeTests
{
    private static void AssertLegacyApis(string html, string script, string expected)
    {
        using var fixture = RuntimeFixture.Setup(html);
        var actual = fixture.Runtime.Evaluate("JSON.stringify(" + script.Trim() + ")")!.GetValue<string>();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), actual);
    }

    // Deviation: Chromium 157 answers true for Reflect.set and Reflect.defineProperty of an index (writes [true,false,true,false]) yet throws on a strict-mode assignment; a Proxy cannot do both, so the port answers false, as upstream and WebIDL do. The own-key order (expandos before names) is Chromium's; upstream lists names first.
    [Fact]
    public void TableRowsAreLiveOrderedReadonlyCollections() => AssertLegacyApis(
        """
        <html><body></body></html>
        """,
        """
        (() => {
            const table = document.createElement('table');
            document.body.append(table);
            for (const [section, id] of [['tfoot','foot'],['tbody','body'],['thead','head']]) {
                const group = document.createElement(section);
                group.innerHTML = '<tr id="'+id+'"><td></td></tr>';
                table.append(group);
            }
            const direct = document.createElement('tr');
            direct.id = 'direct';
            table.append(direct);
            table.querySelector('td').innerHTML = '<table><tr id="nested"><td></td></tr></table>';
            table.querySelector('thead').append(document.createElementNS('urn:foreign', 'tr'));
            const rows = table.rows;
            const initial = Array.from(rows, row => row.id);
            const collection = [rows === table.rows, rows instanceof HTMLCollection,
                Array.isArray(rows), Object.prototype.toString.call(rows),
                rows.item(1).id, rows.namedItem('head').id, rows.head.id,
                Object.keys(rows), 'head' in rows, rows.item(99)];
            const writes = [Reflect.set(rows, '0', null), Reflect.set(rows, 'length', 0),
                Reflect.defineProperty(rows, '1', {value:null}), Reflect.deleteProperty(rows, '0')];
            rows.custom = 'expando';
            const reflection = [Object.keys(rows), Object.getOwnPropertyNames(rows),
                Reflect.deleteProperty(rows, '99')];
            Object.defineProperty(rows, Symbol.toStringTag, {value:'CustomCollection', configurable:false});
            reflection.push(Object.prototype.toString.call(rows));
            table.querySelector('thead').textContent = '';
            direct.id = 'renamed';
            direct.setAttribute('name', 'alias');
            const changed = [rows.length, rows[0].id, rows.renamed === direct,
                rows.alias === direct, 'head' in rows, rows.head === undefined];
            direct.id = '99';
            const numericName = [Object.getOwnPropertyNames(rows).includes('99'),
                rows[99] === undefined, rows.namedItem('99') === direct,
                Reflect.deleteProperty(rows, '99')];
            table.textContent = '';
            return {initial, collection, writes, reflection, changed, numericName, empty:rows.length};
        })()
        """,
        """
        {"initial":["head","body","direct","foot"],"collection":[true,true,false,"[object HTMLCollection]","body","head","head",["0","1","2","3"],true,null],"writes":[false,false,false,false],"reflection":[["0","1","2","3","custom"],["0","1","2","3","custom","head","body","direct","foot"],true,"[object CustomCollection]"],"changed":[3,"body",true,true,false,true],"numericName":[true,true,true,true],"empty":0}
        """);

    [Fact]
    public void TableRowsPreserveFactoryBrandsAndRejectNonTableReceivers() => AssertLegacyApis(
        """
        <html><body><table><tr><th>Header</th></tr></table><h1>Heading</h1></body></html>
        """,
        """
        (() => {
            const parsed = document.querySelector('table');
            const created = document.createElement('table');
            const cloned = parsed.cloneNode(true);
            const getter = Object.getOwnPropertyDescriptor(HTMLTableElement.prototype, 'rows').get;
            const invalid = [document.createElement('div'), {},
                document.createElementNS('urn:foreign', 'table')].map(value => {
                try { getter.call(value); return false; } catch (error) { return error instanceof TypeError; }
            });
            let illegalConstructor = false;
            try { new HTMLTableElement(); } catch (error) { illegalConstructor = error instanceof TypeError; }
            return [parsed instanceof HTMLTableElement, created instanceof HTMLTableElement,
                cloned instanceof HTMLTableElement, cloned.rows.length, parsed.rows.length,
                document.querySelector('h1') instanceof HTMLTableElement, invalid, illegalConstructor,
                HTMLTableElement.length, getter.name, getter.toString()];
        })()
        """,
        """
        [true,true,true,1,1,false,[true,true,true],true,0,"get rows","function get rows() { [native code] }"]
        """);

    [Fact]
    public void SelectOptionsUseNativeOwnershipAndLiveOptionIndexes() => AssertLegacyApis(
        """
        <html><body></body></html>
        """,
        """
        (() => {
            const select = document.createElement('select');
            document.body.append(select);
            const add = (parent, id) => {
                const option = new Option(id, id);
                option.id = id;
                parent.append(option);
                return option;
            };
            const first = add(select, 'first');
            const div = document.createElement('div');
            select.append(div);
            const second = add(div, 'second');
            const group = document.createElement('optgroup');
            select.append(group);
            const third = add(group, 'third');
            const nested = document.createElement('select');
            select.append(nested);
            const hidden = add(nested, 'nested');
            hidden.selected = true;
            const datalist = document.createElement('datalist');
            select.append(datalist);
            const suggested = add(datalist, 'suggested');
            const outer = add(select, 'outer');
            const inner = add(outer, 'inner');
            const initial = [select.length, Array.from(select.options, o => o.id),
                [first.index, second.index, third.index, hidden.index, suggested.index, outer.index, inner.index],
                select.value, select.selectedIndex, select.item(2) === third,
                select.namedItem('second') === second];
            select.value = 'third';
            const chosen = [third.selected, hidden.selected, select.value, select.selectedIndex];
            second.remove();
            return {initial, chosen, moved: [third.index, select.length], liveRows:
                typeof Object.getOwnPropertyDescriptor(HTMLTableElement.prototype, 'rows').get};
        })()
        """,
        """
        {"initial":[4,["first","second","third","outer"],[0,1,2,0,0,3,0],"first",0,true,true],"chosen":[true,true,"third",2],"moved":[1,3],"liveRows":"function"}
        """);

    [Fact]
    public void SelectAddRespectsGroupParentsAndUnionConversions() => AssertLegacyApis(
        """
        <html><body></body></html>
        """,
        """
        (() => {
            const result = [];
            for (const kind of ['number','string','bigint','symbol','object','group','self','foreign']) {
                const select = document.createElement('select');
                select.innerHTML = '<option id="a">A</option><optgroup><option id="b">B</option></optgroup>';
                const option = new Option('X');
                const before = {number:1, string:'1', bigint:1n, symbol:Symbol(),
                    object:{valueOf() { return 1; }}, group:select.querySelector('optgroup'),
                    self:select, foreign:document.createElementNS('urn:foreign','option')}[kind];
                try {
                    select.add(option, before);
                    result.push([kind, option.parentNode.localName, Array.from(select.options, o => o.text)]);
                } catch (error) { result.push([kind, error.name]); }
            }
            return result;
        })()
        """,
        """
        [["number","optgroup",["A","X","B"]],["string","optgroup",["A","X","B"]],["bigint","TypeError"],["symbol","TypeError"],["object","optgroup",["A","X","B"]],["group","select",["A","X","B"]],["self","NotFoundError"],["foreign","select",["X","A","B"]]]
        """);

    [Fact]
    public void SelectLengthAndRemoveOverloadsModifyOwnedOptionsOnly() => AssertLegacyApis(
        """
        <html><body><select><option id='a'>A</option><optgroup><option id='b'>B</option><option id='c'>C</option></optgroup></select></body></html>
        """,
        """
        (() => {
            const select = document.querySelector('select');
            const group = select.querySelector('optgroup');
            select.length = 5;
            const grown = [select.length, select.item(4).text, select.item(4) instanceof HTMLOptionElement];
            select.length = 2;
            const shrunk = [select.length, group.children.length, select.item(1).id];
            select.remove(-1);
            select.remove(99);
            const untouched = [select.length, select.isConnected];
            select.remove(null);
            const removedFirst = [select.length, select.item(0).id, select.isConnected];
            select.length = 0;
            select.length = 1;
            const cleared = [select.length, group.children.length, select.item(0).index];
            select.remove();
            return {grown, shrunk, untouched, removedFirst, cleared, connected:select.isConnected};
        })()
        """,
        """
        {"grown":[5,"",true],"shrunk":[2,1,"b"],"untouched":[2,true],"removedFirst":[1,"b",true],"cleared":[1,0,0],"connected":false}
        """);

    // Chromium 157 warns about a length over 100,000 on its own console, not through the page's console.warn (upstream expects two page calls).
    [Fact]
    public void SelectLegacyMethodsEnforceWebidlConversionsAndBoundedLengths() => AssertLegacyApis(
        """
        <html><body><select><option id='first' name='alias'>A</option><option>B</option></select></body></html>
        """,
        """
        (() => {
            const select = document.querySelector('select');
            const throws = f => { try { f(); return false; } catch (e) { return e instanceof TypeError; } };
            const errors = [throws(() => select.item()), throws(() => select.namedItem()),
                throws(() => select.item(0n)), throws(() => select.length = 2n),
                throws(() => select.namedItem(Symbol())), throws(() => select.remove(Symbol())),
                throws(() => select.length = Symbol())];
            const reads = [select.item('0').id, select.item(-1), select.item(undefined).id,
                select.namedItem('alias').id, select.namedItem(''), select.namedItem('missing')];
            const warnings = [];
            console.warn = value => warnings.push(value);
            select.length = 100001;
            select.length = -1;
            const bounded = [select.length, warnings.length,
                warnings.every(value => value.includes('100000'))];
            select.length = '3';
            return {errors, reads, bounded, converted:select.length};
        })()
        """,
        """
        {"errors":[true,true,true,true,true,true,true],"reads":["first",null,"first","first",null,null],"bounded":[2,0,true],"converted":3}
        """);

    [Fact]
    public void SelectLegacyMethodsReadOptionsAfterArgumentCoercion() => AssertLegacyApis(
        """
        <html><body><select><option id='first'>A</option></select></body></html>
        """,
        """
        (() => {
            const select = document.querySelector('select');
            const change = id => { select.innerHTML = '<option id="'+id+'">X</option>'; };
            const item = select.item({valueOf() { change('item'); return 0; }}).id;
            const named = select.namedItem({toString() { change('named'); return 'named'; }}).id;
            select.remove({valueOf() { change('removed'); return 0; }});
            const removed = select.length;
            select.length = {valueOf() { change('length'); return 0; }};
            return [item, named, removed, select.length];
        })()
        """,
        """
        ["item","named",0,0]
        """);

    [Fact]
    public void SelectAndOptionInterfacesPreserveFactoriesAndValidateReceivers() => AssertLegacyApis(
        """
        <html><body><select><option>A</option></select><div></div></body></html>
        """,
        """
        (() => {
            const select = document.querySelector('select');
            const option = select.item(0);
            const length = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'length');
            const index = Object.getOwnPropertyDescriptor(HTMLOptionElement.prototype, 'index').get;
            const errors = [];
            for (const value of [document.querySelector('div'), {}, Object.create(HTMLSelectElement.prototype),
                document.createElementNS('urn:foreign', 'select')]) {
                for (const method of [() => length.get.call(value), () => length.set.call(value, 2),
                    () => HTMLSelectElement.prototype.item.call(value, 0),
                    () => HTMLSelectElement.prototype.namedItem.call(value, ''),
                    () => HTMLSelectElement.prototype.remove.call(value)]) {
                    try { method(); errors.push(false); } catch (e) { errors.push(e instanceof TypeError); }
                }
            }
            for (const value of [select, {}, document.createElementNS('urn:foreign', 'option')]) {
                try { index.call(value); errors.push(false); } catch (e) { errors.push(e instanceof TypeError); }
            }
            const constructors = [HTMLSelectElement, HTMLOptionElement].map(C => {
                try { new C(); return false; } catch (e) { return e instanceof TypeError; }
            });
            return {brands:[select instanceof HTMLSelectElement, option instanceof HTMLOptionElement,
                select.cloneNode(true) instanceof HTMLSelectElement,
                option.cloneNode() instanceof HTMLOptionElement, new Option() instanceof HTMLOptionElement,
                document.createElement('div') instanceof HTMLSelectElement],
                noGenericLength:!('length' in document.createElement('div')),
                errors:errors.every(Boolean), constructors, detached:new Option().index,
                signatures:[HTMLSelectElement.length, HTMLOptionElement.length, index.name,
                    index.toString(), HTMLSelectElement.prototype.item.length,
                    HTMLSelectElement.prototype.namedItem.length, HTMLSelectElement.prototype.remove.length]};
        })()
        """,
        """
        {"brands":[true,true,true,true,true,false],"noGenericLength":true,"errors":true,"constructors":[true,true],"detached":0,"signatures":[0,0,"get index","function get index() { [native code] }",1,1,0]}
        """);

    [Fact]
    public void TableCellColspanAndRowspanReflectThroughTheIdlSurface() => AssertLegacyApis(
        """
        <table><tbody><tr><td id="plain">a</td><td id="markup" colspan="4" rowspan="2">b</td></tr><tr><td id="invalid" colspan="abc">c</td><th id="setter">d</th></tr></tbody></table>
        """,
        """
        (() => {
            const cell = document.getElementById('setter');
            cell.colSpan = 5; cell.rowSpan = 2;
            const plain = document.getElementById('plain'), markup = document.getElementById('markup');
            const a = [plain.colSpan, plain.rowSpan, plain.getAttribute('colspan'),
                markup.colSpan, markup.rowSpan, document.getElementById('invalid').colSpan,
                cell.colSpan, cell.rowSpan, cell.getAttribute('colspan'), cell.getAttribute('rowspan')];
            const b = ['0', '-1', '-0', '+2', '\v2', '1001', '65535', '999999999999999999999999999999999999', ' 3x', '2.9'].map(value => {
                cell.setAttribute('colspan', value); cell.setAttribute('rowspan', value);
                return [cell.colSpan, cell.rowSpan];
            });
            cell.setAttribute('rowspan', '-0');
            const c = Object.is(cell.rowSpan, -0);
            const d = [0, -1, 2.9, 1001, 65535, 2147483647, 2147483648, 4294967296, NaN, Infinity].map(value => {
                cell.colSpan = value; cell.rowSpan = value;
                return [cell.colSpan, cell.rowSpan, cell.getAttribute('colspan'), cell.getAttribute('rowspan')];
            });
            const g = Object.getOwnPropertyDescriptor(HTMLTableCellElement.prototype, 'colSpan');
            let bad = []; for (const v of [document.createElement('div'), {}]) { try { g.get.call(v); bad.push('ok'); } catch (e) { bad.push(e.name); } try { g.set.call(v, 2); bad.push('ok'); } catch (e) { bad.push(e.name); } }
            return {a, b, c, d, bad, names: [g.get.name, g.set.name, String(g.get)]};
        })()
        """,
        """
        {"a":[1,1,null,4,2,1,5,2,"5","2"],"b":[[1,0],[1,1],[1,0],[2,2],[1,1],[1000,1001],[1000,65534],[1000,65534],[3,3],[2,2]],"c":false,"d":[[1,0,"0","0"],[1,1,"1","1"],[2,2,"2","2"],[1000,1001,"1001","1001"],[1000,65534,"65535","65535"],[1000,65534,"2147483647","2147483647"],[1,1,"1","1"],[1,0,"0","0"],[1,0,"0","0"],[1,0,"0","0"]],"bad":["TypeError","TypeError","TypeError","TypeError"],"names":["get colSpan","set colSpan","function get colSpan() { [native code] }"]}
        """);

    // Deviation: defining a select index with configurable:false sets the option but answers false (Chromium 157: true); a Proxy may not report a non-configurable property its target lacks. Hence defNC false and odp a TypeError (Chromium: true, 'odp').
    [Fact]
    public void SelectAndTableEdgeCasesMatchChromium() => AssertLegacyApis(
        """
        <select id="s"><option value="a">A</option><option value="b" selected>B</option></select><table id="t"><tfoot><tr><td>f</td></tr></tfoot><thead><tr><th>h</th></tr></thead><tbody><tr id="body"><td id="first">1</td><td>2</td></tr></tbody><tr><td>direct</td></tr></table>
        """,
        """
        (() => {
            const r = {};
            const s = document.getElementById('s');
            const e = f => { try { return f(); } catch (x) { return 'THROW ' + x.name; } };
            r.big = e(() => { s[100000] = new Option('x'); return s.length; });
            r.strictRows = e(() => { 'use strict'; const t = document.getElementById('t'); t.rows[0] = null; return 'nothrow'; });
            r.strictSet = e(() => { 'use strict'; s[0] = 'bad'; return 'nothrow'; });
            r.defNC = e(() => Reflect.defineProperty(s, '0', {value: new Option('nc','nc'), configurable: false}));
            r.afterNC = s[0].value;
            r.gopdNC = (d => [d.value === s[0], d.writable, d.enumerable, d.configurable])(Object.getOwnPropertyDescriptor(s, "0"));
            r.odp = e(() => { Object.defineProperty(s, '1', {value: new Option('odp','odp'), configurable:false}); return s[1].value; });
            r.setNull = e(() => { s[5] = null; return s.length; });
            const t = document.getElementById('t');
            r.tableMembers = ['caption','createCaption','deleteCaption','createTHead','deleteTHead','createTFoot','deleteTFoot','createTBody','tHead','tFoot','tBodies','rows','insertRow','deleteRow','align','border','frame','rules','summary','width','bgColor','cellPadding','cellSpacing'].filter(k => !(k in t)).join();
            r.sectionMembers = ['rows','insertRow','deleteRow','align','ch','chOff','vAlign'].filter(k => !(k in t.tBodies[0])).join();
            r.rowMembers = ['rowIndex','sectionRowIndex','cells','insertCell','deleteCell','align','ch','chOff','vAlign','bgColor'].filter(k => !(k in t.rows[0])).join();
            r.preventExt = e(() => { Object.preventExtensions(t.rows); return 'ok'; });
            r.preventExtS = e(() => { Object.preventExtensions(s); return 'ok'; });
            r.rowsSetLen = e(() => { 'use strict'; t.rows.length = 0; return 'nothrow'; });
            r.selfIndex = e(() => { const x = document.createElement('select'); x[0] = x; return 'ok'; });
            r.divIndex = e(() => { const x = document.createElement('select'); x[0] = document.createElement('div'); return 'ok'; });
            r.optGroupIdx = e(() => { const x = document.createElement('select'); x[0] = document.createElement('optgroup'); return 'ok'; });
            return r;
        })()
        """,
        """
        {"big":2,"strictRows":"THROW TypeError","strictSet":"THROW TypeError","defNC":false,"afterNC":"nc","gopdNC":[true,true,true,true],"odp":"THROW TypeError","setNull":2,"tableMembers":"","sectionMembers":"","rowMembers":"","preventExt":"THROW TypeError","preventExtS":"THROW TypeError","rowsSetLen":"THROW TypeError","selfIndex":"THROW TypeError","divIndex":"THROW TypeError","optGroupIdx":"THROW TypeError"}
        """);
}
