// Port of `HtmlImageRequests` / `HtmlImageCohort` in crates/obscura-render/src/paint.rs
// (upstream a3171f3).
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render;

/// <summary>
/// The HTML image attempts of one document. A failure belongs to an element's current
/// request, not to every future use of its URL; a pending cohort also covers the elements
/// that share one transport load of the same request key.
/// </summary>
/// <remarks>
/// Rust keys a record by <c>NodeId</c> and stores the arena slot's allocation generation
/// beside it (<c>DomTree::node_allocation_generation</c>), so a reused slot does not inherit
/// a failed attempt. A port <see cref="NodeId"/> already carries its slot's generation and a
/// reused slot gets a different id, so the allocation identity is the key itself. Callers
/// hold <see cref="RenderResourceCache"/>'s gate.
/// </remarks>
internal sealed class HtmlImageRequests
{
    /// <summary>Bound on tracked elements (Rust <c>MAX_HTML_IMAGE_REQUESTS</c>).</summary>
    internal const int MaxRequests = 16 * 1024;

    internal sealed class Record(string key, bool failed, ulong? generation)
    {
        public string Key { get; } = key;

        public bool Failed { get; set; } = failed;

        public ulong? Generation { get; } = generation;
    }

    internal sealed class Cohort(string key)
    {
        /// <summary>The shared key instance every member's record points at.</summary>
        public string Key { get; } = key;

        /// <summary>
        /// Members by attempt. Terminal members remain indexed while another transport of
        /// their own attempt may finish; every indexed member also holds one node record.
        /// </summary>
        public Dictionary<ulong, HashSet<NodeId>> Attempts { get; } = [];

        public int Count { get; set; }

        public ulong? Pending { get; set; }

        public bool Started { get; set; }
    }

    private ulong _nextGeneration;

    internal Dictionary<NodeId, Record> Nodes { get; } = [];

    internal Dictionary<string, Cohort> Cohorts { get; } = new(StringComparer.Ordinal);

    internal void Clear()
    {
        Nodes.Clear();
        Cohorts.Clear();
    }

    internal void Forget(NodeId node)
    {
        if (!Nodes.Remove(node, out Record? record))
        {
            return;
        }

        if (!Cohorts.TryGetValue(record.Key, out Cohort? cohort))
        {
            return;
        }

        if (record.Generation is { } generation
            && cohort.Attempts.TryGetValue(generation, out HashSet<NodeId>? members))
        {
            members.Remove(node);
            if (members.Count == 0)
            {
                cohort.Attempts.Remove(generation);
                if (cohort.Pending == generation)
                {
                    cohort.Pending = null;
                    cohort.Started = false;
                }
            }
        }

        cohort.Count--;
        if (cohort.Count == 0)
        {
            Cohorts.Remove(record.Key);
        }
    }

    internal void Observe(NodeId node, string key, bool failed)
    {
        if (Nodes.TryGetValue(node, out Record? existing)
            && string.Equals(existing.Key, key, StringComparison.Ordinal))
        {
            if (existing.Failed == failed)
            {
                return;
            }

            if (failed)
            {
                existing.Failed = true;
                return;
            }
        }

        Forget(node);
        // Keep established attempts stable. Untracked excess elements keep the ordinary
        // bounded byte cache's retry behaviour.
        if (Nodes.Count >= MaxRequests)
        {
            return;
        }

        if (!Cohorts.TryGetValue(key, out Cohort? cohort))
        {
            cohort = new Cohort(key);
            Cohorts[key] = cohort;
        }

        cohort.Count++;
        ulong? generation = null;
        if (!failed)
        {
            if (cohort.Pending is null)
            {
                _nextGeneration = Math.Max(unchecked(_nextGeneration + 1), 1);
                cohort.Pending = _nextGeneration;
            }

            ulong pending = cohort.Pending.Value;
            if (!cohort.Attempts.TryGetValue(pending, out HashSet<NodeId>? members))
            {
                members = [];
                cohort.Attempts[pending] = members;
            }

            members.Add(node);
            generation = pending;
        }

        Nodes[node] = new Record(cohort.Key, failed, generation);
    }

    internal void Complete(string key, ulong? expected, bool failed)
    {
        if (expected is not { } generation
            || !Cohorts.TryGetValue(key, out Cohort? cohort)
            || !cohort.Attempts.Remove(generation, out HashSet<NodeId>? members))
        {
            return;
        }

        foreach (NodeId node in members)
        {
            if (failed)
            {
                if (Nodes.TryGetValue(node, out Record? record))
                {
                    record.Failed = true;
                }
            }
            else if (Nodes.Remove(node))
            {
                cohort.Count--;
            }
        }

        if (cohort.Pending == generation)
        {
            cohort.Pending = null;
            cohort.Started = false;
        }

        if (failed)
        {
            cohort.Attempts[generation] = members;
        }

        if (cohort.Count == 0)
        {
            Cohorts.Remove(key);
        }
    }

    internal void ObserveSuccess(NodeId node)
    {
        // Bytes from another consumer may become visible while this attempt's own
        // transport is still running. Keep its completion membership.
        bool active = Nodes.TryGetValue(node, out Record? record)
            && Cohorts.TryGetValue(record.Key, out Cohort? cohort)
            && cohort.Started
            && cohort.Pending == record.Generation;
        if (!active)
        {
            Forget(node);
        }
    }

    internal ulong? Begin(string key)
    {
        if (!Cohorts.TryGetValue(key, out Cohort? cohort))
        {
            return null;
        }

        if (cohort.Pending is not null)
        {
            cohort.Started = true;
        }

        return cohort.Pending;
    }

    internal ulong? Generation(string key) =>
        Cohorts.TryGetValue(key, out Cohort? cohort) ? cohort.Pending : null;
}
