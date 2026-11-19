using System.Text.Json.Nodes;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-js/src/indexed_db_tests.rs</c> (upstream 3dea548), in
/// source order and under the same names. Every expected value was also measured in
/// Chromium 141 with the same script, and agrees.
/// </summary>
public sealed class IndexedDbTests
{
    private const string Prelude = """
        globalThis.idbResult = null;
        const opened = (name, version = 1, upgrade = db => db.createObjectStore('items')) => new Promise((resolve, reject) => {
            const r = indexedDB.open(name, version);
            r.onupgradeneeded = () => upgrade(r.result);
            r.onsuccess = () => resolve(r.result);
            r.onerror = () => reject(r.error);
        });
        const requested = r => new Promise((resolve, reject) => {
            r.onsuccess = () => resolve(r.result);
            r.onerror = () => reject(r.error);
        });
        const finished = tx => new Promise(resolve => {
            tx.oncomplete = () => resolve('complete');
            tx.onabort = () => resolve('abort');
        });
        """;

    private static PocketCalculatorJsRuntime Setup(IndexedDbStorage? storage = null, string html = "<!doctype html><body></body>")
    {
        var rt = new PocketCalculatorJsRuntime();
        rt.SetDom(HtmlParsing.ParseHtml(html));
        rt.SetUrl("https://idb.test/");
        if (storage is not null)
        {
            rt.SetIndexedDb(storage);
        }
        rt.RunPageInit();
        return rt;
    }

    private static async Task<JsonNode?> Probe(string script)
    {
        using var rt = Setup();
        rt.ExecuteScript("indexeddb-regression", Prelude + """
            (async () => {
            """ + script + """
            })().then(
                value => idbResult = value,
                error => idbResult = {error:error.name, message:error.message}
            );
            """);
        await rt.RunEventLoopBoundedAsync(5000);
        return rt.Evaluate("idbResult");
    }

    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.Equal(JsonNode.Parse(expected)?.ToJsonString() ?? "null", actual?.ToJsonString() ?? "null");

    [Fact]
    public async Task indexeddb_upgrade_precedes_open_and_requests_precede_commit()
    {
        var output = await Probe("""
            const events = [];
            const r = indexedDB.open('ordering', 1);
            const db = await new Promise(resolve => {
                r.onupgradeneeded = () => { events.push('upgrade'); r.result.createObjectStore('items'); };
                r.onsuccess = () => { events.push('open'); resolve(r.result); };
            });
            const tx = db.transaction('items', 'readwrite');
            const done = finished(tx).then(() => events.push('complete'));
            tx.objectStore('items').put('kept', 1).onsuccess = () => events.push('put');
            await done;
            const value = await requested(db.transaction('items').objectStore('items').get(1));
            return {events, value, schema:db.objectStoreNames.contains('items')};
            """);
        AssertJson("""{"events":["upgrade","open","put","complete"],"value":"kept","schema":true}""", output);
    }

    [Fact]
    public async Task indexeddb_abort_rolls_back_and_readonly_writes_throw()
    {
        var output = await Probe("""
            const db = await opened('abort');
            const tx = db.transaction('items', 'readwrite');
            const done = finished(tx);
            const r = tx.objectStore('items').put('discard', 1);
            let requestError;
            r.onerror = () => {requestError = r.error.name;};
            tx.abort();
            const terminal = await done;
            let readonly;
            const read = db.transaction('items');
            const value = await requested(read.objectStore('items').get(1));
            try { db.transaction('items').objectStore('items').put('bad', 2); }
            catch (e) { readonly = e.name; }
            return {terminal, requestError, missing:value === undefined, readonly};
            """);
        AssertJson("""{"terminal":"abort","requestError":"AbortError","missing":true,"readonly":"ReadOnlyError"}""", output);
    }

    [Fact]
    public async Task indexeddb_values_are_cloned_and_duplicate_add_aborts_atomically()
    {
        var output = await Probe("""
            const db = await opened('clone');
            const tx = db.transaction('items', 'readwrite'), done = finished(tx);
            const source = {n:1, date:new Date(123), bytes:new Uint8Array([0,255]), map:new Map([['a',2]])};
            source.self = source;
            const store = tx.objectStore('items');
            store.put(source, 'clone'); source.n = 99;
            const cloned = await requested(store.get('clone'));
            await done;
            const dup = db.transaction('items', 'readwrite'), dupDone = finished(dup);
            const s = dup.objectStore('items'); s.add('first', 1);
            let error;
            s.add('second', 1).onerror = e => {error = e.target.error.name;};
            const terminal = await dupDone;
            const missing = await requested(db.transaction('items').objectStore('items').get(1));
            return [cloned.n, cloned.date.getTime(), Array.from(cloned.bytes), cloned.map.get('a'),
                cloned.self === cloned, error, terminal, missing === undefined];
            """);
        AssertJson("""[1,123,[0,255],2,true,"ConstraintError","abort",true]""", output);
    }

    [Fact]
    public async Task indexeddb_typed_keys_ranges_and_request_callbacks_share_transaction()
    {
        var output = await Probe("""
            const db = await opened('keys');
            const tx = db.transaction('items', 'readwrite'), done = finished(tx);
            const s = tx.objectStore('items');
            s.put('number', 1); s.put('string', '1'); s.put('date', new Date(1));
            s.put('array', [1,'a']); s.put('binary', new Uint8Array([1]));
            const first = s.get(1);
            first.onsuccess = () => s.put('callback', 2);
            await done;
            const read = db.transaction('items'), r = read.objectStore('items');
            return [await requested(r.get(1)), await requested(r.get('1')),
                await requested(r.get(new Date(1))), await requested(r.get([1,'a'])),
                await requested(r.get(new Uint8Array([1]))),
                await requested(r.getAll(IDBKeyRange.bound(1,2))),
                await requested(r.count())];
            """);
        AssertJson("""["number","string","date","array","binary",["number","callback"],6]""", output);
    }

    [Fact]
    public async Task indexeddb_failed_upgrade_keeps_previous_version_and_schema()
    {
        var output = await Probe("""
            const db = await opened('upgrade'); db.close();
            const r = indexedDB.open('upgrade', 2);
            const error = await new Promise(resolve => {
                r.onupgradeneeded = () => {r.result.createObjectStore('discard'); r.transaction.abort();};
                r.onerror = () => resolve(r.error.name);
                r.onsuccess = () => resolve('unexpected success');
            });
            const old = await requested(indexedDB.open('upgrade'));
            return [error, old.version, old.objectStoreNames.contains('items'), old.objectStoreNames.contains('discard')];
            """);
        AssertJson("""["AbortError",1,true,false]""", output);
    }

    [Fact]
    public async Task indexeddb_request_error_can_be_canceled_without_aborting()
    {
        var output = await Probe("""
            const db = await opened('handled');
            const tx = db.transaction('items','readwrite'), done = finished(tx);
            const s = tx.objectStore('items'); s.add('first',1);
            s.add('duplicate',1).onerror = e => e.preventDefault();
            s.put('second',2);
            const terminal = await done;
            return [terminal,await requested(db.transaction('items').objectStore('items').getAll())];
            """);
        AssertJson("""["complete",["first","second"]]""", output);
    }

    [Fact]
    public async Task indexeddb_unique_indexes_and_cursors_use_generated_keys()
    {
        var output = await Probe("""
            const request = indexedDB.open('indexed',1);
            const db = await new Promise((resolve,reject) => {
                request.onupgradeneeded = () => request.result.createObjectStore('users',{keyPath:'id',autoIncrement:true})
                    .createIndex('email','email',{unique:true});
                request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error);
            });
            const tx = db.transaction('users','readwrite'), done = finished(tx), users = tx.objectStore('users');
            const first = users.add({email:'b@test'}), second = users.add({email:'a@test'});
            const keys = [await requested(first),await requested(second)]; await done;
            const read = db.transaction('users'), index = read.objectStore('users').index('email');
            const found = await requested(index.get('b@test'));
            const rows = await new Promise(resolve => {
                const rows = [], cursor = index.openCursor();
                cursor.onsuccess = () => {const c=cursor.result;if (!c) return resolve(rows);
                    rows.push([c.key,c.primaryKey,c.value.id]); c.continue();};
            });
            const bad = db.transaction('users','readwrite'), badDone = finished(bad);
            bad.objectStore('users').add({email:'a@test'});
            return [keys,found.id,rows,await badDone];
            """);
        AssertJson("""[[1,2],1,[["a@test",2,2],["b@test",1,1]],"abort"]""", output);
    }

    [Fact]
    public async Task indexeddb_waiting_upgrade_notifies_and_respects_closed_connections()
    {
        var output = await Probe("""
            const first = await opened('connections'), events = [];
            first.onversionchange = e => {events.push([e.oldVersion,e.newVersion]);first.close();};
            const r = indexedDB.open('connections',2);
            const upgraded = await new Promise((resolve,reject) => {
                r.onupgradeneeded = () => {events.push('upgrade');r.result.createObjectStore('second');};
                r.onsuccess = () => resolve(r.result);r.onerror=()=>reject(r.error);
            });
            return [events,upgraded.version,upgraded.objectStoreNames.contains('second')];
            """);
        AssertJson("""[[[1,2],"upgrade"],2,true]""", output);
    }

    [Fact]
    public async Task indexeddb_transaction_is_inactive_in_unrelated_timer_tasks()
    {
        var output = await Probe("""
            const db = await opened('inactive');
            const tx = db.transaction('items','readwrite');
            const s = tx.objectStore('items');
            s.put('first',1);
            return await new Promise(resolve => setTimeout(() => {
                try {s.put('late',2);resolve('accepted');}catch(e){resolve(e.name);}
            },0));
            """);
        AssertJson("\"TransactionInactiveError\"", output);
    }

    [Fact]
    public async Task indexeddb_rejects_dom_nodes_instead_of_storing_a_plain_object()
    {
        var output = await Probe("""
            const db = await opened('uncloneable');
            const tx = db.transaction('items','readwrite');
            try {tx.objectStore('items').put({nested:document.body},1);return 'accepted';}
            catch(e){return e.name;}
            """);
        AssertJson("\"DataCloneError\"", output);
    }

    [Fact]
    public async Task indexeddb_cross_document_versionchange_is_delivered_by_the_event_loop()
    {
        var storage = new IndexedDbStorage();
        using var first = Setup(storage);
        first.ExecuteScript("first", """
            globalThis.notified = false;
            const request = indexedDB.open('cross-document',1);
            request.onsuccess = () => { const db = request.result;
                db.onversionchange = () => {notified = true; db.close();}; };
            """);
        await first.RunEventLoopBoundedAsync(100);
        using var second = Setup(storage);
        second.ExecuteScript("second", """
            globalThis.version = 0;
            const request = indexedDB.open('cross-document',2);
            request.onsuccess = () => version = request.result.version;
            """);
        await second.RunEventLoopBoundedAsync(10);
        await first.RunEventLoopBoundedAsync(100);
        Assert.Equal("true", first.Evaluate("notified")?.ToJsonString());
        await second.RunEventLoopBoundedAsync(100);
        Assert.Equal(2.0, second.Evaluate("version")?.GetValue<double>());
    }

    [Fact]
    public async Task indexeddb_failed_unique_insert_does_not_consume_an_auto_increment_key()
    {
        var output = await Probe("""
            const r = indexedDB.open('generator',1);
            const db = await new Promise(resolve => {
                r.onupgradeneeded = () => r.result.createObjectStore('items',{autoIncrement:true}).createIndex('unique','x',{unique:true});
                r.onsuccess = () => resolve(r.result);
            });
            const tx = db.transaction('items','readwrite'), done = finished(tx), s = tx.objectStore('items');
            s.add({x:1}); s.add({x:1}).onerror = e => e.preventDefault();
            const key = await requested(s.add({x:2})); await done;
            return key;
            """);
        Assert.Equal(2.0, output?.GetValue<double>());
    }

    [Fact]
    public async Task indexeddb_handlers_keep_listener_registration_order()
    {
        var output = await Probe("""
            const r = indexedDB.open('listeners',1), events = [];
            r.addEventListener('success', () => events.push('listener'));
            r.onsuccess = () => events.push('handler');
            await new Promise(resolve => r.addEventListener('success',resolve));
            return [events,r instanceof EventTarget,Object.prototype.toString.call(r)];
            """);
        AssertJson("""[["listener","handler"],true,"[object IDBOpenDBRequest]"]""", output);
    }

    [Fact]
    public async Task indexeddb_new_javascript_task_cannot_add_to_an_existing_transaction()
    {
        using var rt = Setup(html: "<!doctype html>");
        rt.ExecuteScript("open", """
            globalThis.db = null;
            const request = indexedDB.open('task',1);
            request.onupgradeneeded = () => request.result.createObjectStore('items');
            request.onsuccess = () => db = request.result;
            """);
        await rt.RunEventLoopBoundedAsync(100);
        rt.ExecuteScript("first-task", """
            globalThis.store = db.transaction('items','readwrite').objectStore('items');
            store.put('first',1);
            """);
        var result = rt.Evaluate("(() => {try {store.put('late',2);return 'accepted';}catch(e){return e.name;}})()");
        AssertJson("\"TransactionInactiveError\"", result);
    }

    [Fact]
    public async Task indexeddb_clones_files_and_blob_aliases_without_losing_their_interfaces()
    {
        var output = await Probe("""
            const db = await opened('files');
            const tx = db.transaction('items','readwrite'), done = finished(tx);
            const file = new File([new Uint8Array([0,255,117])],'fixture.bin',{type:'application/octet-stream',lastModified:123});
            tx.objectStore('items').put({file,alias:file,map:new Map([['file',file]])},1); await done;
            const copy = await requested(db.transaction('items').objectStore('items').get(1));
            return [copy.file instanceof File,copy.file.name,copy.file.type,copy.file.lastModified,
                Array.from(new Uint8Array(await copy.file.arrayBuffer())),copy.alias === copy.file,copy.map.get('file') === copy.file];
            """);
        AssertJson("""[true,"fixture.bin","application/octet-stream",123,[0,255,117],true,true]""", output);
    }

    [Fact]
    public async Task indexeddb_cloned_nonextractable_crypto_keys_keep_their_material_and_permissions()
    {
        var output = await Probe("""
            const key = await crypto.subtle.importKey('raw',new Uint8Array([1,2,3]),{name:'HMAC',hash:'SHA-256'},false,['sign']);
            const expected = new Uint8Array(await crypto.subtle.sign('HMAC',key,new Uint8Array([4,5,6])));
            const db = await opened('keys-storage');
            const tx = db.transaction('items','readwrite'), done = finished(tx);
            tx.objectStore('items').put({key},1); await done;
            const copy = (await requested(db.transaction('items').objectStore('items').get(1))).key;
            const actual = new Uint8Array(await crypto.subtle.sign('HMAC',copy,new Uint8Array([4,5,6])));
            let rejected;
            try {await crypto.subtle.exportKey('raw',copy);rejected=false;}catch(e){rejected=true;}
            return [copy instanceof CryptoKey,copy.extractable,copy.usages,actual.length,
                actual.every((b,i) => b === expected[i]),rejected];
            """);
        AssertJson("""[true,false,["sign"],32,true,true]""", output);
    }

    [Fact]
    public async Task indexeddb_empty_upgrade_completes_and_index_metadata_outlives_transactions()
    {
        var output = await Probe("""
            const empty = await new Promise((resolve,reject) => {
                const req = indexedDB.open('empty');
                req.onsuccess = () => resolve(req.result); req.onerror = () => reject(req.error);
            });
            const db = await opened('metadata',1,db => {
                db.createObjectStore('items').createIndex('email','email',{unique:true});
            });
            const tx = db.transaction('items'), done = finished(tx), index = tx.objectStore('items').index('email');
            await done;
            return [empty.version,empty.objectStoreNames.length,index.keyPath,index.unique,index.multiEntry];
            """);
        AssertJson("""[1,0,"email",true,false]""", output);
    }

    // ---- Port additions, each measured in Chromium 141 with the same script. ----

    /// <summary>
    /// Event shapes and paths: success, error and complete are plain Events, upgradeneeded an
    /// IDBVersionChangeEvent; a request's error reaches capture listeners on the database
    /// first, then bubbles through the transaction to the database; the name lists are
    /// DOMStringLists. Upstream fires IDBVersionChangeEvents only and skips capture.
    /// </summary>
    [Fact]
    public async Task indexeddb_events_follow_chromium_shapes_and_paths()
    {
        var output = await Probe("""
            const out = {};
            const r = indexedDB.open('sem', 1);
            let upgradeEv;
            const db = await new Promise((resolve) => {
              r.onupgradeneeded = (e) => { upgradeEv = [Object.prototype.toString.call(e), e.oldVersion, e.newVersion, e.bubbles, e.cancelable, r.transaction && r.transaction.mode, r.readyState];
                const s = r.result.createObjectStore('items', {keyPath: 'id'}); s.createIndex('by', 'tag'); };
              r.onsuccess = (e) => { out.successEvent = [Object.prototype.toString.call(e), e.bubbles, e.cancelable, e.isTrusted, r.transaction]; resolve(r.result); };
            });
            out.upgradeEv = upgradeEv;
            const seen = [];
            db.addEventListener('error', (e) => seen.push(['db-capture', e.eventPhase, e.target === req]), true);
            db.addEventListener('error', (e) => seen.push(['db-bubble', e.eventPhase]));
            const tx = db.transaction('items', 'readwrite');
            tx.addEventListener('error', (e) => seen.push(['tx-bubble', e.eventPhase, Object.prototype.toString.call(e)]));
            const s = tx.objectStore('items');
            s.add({id: 1, tag: 'a'});
            const req = s.add({id: 1, tag: 'b'});
            req.onerror = (e) => { seen.push(['req', e.eventPhase, e.composedPath().length]); e.preventDefault(); };
            await new Promise((resolve) => { tx.oncomplete = resolve; tx.onabort = resolve; });
            out.errorPath = seen;
            out.completeEvent = await new Promise((resolve) => { const t = db.transaction('items'); t.oncomplete = (e) => resolve([Object.prototype.toString.call(e), e.bubbles]); });
            out.names = [Array.isArray(db.objectStoreNames), Object.prototype.toString.call(db.objectStoreNames), db.objectStoreNames.length, db.objectStoreNames[0]];
            out.dbs = await indexedDB.databases();
            try { new IDBRequest(); } catch (e) { out.ctor = e.message; }
            try { IDBKeyRange.only({}); } catch (e) { out.badKey = [e.name, e.message]; }
            out.cmp = [indexedDB.cmp(1, '1'), indexedDB.cmp([1], 'z'), indexedDB.cmp(new Date(5), 6)];
            try { indexedDB.open('x', 0); } catch (e) { out.badVersion = [e.name, e.message]; }
            out.storeProps = (() => { const st = db.transaction('items').objectStore('items'); return [st.keyPath, st.autoIncrement, Array.from(st.indexNames), Object.prototype.toString.call(st.indexNames)]; })();
            return out;
            """);
        AssertJson("""
            {"successEvent":["[object Event]",false,false,true,null],
             "upgradeEv":["[object IDBVersionChangeEvent]",0,1,false,false,"versionchange","done"],
             "errorPath":[["db-capture",1,true],["req",2,1],["tx-bubble",3,"[object Event]"],["db-bubble",3]],
             "completeEvent":["[object Event]",false],
             "names":[false,"[object DOMStringList]",1,"items"],
             "dbs":[{"name":"sem","version":1}],
             "ctor":"Failed to construct 'IDBRequest': Illegal constructor",
             "badKey":["DataError","Failed to execute 'only' on 'IDBKeyRange': The parameter is not a valid key."],
             "cmp":[-1,1,1],
             "badVersion":["TypeError","Failed to execute 'open' on 'IDBFactory': The version provided must not be 0."],
             "storeProps":["id",false,["by"],"[object DOMStringList]"]}
            """, output);
    }

    /// <summary>The values structured serialization keeps and the ones it refuses (Chromium 141).</summary>
    [Fact]
    public async Task indexeddb_serializes_values_as_structured_clone_does()
    {
        var output = await Probe("""
            const db = await opened('kinds', 1, db => db.createObjectStore('items', {keyPath: 'id'}));
            const out = {};
            out.kinds = await new Promise((resolve) => {
              const t = db.transaction('items', 'readwrite');
              const v = {id: 9, a: undefined, n: -0, nan: NaN, big: 10n, bool: new Boolean(false), str: new String('s'), re: /x/gi, err: new RangeError('r'), sparse: [1, , 3], buf: new ArrayBuffer(2)};
              v.view = new Uint8Array(v.buf); v.view2 = new Uint16Array(v.buf);
              v.sparse.extra = 'e';
              t.objectStore('items').put(v);
              t.oncomplete = () => { const q = db.transaction('items').objectStore('items').get(9); q.onsuccess = () => { const c = q.result; resolve(['a' in c, Object.is(c.n, -0), Number.isNaN(c.nan), typeof c.big, typeof c.bool, c.bool.valueOf(), c.str.valueOf(), String(c.re), c.err instanceof RangeError, c.err.message, 1 in c.sparse, c.sparse.length, c.sparse.extra, c.view.buffer === c.buf, c.view2.buffer === c.buf]); }; };
            });
            for (const bad of [() => 1, Symbol('s'), new WeakMap(), Promise.resolve()]) {
              try { db.transaction('items', 'readwrite').objectStore('items').put({id: 10, bad}); out[Object.prototype.toString.call(bad)] = 'ok'; } catch (e) { out[Object.prototype.toString.call(bad)] = e.name; }
            }
            return out;
            """);
        AssertJson("""
            {"kinds":[true,true,true,"bigint","object",false,"s","/x/gi",true,"r",false,3,"e",true,true],
             "[object Function]":"DataCloneError","[object Symbol]":"DataCloneError",
             "[object WeakMap]":"DataCloneError","[object Promise]":"DataCloneError"}
            """, output);
    }

    /// <summary>
    /// An opaque-origin document gets the factory, as in Chromium, and its open() throws
    /// SecurityError. Upstream's indexedDB getter throws instead.
    /// </summary>
    [Fact]
    public void indexeddb_opaque_origin_answers_the_factory_and_refuses_open()
    {
        using var rt = new PocketCalculatorJsRuntime();
        rt.SetDom(HtmlParsing.ParseHtml("<!doctype html><body></body>"));
        rt.SetUrl("data:text/html,x");
        rt.RunPageInit();
        AssertJson("""
            ["object","SecurityError","Failed to execute 'open' on 'IDBFactory': access to the Indexed Database API is denied in this context."]
            """, rt.Evaluate("""
            (() => { const f = indexedDB; try { f.open('x'); return [typeof f, 'opened']; } catch (e) { return [typeof f, e.name, e.message]; } })()
            """));
    }

    /// <summary>The notice hook is the host's, not a page-visible global.</summary>
    [Fact]
    public void indexeddb_notice_hook_is_not_page_visible()
    {
        using var rt = Setup();
        AssertJson("[false,false]", rt.Evaluate("""
            ['__obscura_poll_indexeddb' in globalThis, Object.getOwnPropertyNames(globalThis).some((n) => /indexeddb_?poll|poll_indexeddb/i.test(n))]
            """));
    }
}

/// <summary>
/// The xUnit port of the unit tests in <c>crates/obscura-js/src/indexed_db.rs</c> (upstream
/// 3dea548), in source order and under the same names.
/// </summary>
public sealed class IndexedDbStorageTests
{
    private const string Origin = "https://idb.test";

    private static ulong Begin(IndexedDbOwner owner, string name, string mode)
    {
        var connection = owner.Command(Origin, "connect", new JsonObject { ["name"] = name });
        var id = owner.Command(Origin, "begin", new JsonObject
        {
            ["name"] = name,
            ["mode"] = mode,
            ["stores"] = new JsonArray("items"),
            ["connection"] = connection?.DeepClone(),
        })!.GetValue<ulong>();
        Assert.NotNull(owner.Command(Origin, "ready", new JsonObject { ["id"] = id }));
        return id;
    }

    private static string? Error(JsonNode? result) => (result as JsonObject)?["error"]?.GetValue<string>();

    [Fact]
    public void pending_writes_share_one_context_memory_budget()
    {
        var store = new IndexedDbStorage();
        using var owner = new IndexedDbOwner(store);
        var first = Begin(owner, "first", "versionchange");
        var second = Begin(owner, "second", "versionchange");
        var value = new string('x', 8 * 1024 * 1024);
        var refused = false;
        for (var i = 0; i < 9; i++)
        {
            var result = owner.Command(Origin, "put", new JsonObject
            {
                ["id"] = i < 4 ? first : second,
                ["store"] = "items",
                ["key"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["value"] = value,
            });
            refused |= Error(result) == "QuotaExceededError";
        }
        Assert.True(refused, "parallel databases must not multiply the context's pending-write budget");
    }

    [Fact]
    public void malformed_commit_cannot_publish_writes_or_consume_the_transaction()
    {
        using var owner = new IndexedDbOwner(new IndexedDbStorage());
        var id = Begin(owner, "atomic", "versionchange");
        owner.Command(Origin, "put", new JsonObject { ["id"] = id, ["store"] = "items", ["key"] = "key", ["value"] = "value" });
        Assert.Equal("DataError", Error(owner.Command(Origin, "commit", new JsonObject
        {
            ["id"] = id,
            ["schema"] = new JsonArray(new JsonObject { ["name"] = "items" }),
        })));
        Assert.Equal("[]", owner.Command(Origin, "info", new JsonObject { ["name"] = "atomic" })?["schema"]?.ToJsonString());
        Assert.Equal("\"value\"", owner.Command(Origin, "get", new JsonObject { ["id"] = id, ["store"] = "items", ["key"] = "key" })?.ToJsonString());
    }

    [Fact]
    public void dropping_a_document_aborts_its_writes_and_unblocks_waiting_transactions()
    {
        var store = new IndexedDbStorage();
        var owner = new IndexedDbOwner(store);
        var id = Begin(owner, "dropped", "versionchange");
        owner.Command(Origin, "put", new JsonObject { ["id"] = id, ["store"] = "items", ["key"] = "key", ["value"] = "discard" });
        using var next = new IndexedDbOwner(store);
        var waiting = next.Command(Origin, "begin", new JsonObject
        {
            ["name"] = "dropped",
            ["mode"] = "versionchange",
            ["stores"] = new JsonArray(),
        })!.GetValue<ulong>();
        Assert.Null(next.Command(Origin, "ready", new JsonObject { ["id"] = waiting }));
        owner.Dispose();
        Assert.NotNull(next.Command(Origin, "ready", new JsonObject { ["id"] = waiting }));
        Assert.Null(next.Command(Origin, "get", new JsonObject { ["id"] = waiting, ["store"] = "items", ["key"] = "key" }));
    }

    [Fact]
    public void opening_requests_are_ordered_across_documents_and_released_on_teardown()
    {
        var storage = new IndexedDbStorage();
        var first = new IndexedDbOwner(storage);
        using var second = new IndexedDbOwner(storage);
        var a = first.Command(Origin, "reserve_open", new JsonObject { ["name"] = "shared" })!.GetValue<ulong>();
        var b = second.Command(Origin, "reserve_open", new JsonObject { ["name"] = "shared" })!.GetValue<ulong>();
        Assert.Equal("true", first.Command(Origin, "open_ready", new JsonObject { ["name"] = "shared", ["opening"] = a })?.ToJsonString());
        Assert.Equal("false", second.Command(Origin, "open_ready", new JsonObject { ["name"] = "shared", ["opening"] = b })?.ToJsonString());
        first.Dispose();
        Assert.Equal("true", second.Command(Origin, "open_ready", new JsonObject { ["name"] = "shared", ["opening"] = b })?.ToJsonString());
        Assert.Equal("true", second.Command(Origin, "delete", new JsonObject { ["name"] = "shared" })?.ToJsonString());
        Assert.Equal("true", second.Command(Origin, "open_ready", new JsonObject { ["name"] = "shared", ["opening"] = b })?.ToJsonString());
    }
}
