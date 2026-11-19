using System.Text.Json;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// One Web Storage area set (HTML "Web storage"): a storage area per storage key, kept on
/// the host so it outlives the document that wrote it. A browser context holds one for
/// <c>localStorage</c> and each page (top-level browsing context) one for
/// <c>sessionStorage</c>. Port addition: the reference's shim kept both areas in the
/// document's realm, so a navigation, even a same-origin reload, started them empty.
/// </summary>
/// <remarks>
/// Thread-safe: frames and pages of one context run on different threads. Quota is
/// Chromium's: about 5 MiB of UTF-16 per storage key (key and value lengths summed),
/// beyond which a set fails with QuotaExceededError and changes nothing.
/// </remarks>
public sealed class WebStorage
{
    /// <summary>Chromium's per-origin quota, in UTF-16 code units of keys plus values.</summary>
    public const long QuotaChars = 5L * 1024 * 1024;

    /// <summary>
    /// Budget across every storage key of this set (upstream 677f6d2: 32 MiB). Per-key quota
    /// alone let iframes on many origins grow a long-lived context without bound.
    /// </summary>
    public const long TotalQuotaChars = 32L * 1024 * 1024;

    /// <summary>Most storage keys one set holds (upstream 677f6d2).</summary>
    public const int MaxAreas = 256;

    private long _totalChars;

    // Storage keys written since the last successful flush (upstream f661c2e / 677f6d2).
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Area> _areas = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private sealed class Area
    {
        public readonly Dictionary<string, string> Items = new(StringComparer.Ordinal);
        // Insertion order, which Chromium's key(n) does not promise but the shim's did.
        public readonly List<string> Order = [];
        public long Chars;
    }

    /// <summary>The area's items, in insertion order, as a JSON array of [key, value] pairs.</summary>
    public string SnapshotJson(string storageKey)
    {
        ArgumentNullException.ThrowIfNull(storageKey);
        lock (_gate)
        {
            if (!_areas.TryGetValue(storageKey, out var area) || area.Order.Count == 0)
            {
                return "[]";
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                foreach (var key in area.Order)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(key);
                    writer.WriteStringValue(area.Items[key]);
                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
            }

            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    /// <summary>One item's value, or null.</summary>
    public string? Get(string storageKey, string key)
    {
        lock (_gate)
        {
            return _areas.TryGetValue(storageKey, out var area) && area.Items.TryGetValue(key, out var value) ? value : null;
        }
    }

    /// <summary>The number of items in the area.</summary>
    public int Count(string storageKey)
    {
        lock (_gate)
        {
            return _areas.TryGetValue(storageKey, out var area) ? area.Order.Count : 0;
        }
    }

    /// <summary>The key at <paramref name="index"/> in insertion order, or null.</summary>
    public string? KeyAt(string storageKey, int index)
    {
        lock (_gate)
        {
            return _areas.TryGetValue(storageKey, out var area) && index >= 0 && index < area.Order.Count
                ? area.Order[index]
                : null;
        }
    }

    /// <summary>Sets one item. False, with nothing changed, when it would exceed the quota.</summary>
    public bool Set(string storageKey, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(storageKey);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            var exists = _areas.TryGetValue(storageKey, out var area);
            if (!exists && _areas.Count >= MaxAreas)
            {
                return false;
            }

            string? old = null;
            if (area is not null && area.Items.TryGetValue(key, out old) && old == value)
            {
                return true;
            }

            long previous = old is null ? 0 : key.Length + old.Length;
            long next = (area?.Chars ?? 0) - previous + key.Length + value.Length;
            long nextTotal = _totalChars - previous + key.Length + value.Length;
            if (next > QuotaChars || nextTotal > TotalQuotaChars)
            {
                return false;
            }

            if (area is null)
            {
                area = new Area();
                _areas[storageKey] = area;
            }

            if (old is null)
            {
                area.Order.Add(key);
            }

            area.Items[key] = value;
            area.Chars = next;
            _totalChars = nextTotal;
            _changed.Add(storageKey);
            return true;
        }
    }

    /// <summary>Removes one item, if present.</summary>
    public void Remove(string storageKey, string key)
    {
        ArgumentNullException.ThrowIfNull(storageKey);
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_areas.TryGetValue(storageKey, out var area) && area.Items.Remove(key, out var old))
            {
                area.Order.Remove(key);
                area.Chars -= key.Length + old.Length;
                _totalChars -= key.Length + old.Length;
                if (area.Order.Count == 0)
                {
                    _areas.Remove(storageKey);
                }
                _changed.Add(storageKey);
            }
        }
    }

    /// <summary>Empties one area.</summary>
    public void Clear(string storageKey)
    {
        ArgumentNullException.ThrowIfNull(storageKey);
        lock (_gate)
        {
            if (_areas.Remove(storageKey, out var area))
            {
                _totalChars -= area.Chars;
                _changed.Add(storageKey);
            }
        }
    }

    /// <summary>The area's items in insertion order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Snapshot(string storageKey)
    {
        lock (_gate)
        {
            return _areas.TryGetValue(storageKey, out var area)
                ? [.. area.Order.Select(key => KeyValuePair.Create(key, area.Items[key]))]
                : [];
        }
    }

    /// <summary>Every non-empty area, ordered by storage key.</summary>
    public IReadOnlyList<KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>>> Entries()
    {
        lock (_gate)
        {
            return [.. _areas.Keys.Order(StringComparer.Ordinal)
                .Select(key => KeyValuePair.Create(key, Snapshot(key)))];
        }
    }

    /// <summary>True while some area changed since its last successful flush.</summary>
    public bool IsDirty
    {
        get { lock (_gate) { return _changed.Count > 0; } }
    }

    /// <summary>The storage keys that changed since their last successful flush, sorted.</summary>
    public IReadOnlyList<string> ChangedKeys()
    {
        lock (_gate)
        {
            return [.. _changed.Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Restore one area from a profile without scheduling a rewrite (upstream <c>load</c>).
    /// False, with nothing changed, for duplicate keys or an area over a budget.
    /// </summary>
    public bool Load(string storageKey, IReadOnlyList<KeyValuePair<string, string>> items)
    {
        ArgumentNullException.ThrowIfNull(storageKey);
        ArgumentNullException.ThrowIfNull(items);
        var area = new Area();
        foreach (var (key, value) in items)
        {
            if (!area.Items.TryAdd(key, value))
            {
                return false;
            }
            area.Order.Add(key);
            area.Chars += key.Length + value.Length;
            if (area.Chars > QuotaChars)
            {
                return false;
            }
        }
        lock (_gate)
        {
            var exists = _areas.TryGetValue(storageKey, out var previous);
            if (items.Count > 0 && !exists && _areas.Count >= MaxAreas)
            {
                return false;
            }
            var nextTotal = _totalChars - (previous?.Chars ?? 0) + area.Chars;
            if (nextTotal > TotalQuotaChars)
            {
                return false;
            }
            if (items.Count == 0)
            {
                _areas.Remove(storageKey);
            }
            else
            {
                _areas[storageKey] = area;
            }
            _totalChars = nextTotal;
            _changed.Remove(storageKey);
            return true;
        }
    }

    /// <summary>
    /// Hand every changed area to <paramref name="write"/> under the store's lock, so no
    /// mutation interleaves; an area is clean again only when its write reports success, and
    /// a failed one is retried by the next flush (upstream 677f6d2).
    /// </summary>
    public void FlushChanged(Func<string, IReadOnlyList<KeyValuePair<string, string>>, bool> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        lock (_gate)
        {
            foreach (var key in _changed.Order(StringComparer.Ordinal).ToList())
            {
                if (write(key, Snapshot(key)))
                {
                    _changed.Remove(key);
                }
            }
        }
    }

    /// <summary>An independent copy with no pending changes (upstream 9a5d9c6).</summary>
    public WebStorage IsolatedSnapshot()
    {
        var copy = new WebStorage();
        lock (_gate)
        {
            foreach (var (key, area) in _areas)
            {
                var copied = new Area { Chars = area.Chars };
                copied.Order.AddRange(area.Order);
                foreach (var (item, value) in area.Items)
                {
                    copied.Items[item] = value;
                }
                copy._areas[key] = copied;
            }
            copy._totalChars = _totalChars;
        }
        return copy;
    }

    /// <summary>
    /// Merge the keys <paramref name="current"/> changed since it was snapshotted from
    /// <paramref name="initial"/>, without replacing values another connection wrote to
    /// untouched keys (upstream 9a5d9c6). False when a changed value did not fit the budget.
    /// </summary>
    public bool MergeChanges(WebStorage initial, WebStorage current)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(current);
        var accepted = true;
        foreach (var storageKey in current.ChangedKeys())
        {
            var before = initial.Snapshot(storageKey);
            var after = current.Snapshot(storageKey);
            var afterKeys = after.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            var beforeValues = before.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            foreach (var (key, _) in before)
            {
                if (!afterKeys.Contains(key))
                {
                    Remove(storageKey, key);
                }
            }
            foreach (var (key, value) in after)
            {
                if (beforeValues.TryGetValue(key, out var previous) && previous == value)
                {
                    continue;
                }
                if (!Set(storageKey, key, value))
                {
                    accepted = false;
                }
            }
        }
        return accepted;
    }
}
