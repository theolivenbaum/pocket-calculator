using System.Text.Json;
using System.Text.Json.Nodes;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// A browser context's IndexedDB records (upstream 3dea548, <c>crates/obscura-js/src/indexed_db.rs</c>).
/// Databases are keyed by storage key and name; transactions stage deltas, not copies of a
/// database, and publish them atomically on commit. The shim (<c>bootstrap.js</c>) keeps
/// the IndexedDB objects, key encoding, schema and structured serialization; this class
/// keeps the committed records, the per-database transaction queue, the open queue and the
/// connections, so pages of one context share them and a navigation keeps them.
/// </summary>
/// <remarks>
/// Thread-safe: pages of one context run on different threads. In memory only, as upstream:
/// <c>--storage-dir</c> does not persist IndexedDB. Sizes are counted in UTF-16 code units,
/// where upstream counts UTF-8 bytes; the shim's payloads are ASCII JSON, so the budgets
/// agree for them.
/// </remarks>
public sealed class IndexedDbStorage
{
    /// <summary>The context's budget for committed records, schema and pending writes (64 MiB).</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>Most databases, connections, open requests and transactions at once.</summary>
    public const int MaxHandles = 1024;

    private const long RecordOverhead = 96;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Origin, string Name), Database> _databases = [];
    private readonly Dictionary<ulong, Transaction> _transactions = [];
    private readonly Dictionary<ulong, List<JsonObject>> _notices = [];
    private ulong _next;
    private long _bytes;
    private long _pendingBytes;
    private int _pendingNotices;

    private sealed class Database
    {
        public ulong Version;
        public JsonArray Schema = [];
        public readonly Dictionary<string, Dictionary<string, string>> Records = new(StringComparer.Ordinal);
        public readonly Dictionary<ulong, ulong> Connections = [];
        public readonly List<ulong> Queue = [];
        public readonly List<(ulong Id, ulong Owner)> Openings = [];
        public long SchemaBytes;
    }

    private sealed class Transaction
    {
        public required ulong Owner;
        public required (string Origin, string Name) Database;
        public required string Mode;
        public required HashSet<string> Scope;
        public readonly HashSet<string> Cleared = new(StringComparer.Ordinal);
        public readonly Dictionary<(string Store, string Key), string?> Writes = [];
        public long PendingBytes;
    }

    /// <summary>True while a <c>versionchange</c> notice waits for some document of the context.</summary>
    public bool HasPendingNotifications => Volatile.Read(ref _pendingNotices) != 0;

    internal ulong NextId()
    {
        lock (_gate)
        {
            return ++_next;
        }
    }

    internal JsonNode? Command(ulong owner, string origin, string action, JsonNode? data)
    {
        lock (_gate)
        {
            try
            {
                return Execute(owner, origin, action, data);
            }
            catch (IdbError error)
            {
                return new JsonObject { ["error"] = error.Name };
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException)
            {
                // A name System.Text.Json cannot read (an unpaired surrogate): upstream does
                // not support those either.
                return new JsonObject { ["error"] = "DataError" };
            }
            finally
            {
                Volatile.Write(ref _pendingNotices, _notices.Count > 0 ? 1 : 0);
            }
        }
    }

    internal JsonArray TakeNotices(ulong owner)
    {
        lock (_gate)
        {
            var taken = _notices.Remove(owner, out var notices) ? notices : [];
            Volatile.Write(ref _pendingNotices, _notices.Count > 0 ? 1 : 0);
            JsonArray result = [];
            foreach (var notice in taken)
            {
                result.Add(notice);
            }
            return result;
        }
    }

    /// <summary>
    /// Upstream <c>Drop for IndexedDbOwner</c>: a document that goes away aborts its
    /// transactions, closes its connections and gives up its place in the open queues.
    /// </summary>
    internal void Release(ulong owner)
    {
        lock (_gate)
        {
            List<ulong> ids = [];
            foreach (var (id, tx) in _transactions)
            {
                if (tx.Owner == owner)
                {
                    ids.Add(id);
                    _pendingBytes = Math.Max(0, _pendingBytes - tx.PendingBytes);
                }
            }
            foreach (var id in ids)
            {
                _transactions.Remove(id);
            }
            foreach (var db in _databases.Values)
            {
                foreach (var connection in db.Connections.Where(entry => entry.Value == owner).Select(entry => entry.Key).ToList())
                {
                    db.Connections.Remove(connection);
                }
                db.Queue.RemoveAll(ids.Contains);
                db.Openings.RemoveAll(entry => entry.Owner == owner);
            }
            _notices.Remove(owner);
            Volatile.Write(ref _pendingNotices, _notices.Count > 0 ? 1 : 0);
        }
    }

    private sealed class IdbError(string name) : Exception(name)
    {
        public string Name { get; } = name;
    }

    private static string Text(JsonNode? data, string key) =>
        data?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new IdbError("DataError");

    private static ulong? Number(JsonNode? data, string key)
    {
        if (data?[key] is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<ulong>(out var whole))
        {
            return whole;
        }
        return value.TryGetValue<double>(out var real) && real >= 0 && real <= ulong.MaxValue && Math.Floor(real) == real
            ? (ulong)real
            : null;
    }

    private static long RecordBytes(string key, string value) => key.Length + value.Length + RecordOverhead;

    private JsonNode? Execute(ulong owner, string origin, string action, JsonNode? data)
    {
        if (origin == "null")
        {
            throw new IdbError("SecurityError");
        }
        if (action == "access")
        {
            return JsonValue.Create(true);
        }
        if (action == "databases")
        {
            JsonArray list = [];
            foreach (var ((dbOrigin, name), db) in _databases.OrderBy(entry => entry.Key.Name, StringComparer.Ordinal))
            {
                if (dbOrigin == origin && db.Version > 0)
                {
                    list.Add(new JsonObject { ["name"] = name, ["version"] = db.Version });
                }
            }
            return list;
        }

        if (action is "info" or "connect" or "close" or "notify" or "delete" or "begin"
            or "reserve_open" or "open_ready" or "release_open")
        {
            var name = Text(data, "name");
            var key = (origin, name);
            if (name.Length > 16 * 1024 || (_databases.Count >= MaxHandles && !_databases.ContainsKey(key)))
            {
                throw new IdbError("QuotaExceededError");
            }
            if (action == "delete")
            {
                if (!_databases.TryGetValue(key, out var existing))
                {
                    return JsonValue.Create(true);
                }
                if (existing.Connections.Count > 0 || existing.Queue.Count > 0)
                {
                    return JsonValue.Create(false);
                }
                _bytes = Math.Max(0, _bytes - existing.SchemaBytes);
                foreach (var store in existing.Records.Values)
                {
                    foreach (var (k, v) in store)
                    {
                        _bytes = Math.Max(0, _bytes - RecordBytes(k, v));
                    }
                }
                existing.Records.Clear();
                existing.Schema = [];
                existing.SchemaBytes = 0;
                existing.Version = 0;
                return JsonValue.Create(true);
            }

            if (!_databases.TryGetValue(key, out var db))
            {
                db = new Database();
                _databases[key] = db;
            }
            switch (action)
            {
                case "reserve_open":
                    if (db.Openings.Count >= MaxHandles)
                    {
                        throw new IdbError("QuotaExceededError");
                    }
                    db.Openings.Add((++_next, owner));
                    return JsonValue.Create(_next);
                case "open_ready":
                case "release_open":
                {
                    var opening = Number(data, "opening") ?? throw new IdbError("DataError");
                    if (action == "open_ready")
                    {
                        return JsonValue.Create(db.Openings.Count > 0 && db.Openings[0] == (opening, owner));
                    }
                    db.Openings.Remove((opening, owner));
                    if (db.Openings.Count == 0 && db.Version == 0 && db.Connections.Count == 0 && db.Queue.Count == 0)
                    {
                        _databases.Remove(key);
                    }
                    return null;
                }
                case "info":
                    return new JsonObject
                    {
                        ["version"] = db.Version,
                        ["schema"] = db.Schema.DeepClone(),
                        ["connections"] = db.Connections.Count,
                    };
                case "connect":
                    if (db.Connections.Count >= MaxHandles)
                    {
                        throw new IdbError("QuotaExceededError");
                    }
                    db.Connections[++_next] = owner;
                    return JsonValue.Create(_next);
                case "close":
                {
                    var connection = Number(data, "connection") ?? throw new IdbError("DataError");
                    if (db.Connections.TryGetValue(connection, out var holder) && holder == owner)
                    {
                        db.Connections.Remove(connection);
                    }
                    return null;
                }
                case "notify":
                {
                    var version = data?["version"]?.DeepClone();
                    foreach (var (id, other) in db.Connections)
                    {
                        if (!_notices.TryGetValue(other, out var pending))
                        {
                            pending = [];
                            _notices[other] = pending;
                        }
                        var notice = new JsonObject { ["connection"] = id, ["version"] = version?.DeepClone() };
                        if (pending.Count < MaxHandles && !pending.Exists(existing => JsonNode.DeepEquals(existing, notice)))
                        {
                            pending.Add(notice);
                        }
                    }
                    return null;
                }
                default: // begin
                {
                    if (_transactions.Count >= MaxHandles)
                    {
                        throw new IdbError("QuotaExceededError");
                    }
                    var mode = Text(data, "mode");
                    if (mode is not ("readonly" or "readwrite" or "versionchange"))
                    {
                        throw new IdbError("TypeError");
                    }
                    if (mode != "versionchange")
                    {
                        var connection = Number(data, "connection") ?? throw new IdbError("InvalidStateError");
                        if (!db.Connections.TryGetValue(connection, out var holder) || holder != owner)
                        {
                            throw new IdbError("InvalidStateError");
                        }
                    }
                    if (data?["stores"] is not JsonArray stores)
                    {
                        throw new IdbError("DataError");
                    }
                    var scope = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var store in stores)
                    {
                        scope.Add(store is JsonValue value && value.TryGetValue<string>(out var text)
                            ? text
                            : throw new IdbError("DataError"));
                    }
                    var id = ++_next;
                    db.Queue.Add(id);
                    _transactions[id] = new Transaction
                    {
                        Owner = owner,
                        Database = key,
                        Mode = mode,
                        Scope = scope,
                    };
                    return JsonValue.Create(id);
                }
            }
        }

        var txId = Number(data, "id") ?? throw new IdbError("TransactionInactiveError");
        if (!_transactions.TryGetValue(txId, out var tx))
        {
            throw new IdbError("TransactionInactiveError");
        }
        if (tx.Owner != owner || tx.Database.Origin != origin)
        {
            throw new IdbError("InvalidStateError");
        }
        if (!_databases.TryGetValue(tx.Database, out var database))
        {
            throw new IdbError("InvalidStateError");
        }
        if (action == "abort")
        {
            _pendingBytes = Math.Max(0, _pendingBytes - tx.PendingBytes);
            _transactions.Remove(txId);
            database.Queue.Remove(txId);
            return null;
        }
        var current = database.Queue.Count > 0 && database.Queue[0] == txId;
        if (action == "ready")
        {
            return current
                ? new JsonObject { ["version"] = database.Version, ["schema"] = database.Schema.DeepClone() }
                : null;
        }
        if (!current)
        {
            throw new IdbError("TransactionInactiveError");
        }
        if (action == "commit")
        {
            return Commit(txId, tx, database, data);
        }

        var storeName = Text(data, "store");
        if (tx.Mode != "versionchange" && !tx.Scope.Contains(storeName))
        {
            throw new IdbError("NotFoundError");
        }
        switch (action)
        {
            case "get":
            {
                var recordKey = Text(data, "key");
                string? value;
                if (!tx.Writes.TryGetValue((storeName, recordKey), out value))
                {
                    value = !tx.Cleared.Contains(storeName)
                        && database.Records.TryGetValue(storeName, out var records)
                        && records.TryGetValue(recordKey, out var committed)
                        ? committed
                        : null;
                }
                return value is null ? null : JsonValue.Create(value);
            }
            case "entries":
            {
                var records = tx.Cleared.Contains(storeName) || !database.Records.TryGetValue(storeName, out var committed)
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(committed, StringComparer.Ordinal);
                foreach (var ((store, recordKey), value) in tx.Writes)
                {
                    if (store != storeName)
                    {
                        continue;
                    }
                    if (value is null)
                    {
                        records.Remove(recordKey);
                    }
                    else
                    {
                        records[recordKey] = value;
                    }
                }
                JsonArray entries = [];
                foreach (var (recordKey, value) in records)
                {
                    entries.Add(new JsonArray(recordKey, value));
                }
                return entries;
            }
            case "put":
            case "remove":
            case "clear":
                if (tx.Mode == "readonly")
                {
                    throw new IdbError("ReadOnlyError");
                }
                if (action == "clear")
                {
                    tx.Cleared.Add(storeName);
                    long released = 0;
                    foreach (var ((store, recordKey), value) in tx.Writes)
                    {
                        if (store == storeName)
                        {
                            released += store.Length + recordKey.Length + (value?.Length ?? 0) + RecordOverhead;
                        }
                    }
                    tx.PendingBytes = Math.Max(0, tx.PendingBytes - released);
                    _pendingBytes = Math.Max(0, _pendingBytes - released);
                    foreach (var write in tx.Writes.Keys.Where(write => write.Store == storeName).ToList())
                    {
                        tx.Writes.Remove(write);
                    }
                }
                else
                {
                    var recordKey = Text(data, "key");
                    var value = action == "put" ? Text(data, "value") : null;
                    if (recordKey.Length > 64 * 1024 || (value?.Length ?? 0) > 16 * 1024 * 1024)
                    {
                        throw new IdbError("QuotaExceededError");
                    }
                    var old = tx.Writes.TryGetValue((storeName, recordKey), out var previous)
                        ? storeName.Length + recordKey.Length + (previous?.Length ?? 0) + RecordOverhead
                        : 0;
                    var added = storeName.Length + recordKey.Length + (value?.Length ?? 0) + RecordOverhead;
                    var total = Math.Max(0, _pendingBytes - old) + added;
                    if (_bytes + total > MaxBytes)
                    {
                        throw new IdbError("QuotaExceededError");
                    }
                    tx.PendingBytes = Math.Max(0, tx.PendingBytes - old) + added;
                    _pendingBytes = total;
                    tx.Writes[(storeName, recordKey)] = value;
                }
                return null;
            default:
                throw new IdbError("NotSupportedError");
        }
    }

    private JsonNode? Commit(ulong txId, Transaction tx, Database db, JsonNode? data)
    {
        if (data?["schema"] is not JsonArray schema)
        {
            throw new IdbError("DataError");
        }
        ulong? version = null;
        if (tx.Mode == "versionchange")
        {
            version = Number(data, "version") is { } v && v > 0 ? v : throw new IdbError("DataError");
        }
        if (schema.Count > MaxHandles)
        {
            throw new IdbError("QuotaExceededError");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var store in schema)
        {
            names.Add(Text(store, "name"));
        }
        var schemaJson = schema.ToJsonString();
        long schemaBytes = schemaJson.Length;
        var bytes = Math.Max(0, _bytes - db.SchemaBytes) + schemaBytes;
        foreach (var (name, records) in db.Records)
        {
            if (!names.Contains(name) || tx.Cleared.Contains(name))
            {
                foreach (var (k, v) in records)
                {
                    bytes = Math.Max(0, bytes - RecordBytes(k, v));
                }
            }
        }
        foreach (var ((store, key), value) in tx.Writes)
        {
            if (!names.Contains(store))
            {
                continue;
            }
            if (!tx.Cleared.Contains(store) && db.Records.TryGetValue(store, out var records) && records.TryGetValue(key, out var old))
            {
                bytes = Math.Max(0, bytes - RecordBytes(key, old));
            }
            if (value is not null)
            {
                bytes += RecordBytes(key, value);
            }
        }
        if (bytes > MaxBytes)
        {
            throw new IdbError("QuotaExceededError");
        }

        _transactions.Remove(txId);
        _pendingBytes = Math.Max(0, _pendingBytes - tx.PendingBytes);
        foreach (var name in db.Records.Keys.Where(name => !names.Contains(name) || tx.Cleared.Contains(name)).ToList())
        {
            db.Records.Remove(name);
        }
        foreach (var ((store, key), value) in tx.Writes)
        {
            if (!names.Contains(store))
            {
                continue;
            }
            if (!db.Records.TryGetValue(store, out var records))
            {
                records = new Dictionary<string, string>(StringComparer.Ordinal);
                db.Records[store] = records;
            }
            if (value is null)
            {
                records.Remove(key);
            }
            else
            {
                records[key] = value;
            }
        }
        db.Schema = (JsonArray)schema.DeepClone();
        db.SchemaBytes = schemaBytes;
        if (version is { } committed)
        {
            db.Version = committed;
        }
        db.Queue.RemoveAt(0);
        _bytes = bytes;
        return null;
    }
}

/// <summary>
/// One document's handle on its context's <see cref="IndexedDbStorage"/> (upstream
/// <c>IndexedDbOwner</c>): its connections, open requests and transactions are its own, and
/// <see cref="Dispose"/>, when the document goes away, releases them.
/// </summary>
public sealed class IndexedDbOwner(IndexedDbStorage storage) : IDisposable
{
    private int _disposed;

    public IndexedDbStorage Storage { get; } = storage ?? throw new ArgumentNullException(nameof(storage));

    public ulong Id { get; } = storage.NextId();

    /// <summary>One command of the shim's protocol; an error answers <c>{"error": name}</c>.</summary>
    public JsonNode? Command(string origin, string action, JsonNode? data) =>
        Storage.Command(Id, origin, action, data);

    /// <summary>The <c>versionchange</c> notices waiting for this document's connections.</summary>
    public JsonArray TakeNotices() => Storage.TakeNotices(Id);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Storage.Release(Id);
        }
    }

    /// <summary>
    /// <c>op_indexed_db</c>: one command from the calling document, as JSON. Upstream answers a
    /// payload over 24 MiB with QuotaExceededError and one that does not parse with DataError.
    /// </summary>
    public static string RunOp(PocketCalculatorState document, string action, string payload)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (payload.Length > 24 * 1024 * 1024)
        {
            return """{"error":"QuotaExceededError"}""";
        }
        var owner = document.IndexedDbOwner ??= new IndexedDbOwner(document.IndexedDb);
        if (action == "notices")
        {
            return owner.TakeNotices().ToJsonString();
        }
        JsonNode? data;
        try
        {
            data = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            return """{"error":"DataError"}""";
        }
        var origin = StateHelpers.StorageKey(document) ?? "null";
        return owner.Command(origin, action, data)?.ToJsonString() ?? "null";
    }
}
