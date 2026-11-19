using System.Text.Json.Nodes;

using PocketCalculator.Dom;

using BrowserPage = PocketCalculator.Browser.Page;

namespace PocketCalculator.Cdp.Domains;

/// <summary>
/// The nodes one session's <c>DOM.getDocument</c> published, and whether each one's children
/// went with it (upstream 717dd92). A node id is only handed to a session that holds its
/// parent's children, as Chromium's DOM agent only reports ids it has pushed.
/// </summary>
internal sealed class DomDocument(string pageId, string? sessionId)
{
    internal string PageId { get; } = pageId;

    internal string? SessionId { get; } = sessionId;

    internal Dictionary<NodeId, bool> ChildrenSent { get; } = [];

    /// <summary>Records every node of a serialized tree, shadow roots included.</summary>
    internal void Remember(JsonNode? root)
    {
        var pending = new Stack<JsonNode?>();
        pending.Push(root);
        while (pending.TryPop(out JsonNode? node))
        {
            if (node is not JsonObject value)
            {
                continue;
            }

            if (value["nodeId"].AsU64() is { } id && id <= uint.MaxValue)
            {
                ChildrenSent[NodeId.New((uint)id)] =
                    value["children"] is JsonArray || value["childNodeCount"].AsI64() == 0;
            }

            foreach (string key in (ReadOnlySpan<string>)["children", "shadowRoots"])
            {
                if (value[key] is JsonArray items)
                {
                    foreach (JsonNode? item in items)
                    {
                        pending.Push(item);
                    }
                }
            }
        }
    }
}

public static partial class Dom
{
    /// <summary>The <see cref="CdpContext.DomDocuments"/> key of a session; "" for none.</summary>
    internal static string DocumentKey(string? sessionId) => sessionId ?? string.Empty;

    private static NodeId NodeIdParam(JsonNode? parameters) =>
        parameters.Get("nodeId").AsU64() is { } id && id <= uint.MaxValue
            ? NodeId.New((uint)id)
            : throw new DomainError("valid nodeId required");

    /// <summary>
    /// The parent a node is pushed under: its parent, or for a shadow root its host.
    /// </summary>
    /// <remarks>
    /// DEVIATION from upstream 717dd92, which only follows parent links and so refuses every
    /// node in a shadow tree ("outside the requested light-DOM document"). Chromium pushes the
    /// path through the shadow root (measured: <c>DOM.requestNode</c> on an element inside an
    /// open shadow root emits <c>setChildNodes</c> for the root's children, with the root as
    /// <c>parentId</c>), and the port lists shadow roots in its trees, so it does the same.
    /// </remarks>
    private static NodeId? PushParent(DomTree dom, NodeId node) =>
        dom.GetNode(node)?.Parent is { } parent ? parent
            : dom.ShadowRootInfo(node) is { } info ? info.Host
            : null;

    /// <summary>
    /// Sends the children of <paramref name="node"/> and of its known descendants down to
    /// <paramref name="depth"/>, each parent's once (upstream 717dd92 <c>push_children</c>).
    /// </summary>
    private static void PushChildren(
        DomTree dom,
        DomDocument state,
        NodeId node,
        uint depth,
        bool pierce,
        object pinOwner,
        List<(NodeId Parent, JsonArray Nodes)> pushes)
    {
        var pending = new Stack<(NodeId Node, uint Depth)>();
        pending.Push((node, Math.Min(depth, MaxSerializeDepth)));
        while (pending.TryPop(out (NodeId Node, uint Depth) item))
        {
            WorkCancellation.ThrowIfCancellationRequested();
            (NodeId parent, uint remaining) = item;
            if (!state.ChildrenSent.TryGetValue(parent, out bool sent))
            {
                continue;
            }

            List<NodeId> children = dom.Children(parent);
            if (!sent)
            {
                var nodes = new JsonArray();
                foreach (NodeId child in children)
                {
                    JsonNode? value = SerializeNode(dom, child, remaining - 1, 0, pinOwner, pierce);
                    state.Remember(value);
                    nodes.Add(value);
                }

                state.ChildrenSent[parent] = true;
                pushes.Add((parent, nodes));
            }
            else if (remaining > 1)
            {
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    pending.Push((children[i], remaining - 1));
                }

                // Port addition with the shadowRoots above: a pierce request descends into
                // the shadow roots the session knows, as Chromium's does.
                if (pierce && dom.ShadowRootOf(parent) is { } root)
                {
                    pending.Push((root, remaining - 1));
                }
            }
        }
    }

    /// <summary>
    /// Publishes a node to the session (upstream 717dd92 <c>push_node</c>): 0 when the session
    /// holds no document, as Chromium answers before <c>DOM.getDocument</c>; otherwise the
    /// path from the document down to it is pushed, once, and its id returned.
    /// </summary>
    private static ulong PushNode(CdpContext ctx, string? sessionId, BrowserPage page, NodeId node)
    {
        if (!ctx.DomDocuments.TryGetValue(DocumentKey(sessionId), out DomDocument? state))
        {
            return 0;
        }

        List<(NodeId Parent, JsonArray Nodes)> pushes = [];
        (bool Loaded, ulong Id, string? Error) result;
        try
        {
            result = page.WithDom(dom =>
            {
                if (dom.GetNode(node) is null)
                {
                    return (true, 0UL, "No node with given id found");
                }

                if (!state.ChildrenSent.ContainsKey(node))
                {
                    List<NodeId> path = [];
                    for (NodeId? current = PushParent(dom, node); current is { } id; current = PushParent(dom, id))
                    {
                        path.Add(id);
                        if (path.Count > dom.Count)
                        {
                            break;
                        }
                    }

                    if (path.Count == 0 || path[^1] != dom.Document)
                    {
                        return (true, 0UL, "Node is outside the requested light-DOM document");
                    }

                    for (int i = path.Count - 1; i >= 0; i--)
                    {
                        PushChildren(dom, state, path[i], 1, false, ctx, pushes);
                    }

                    if (!state.ChildrenSent.ContainsKey(node))
                    {
                        // Upstream's shortcut, kept: without DOM mutation events a node
                        // inserted after its parent's children were sent has no published id.
                        return (true, 0UL, "Node requires DOM mutation events; request the document again");
                    }
                }

                return (true, (ulong)node.Raw, (string?)null);
            });
        }
        finally
        {
            // Whatever was pushed is now recorded as sent, so it is sent even on an error.
            EmitPushes(ctx, page, sessionId, pushes);
        }

        if (!result.Loaded)
        {
            throw new DomainError("No DOM loaded");
        }

        return result.Error is { } error ? throw new DomainError(error) : result.Id;
    }

    private static void EmitPushes(
        CdpContext ctx,
        BrowserPage page,
        string? sessionId,
        List<(NodeId Parent, JsonArray Nodes)> pushes)
    {
        foreach ((NodeId parent, JsonArray nodes) in pushes)
        {
            ctx.PendingEvents.Add(new CdpEvent
            {
                Method = "DOM.setChildNodes",
                Params = new JsonObject { ["parentId"] = (ulong)parent.Raw, ["nodes"] = nodes },
                SessionId = sessionId,
            });
        }
    }

    /// <summary>
    /// <c>DOM.requestChildNodes</c> with <c>pierce</c>: an iframe in the requested subtree,
    /// or a shadow root the session was never given, cannot be served, and must not be
    /// silently left out (upstream 717dd92).
    /// </summary>
    /// <remarks>
    /// DEVIATION from upstream 717dd92, which refuses every shadow root here. The port lists
    /// shadow roots in its trees and descends into them with <c>pierce</c>, as Chromium does;
    /// only a root attached after its host was sent is refused, since without
    /// <c>DOM.shadowRootPushed</c> the session has no id for it.
    /// </remarks>
    private static string? UnservedPierceRoot(DomTree dom, DomDocument? state, NodeId node, uint depth)
    {
        var pending = new Stack<(NodeId Node, uint Depth)>();
        pending.Push((node, Math.Min(depth, MaxSerializeDepth)));
        while (pending.TryPop(out (NodeId Node, uint Depth) item))
        {
            WorkCancellation.ThrowIfCancellationRequested();
            (NodeId id, uint remaining) = item;
            if (dom.GetNode(id)?.AsElement() is { } element
                && element.Name.Ns == Namespaces.Html
                && element.Name.Local is "iframe" or "frame")
            {
                return "Piercing frame trees is not supported";
            }

            if (dom.ShadowRootOf(id) is { } root)
            {
                if (state is null || !state.ChildrenSent.ContainsKey(root))
                {
                    return "Node requires DOM mutation events; request the document again";
                }

                if (remaining > 0)
                {
                    pending.Push((root, remaining - 1));
                }
            }

            if (remaining > 0)
            {
                foreach (NodeId child in dom.Children(id))
                {
                    pending.Push((child, remaining - 1));
                }
            }
        }

        return null;
    }
}
