using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// IndexedDB belongs to the browser context (upstream 3dea548): its records survive
/// navigation, pages of one context and origin share them, and another origin, an incognito
/// copy or a CDP connection copy starts empty.
/// </summary>
public sealed class IndexedDbTests
{
    private static TestHttpServer Server() =>
        TestHttpServer.Start(_ => TestResponse.Html("<!doctype html><html><body>page</body></html>"));

    private static BrowserContext Context(string id) =>
        BrowserContext.WithStorageAndNetwork(id, null, false, null, null, true);

    /// <summary>Starts <paramref name="script"/>, which sets <c>globalThis.idbOut</c>, and pumps until it has.</summary>
    private static async Task<JsonNode?> RunAsync(Page page, string script)
    {
        page.Evaluate("(() => { globalThis.idbOut = undefined; " + script + "; return 0; })()");
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline)
        {
            await page.Js!.RunEventLoopBoundedAsync(20);
            if (page.Evaluate("globalThis.idbOut !== undefined")?.GetValue<bool>() == true)
            {
                break;
            }
        }
        return page.Evaluate("globalThis.idbOut");
    }

    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.Equal(JsonNode.Parse(expected)?.ToJsonString() ?? "null", actual?.ToJsonString() ?? "null");

    // upstream 3dea548 (crates/obscura-browser/src/page.rs):
    // indexeddb_records_survive_navigation_and_share_only_their_context_and_origin
    [Fact]
    public async Task indexeddb_records_survive_navigation_and_share_only_their_context_and_origin()
    {
        using TestHttpServer server = Server();
        string originA = server.Origin;
        string originB = server.Origin.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        BrowserContext context = Context("idb-shared");
        using var first = new Page("idb-first", context);
        await first.NavigateAsync($"{originA}/");
        AssertJson("true", await RunAsync(first, """
            const r = indexedDB.open('shared',1);
            r.onupgradeneeded = () => r.result.createObjectStore('items');
            r.onsuccess = () => {
                const tx = r.result.transaction('items','readwrite');
                tx.objectStore('items').put({value:'kept'},1);
                tx.oncomplete = () => idbOut = true;
            }
            """));
        const string Read = """
            const r = indexedDB.open('shared',1);
            r.onupgradeneeded = () => r.result.createObjectStore('items');
            r.onsuccess = () => {
                const q = r.result.transaction('items').objectStore('items').get(1);
                q.onsuccess = () => idbOut = q.result?.value ?? null;
            }
            """;
        await first.NavigateAsync($"{originB}/");
        AssertJson("null", await RunAsync(first, Read));
        await first.NavigateAsync($"{originA}/");
        AssertJson("\"kept\"", await RunAsync(first, Read));
        using var second = new Page("idb-second", context);
        await second.NavigateAsync($"{originA}/");
        AssertJson("\"kept\"", await RunAsync(second, Read));
        using var isolated = new Page("idb-isolated", Context("idb-private"));
        await isolated.NavigateAsync($"{originA}/");
        AssertJson("null", await RunAsync(isolated, Read));
    }

    /// <summary>
    /// The common flow, every value measured in Chromium 141 with the same scripts: open v1
    /// with upgradeneeded creating a store (in-line keys, a key generator) and an index; put,
    /// get, an index getAll, a reverse cursor; then, after navigating to another document of
    /// the origin, reopen without a version and read through the index with a key range; a
    /// second origin of the context gets a new, empty database.
    /// </summary>
    [Fact]
    public async Task IndexedDbCommonFlowMatchesChromium()
    {
        using TestHttpServer server = Server();
        BrowserContext context = Context("idb-flow");
        using var page = new Page("idb-flow-page", context);
        await page.NavigateAsync($"{server.Origin}/one");
        AssertJson("""["upgrade:0->1","open:1:people","get:Bob","byCity:Ann,Cid","cursor:3=Cid,2=Bob,1=Ann","keys:1,2,3"]""",
            await RunAsync(page, """
                const events = [];
                const r = indexedDB.open('app', 1);
                r.onupgradeneeded = (e) => {
                  events.push('upgrade:' + e.oldVersion + '->' + e.newVersion);
                  const store = r.result.createObjectStore('people', { keyPath: 'id', autoIncrement: true });
                  store.createIndex('byCity', 'city');
                };
                r.onsuccess = () => {
                  const db = r.result;
                  events.push('open:' + db.version + ':' + Array.from(db.objectStoreNames).join());
                  const tx = db.transaction('people', 'readwrite');
                  const s = tx.objectStore('people');
                  const keys = [];
                  for (const p of [{ name: 'Ann', city: 'Oslo' }, { name: 'Bob', city: 'Bern' }, { name: 'Cid', city: 'Oslo' }]) s.put(p).onsuccess = (e) => keys.push(e.target.result);
                  const got = s.get(2);
                  got.onsuccess = () => events.push('get:' + got.result.name);
                  const all = s.index('byCity').getAll('Oslo');
                  all.onsuccess = () => events.push('byCity:' + all.result.map((p) => p.name).join());
                  const rows = [];
                  s.openCursor(null, 'prev').onsuccess = (e) => { const c = e.target.result; if (c) { rows.push(c.key + '=' + c.value.name); c.continue(); } else events.push('cursor:' + rows.join()); };
                  tx.oncomplete = () => { events.push('keys:' + keys.join()); db.close(); idbOut = events; };
                  tx.onerror = () => idbOut = 'error:' + tx.error;
                };
                r.onerror = () => idbOut = 'error:' + r.error
                """));

        const string Read = """
            const r = indexedDB.open('app');
            let upgraded = false;
            r.onupgradeneeded = () => { upgraded = true; };
            r.onsuccess = () => {
              const db = r.result;
              if (!db.objectStoreNames.contains('people')) { db.close(); idbOut = [upgraded, db.version, db.objectStoreNames.length]; return; }
              const q = db.transaction('people').objectStore('people').index('byCity').getAllKeys(IDBKeyRange.bound('A', 'P'));
              q.onsuccess = () => {
                const c = db.transaction('people').objectStore('people').count();
                c.onsuccess = () => { db.close(); idbOut = [upgraded, db.version, Array.from(db.objectStoreNames).join(), q.result.join(), c.result]; };
              };
            };
            r.onerror = () => idbOut = 'error:' + r.error
            """;
        await page.NavigateAsync($"{server.Origin}/two");
        AssertJson("""[false,1,"people","2,1,3",3]""", await RunAsync(page, Read));

        string other = server.Origin.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        using var second = new Page("idb-flow-other", context);
        await second.NavigateAsync($"{other}/");
        AssertJson("[true,1,0]", await RunAsync(second, Read));
    }

    // upstream 3dea548 (crates/obscura-browser/src/context.rs): a persistent copy shares the
    // context's IndexedDB, an incognito copy and a CDP connection copy start their own.
    [Fact]
    public void Context_copies_share_or_isolate_indexed_db_as_upstream()
    {
        var template = BrowserContext.New("idb-template");
        Assert.Same(template.IndexedDb, template.IsolatedCopy("persistent", true).IndexedDb);
        Assert.NotSame(template.IndexedDb, template.IsolatedCopy("incognito", false).IndexedDb);
        var a = template.ConnectionCopy("a");
        var b = template.ConnectionCopy("b");
        Assert.NotSame(a.IndexedDb, b.IndexedDb);
        Assert.NotSame(a.IndexedDb, template.IndexedDb);
    }
}
