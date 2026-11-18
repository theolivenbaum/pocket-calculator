using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PocketCalculator.Dom;
using PocketCalculator.Js.Runtime;

namespace PocketCalculator.Browser;

/// <summary>
/// Shared accessibility names, visibility and control state for the CDP Accessibility domain
/// and the MCP snapshot tools. Port of <c>crates/obscura-browser/src/accessibility.rs</c>
/// (upstream 3c56f59, which moved it out of <c>obscura-cdp</c>; the naming and visibility
/// rules are upstream f81c296).
/// </summary>
public static class AccessibilityTree
{
    /// <summary>Walk the full DOM tree and produce the CDP Accessibility AXNode array.</summary>
    public static List<JsonObject> BuildAxNodes(DomTree dom) =>
        BuildAxNodesWithStyles(dom, EmptyStyles);

    internal static readonly IReadOnlyDictionary<NodeId, AccessibilityStyle> EmptyStyles =
        new Dictionary<NodeId, AccessibilityStyle>();

    /// <summary>
    /// The AX tree of the page's live document, with the cascade's <c>display</c> and
    /// <c>visibility</c> hiding what CSS hides.
    /// </summary>
    public static List<JsonObject> BuildAxNodes(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        IReadOnlyDictionary<NodeId, AccessibilityStyle> styles = AccessibilityContext.StylesOf(page);
        return page.WithDom(dom => BuildAxNodesWithStyles(dom, styles)) ?? [];
    }

    /// <summary>Build the visible AX tree using optional computed styles from the renderer.</summary>
    public static List<JsonObject> BuildAxNodesWithStyles(
        DomTree dom,
        IReadOnlyDictionary<NodeId, AccessibilityStyle> styles)
    {
        ArgumentNullException.ThrowIfNull(dom);
        ArgumentNullException.ThrowIfNull(styles);
        var nodes = new List<JsonObject>();
        uint idCounter = 0;
        // DOM NodeId -> AX string id, populated only for nodes actually in the AX tree.
        var domToAx = new Dictionary<uint, string>();

        NodeId document = dom.Document;
        var allDomIds = new List<NodeId> { document };
        allDomIds.AddRange(dom.Descendants(document));
        var context = new AccessibilityContext(dom, allDomIds, styles);

        // First pass: assign AX ids only to the visible nodes that produce an AX node.
        var eligible = new List<NodeId>();
        foreach (NodeId domId in allDomIds)
        {
            if (dom.GetNode(domId) is { } node
                && AccessibilityContext.MapRole(node.Data).Length != 0
                && !context.IsHidden(domId))
            {
                idCounter++;
                domToAx[domId.Raw] = idCounter.ToString(CultureInfo.InvariantCulture);
                eligible.Add(domId);
            }
        }

        // Second pass: an AXNode for each eligible node.
        foreach (NodeId domId in eligible)
        {
            if (BuildAxNode(dom, domId, domToAx, context) is { } ax)
            {
                nodes.Add(ax);
            }
        }

        // childIds are the inverse of parentId (upstream f81c296), so a node whose DOM parent
        // is hidden is still listed by the AX ancestor it names as its parent.
        var children = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (JsonObject node in nodes)
        {
            if (node.TryGetPropertyValue("parentId", out JsonNode? parent) && parent is not null)
            {
                string parentId = parent.GetValue<string>();
                if (!children.TryGetValue(parentId, out JsonArray? ids))
                {
                    ids = [];
                    children[parentId] = ids;
                }

                ids.Add(node["nodeId"]!.GetValue<string>());
            }
        }

        foreach (JsonObject node in nodes)
        {
            if (children.Remove(node["nodeId"]!.GetValue<string>(), out JsonArray? ids))
            {
                // Before backendDOMNodeId, where the port has always put it.
                JsonNode? backend = node["backendDOMNodeId"];
                node.Remove("backendDOMNodeId");
                node["childIds"] = ids;
                node["backendDOMNodeId"] = backend;
            }
        }

        return nodes;
    }

    private static JsonObject? BuildAxNode(
        DomTree dom,
        NodeId nodeId,
        Dictionary<uint, string> domToAx,
        AccessibilityContext context)
    {
        if (!domToAx.TryGetValue(nodeId.Raw, out string? axId)
            || context.Describe(dom, nodeId) is not { } description)
        {
            return null;
        }

        // parentId: walk DOM ancestors until one is in the AX tree.
        string? parentId = null;
        NodeId current = nodeId;
        while (dom.GetNode(current)?.Parent is { } parent)
        {
            if (domToAx.TryGetValue(parent.Raw, out string? axParentId))
            {
                parentId = axParentId;
                break;
            }

            current = parent;
        }

        // Only the non-empty optional fields; per the CDP spec an optional field is omitted
        // when empty. Rust's serde_json map sorts the keys; the port keeps its insertion order.
        var axNode = new JsonObject
        {
            ["nodeId"] = axId,
            ["ignored"] = false,
            ["role"] = AccessibilityContext.AxValueRole(description.Role),
        };
        if (parentId is not null)
        {
            axNode["parentId"] = parentId;
        }

        description.AddFields(axNode);
        axNode["backendDOMNodeId"] = nodeId.Raw;
        return axNode;
    }
}

/// <summary>
/// Names and visibility for one unchanged document, without AX node serialization. Port of
/// <c>AccessibilityContext</c> in <c>crates/obscura-browser/src/accessibility.rs</c>.
/// </summary>
public sealed class AccessibilityContext
{
    private readonly struct Visibility(bool subtreeHidden, bool visibilityHidden)
    {
        public bool SubtreeHidden { get; } = subtreeHidden;

        public bool VisibilityHidden { get; } = visibilityHidden;
    }

    /// <summary>A described node: role, name, value and properties.</summary>
    internal sealed record Description(string Role, string? Name, string? Value, List<JsonObject> Properties)
    {
        internal void AddFields(JsonObject target)
        {
            if (Name is not null)
            {
                target["name"] = AxValueString(Name);
            }

            if (Value is not null)
            {
                target["value"] = AxValueString(Value);
            }

            if (Properties.Count != 0)
            {
                var array = new JsonArray();
                foreach (JsonObject property in Properties)
                {
                    array.Add(property);
                }

                target["properties"] = array;
            }
        }
    }

    private readonly Dictionary<NodeId, Visibility> _visibility = [];
    private readonly Dictionary<string, NodeId> _elementsById = new(StringComparer.Ordinal);
    private readonly Dictionary<NodeId, List<NodeId>> _labels = [];

    /// <summary>The context for every node of <paramref name="dom"/>.</summary>
    public static AccessibilityContext ForDocument(
        DomTree dom,
        IReadOnlyDictionary<NodeId, AccessibilityStyle> styles)
    {
        ArgumentNullException.ThrowIfNull(dom);
        ArgumentNullException.ThrowIfNull(styles);
        var ids = new List<NodeId> { dom.Document };
        ids.AddRange(dom.Descendants(dom.Document));
        return new AccessibilityContext(dom, ids, styles);
    }

    /// <summary>
    /// The context for the page's live document, with CSS visibility from its runtime. Port of
    /// <c>accessibility_context</c> in <c>crates/obscura-mcp/src/lib.rs</c>; <c>null</c> when
    /// the page has no document.
    /// </summary>
    public static AccessibilityContext? ForPage(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        IReadOnlyDictionary<NodeId, AccessibilityStyle> styles = StylesOf(page);
        return page.WithDom(dom => ForDocument(dom, styles));
    }

    internal static IReadOnlyDictionary<NodeId, AccessibilityStyle> StylesOf(Page page) =>
        page.Js is { } js ? js.AccessibilityStyles() : AccessibilityTree.EmptyStyles;

    internal AccessibilityContext(
        DomTree dom,
        List<NodeId> ids,
        IReadOnlyDictionary<NodeId, AccessibilityStyle> styles)
    {
        foreach (NodeId id in ids)
        {
            if (dom.GetNode(id) is not { } node)
            {
                continue;
            }

            Visibility parent = node.Parent is { } parentId
                && _visibility.TryGetValue(parentId, out Visibility parentVisibility)
                ? parentVisibility
                : default;
            AccessibilityStyle style = styles.TryGetValue(id, out AccessibilityStyle found) ? found : default;
            string tag = Tag(node);
            bool hidden = node.GetAttribute("hidden") is not null
                || (node.GetAttribute("aria-hidden") is { } ariaHidden
                    && ariaHidden.Equals("true", StringComparison.OrdinalIgnoreCase))
                || tag is "head" or "script" or "style" or "template" or "noscript"
                || (tag == "input" && InputType(node) == "hidden");
            _visibility[id] = new Visibility(
                parent.SubtreeHidden || hidden || style.DisplayNone,
                style.VisibilityHidden ?? parent.VisibilityHidden);
            if (node.GetAttribute("id") is { Length: > 0 } value)
            {
                _elementsById.TryAdd(value, id);
            }
        }

        foreach (NodeId id in ids)
        {
            if (dom.GetNode(id) is not { } node || Tag(node) != "label")
            {
                continue;
            }

            NodeId? control = null;
            if (node.GetAttribute("for") is { } target)
            {
                if (_elementsById.TryGetValue(target, out NodeId targetId)
                    && dom.GetNode(targetId) is { } targetNode
                    && IsLabelable(targetNode))
                {
                    control = targetId;
                }
            }
            else
            {
                foreach (NodeId descendant in dom.Descendants(id))
                {
                    if (dom.GetNode(descendant) is { } candidate && IsLabelable(candidate))
                    {
                        control = descendant;
                        break;
                    }
                }
            }

            if (control is { } controlId)
            {
                if (!_labels.TryGetValue(controlId, out List<NodeId>? labels))
                {
                    labels = [];
                    _labels[controlId] = labels;
                }

                labels.Add(id);
            }
        }
    }

    /// <summary>
    /// Whether the node is excluded: inside a <c>hidden</c>, <c>aria-hidden="true"</c> or
    /// <c>display: none</c> subtree, or <c>visibility: hidden</c> itself.
    /// </summary>
    public bool IsHidden(NodeId id) =>
        _visibility.TryGetValue(id, out Visibility state) && (state.SubtreeHidden || state.VisibilityHidden);

    private bool IsSubtreeHidden(NodeId id) =>
        _visibility.TryGetValue(id, out Visibility state) && state.SubtreeHidden;

    /// <summary>
    /// Describe only the requested visible node: <c>backendDOMNodeId</c>, <c>role</c> and the
    /// optional <c>name</c>, <c>value</c> and <c>properties</c>. <c>null</c> for a hidden node
    /// or one with no role. CDP adds the tree links separately.
    /// </summary>
    public JsonObject? DescribeNode(DomTree dom, NodeId id)
    {
        ArgumentNullException.ThrowIfNull(dom);
        if (Describe(dom, id) is not { } description)
        {
            return null;
        }

        var result = new JsonObject
        {
            ["backendDOMNodeId"] = id.Raw,
            ["role"] = AxValueRole(description.Role),
        };
        description.AddFields(result);
        return result;
    }

    internal Description? Describe(DomTree dom, NodeId id)
    {
        if (IsHidden(id) || dom.GetNode(id) is not { } node)
        {
            return null;
        }

        string role = MapRole(node.Data);
        if (role.Length == 0)
        {
            return null;
        }

        return new Description(role, ComputeName(dom, node), ComputeValue(dom, node), ComputeProperties(dom, node));
    }

    // ===== AXValue builders =====

    internal static JsonObject AxValueRole(string role) => new() { ["type"] = "role", ["value"] = role };

    private static JsonObject AxValueString(string value) => new() { ["type"] = "string", ["value"] = value };

    private static JsonObject AxValueBoolean(bool value) => new() { ["type"] = "boolean", ["value"] = value };

    private static JsonObject AxValueInteger(uint value) => new() { ["type"] = "integer", ["value"] = value };

    private static JsonObject Property(string name, JsonObject value) => new() { ["name"] = name, ["value"] = value };

    // ===== roles =====

    /// <summary>Map an HTML element tag to an ARIA role value.</summary>
    internal static string MapRole(NodeData data)
    {
        switch (data)
        {
            case DocumentData:
                return "RootWebArea";
            case TextData:
                return "StaticText";
            case ElementData element:
            {
                string tag = element.Name.Local;
                List<PocketCalculator.Dom.Attribute> attrs = element.Attrs;

                // The explicit role attribute first.
                if (FindAttribute(attrs, "role") is { } roleAttr)
                {
                    return roleAttr.Value switch
                    {
                        "button" => "button",
                        "link" => "link",
                        "heading" => "heading",
                        "textbox" or "searchbox" => "textbox",
                        "checkbox" => "checkbox",
                        "radio" => "radio",
                        "listbox" => "listbox",
                        "combobox" => "combobox",
                        "list" => "list",
                        "listitem" => "listitem",
                        "navigation" => "navigation",
                        "banner" => "banner",
                        "main" => "main",
                        "complementary" => "complementary",
                        "contentinfo" => "contentinfo",
                        "form" => "form",
                        "table" => "table",
                        "row" => "row",
                        "cell" or "gridcell" => "cell",
                        "img" => "image",
                        "dialog" => "dialog",
                        "alert" => "alert",
                        "tab" => "tab",
                        "tablist" => "tablist",
                        "tabpanel" => "tabpanel",
                        "menu" => "menu",
                        "menuitem" => "menuitem",
                        "toolbar" => "toolbar",
                        "separator" => "separator",
                        // presentation/none roles get the role but content is still in tree.
                        "presentation" or "none" => "presentation",
                        _ => "generic",
                    };
                }

                return tag switch
                {
                    "a" => FindAttribute(attrs, "href") is not null ? "link" : "generic",
                    "button" or "summary" => "button",
                    "input" => (FindAttribute(attrs, "type")?.Value ?? "text") switch
                    {
                        "submit" or "reset" or "button" or "image" => "button",
                        "checkbox" => "checkbox",
                        "radio" => "radio",
                        "range" => "slider",
                        "number" => "spinbutton",
                        "search" => "searchbox",
                        _ => "textbox",
                    },
                    "textarea" => "textbox",
                    "select" => FindAttribute(attrs, "multiple") is not null
                        || FindAttribute(attrs, "size") is not null
                        ? "listbox"
                        : "combobox",
                    "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => "heading",
                    "img" or "svg" => "image",
                    "ul" or "ol" or "menu" => "list",
                    "li" => "listitem",
                    "table" => "table",
                    "tr" => "row",
                    "td" or "th" => "cell",
                    "nav" => "navigation",
                    "header" => "banner",
                    "main" => "main",
                    "footer" => "contentinfo",
                    "form" => "form",
                    "dialog" => "dialog",
                    "hr" => "separator",
                    "label" => "LabelText",
                    "article" => "article",
                    "aside" => "complementary",
                    "section" => "region",
                    "figure" => "figure",
                    "figcaption" => "StaticText",
                    "iframe" => "Iframe",
                    _ => "generic",
                };
            }

            default:
                // Doctype, Comment, ProcessingInstruction.
                return string.Empty;
        }
    }

    // ===== helpers =====

    private static string Tag(Node node) => node.AsElement()?.Name.Local ?? string.Empty;

    private static string InputType(Node node) => (node.GetAttribute("type") ?? "text").ToLowerInvariant();

    private static bool IsLabelable(Node node)
    {
        string tag = Tag(node);
        return tag is "button" or "meter" or "output" or "progress" or "select" or "textarea"
            || (tag == "input" && InputType(node) != "hidden");
    }

    private static bool IsNestedControl(Node node) =>
        Tag(node) is "input" or "textarea" or "select" or "button" or "summary" or "iframe"
        || ContentEditableKeyword(node) == true
        || MapRole(node.Data) is "button" or "textbox" or "searchbox" or "checkbox" or "radio"
            or "listbox" or "combobox" or "slider" or "spinbutton" or "menuitem" or "tab";

    /// <summary>Rust's <c>split_whitespace().join(" ")</c>.</summary>
    private static string FlatString(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length != 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string? NonEmpty(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string flat = FlatString(value);
        return flat.Length != 0 ? flat : null;
    }

    private static string? TextAlternative(Node node)
    {
        if (NonEmpty(node.GetAttribute("aria-label")) is { } label)
        {
            return label;
        }

        string tag = Tag(node);
        if (tag == "img" || (tag == "input" && InputType(node) == "image"))
        {
            return node.GetAttribute("alt") is { } alt ? FlatString(alt) : null;
        }

        if (tag == "input")
        {
            switch (InputType(node))
            {
                case "button":
                    return NonEmpty(node.GetAttribute("value"));
                case "submit":
                    return node.GetAttribute("value") is { } submit ? FlatString(submit) : "Submit";
                case "reset":
                    return node.GetAttribute("value") is { } reset ? FlatString(reset) : "Reset";
            }
        }

        return null;
    }

    private string ContentName(DomTree dom, NodeId root, bool includeHidden)
    {
        var text = new StringBuilder();
        var pending = new Stack<(NodeId Id, bool EndBlock)>();
        pending.Push((root, false));
        while (pending.Count != 0)
        {
            WorkCancellation.ThrowIfCancellationRequested();
            (NodeId id, bool endBlock) = pending.Pop();
            if (endBlock)
            {
                text.Append(' ');
                continue;
            }

            if (dom.GetNode(id) is not { } node)
            {
                continue;
            }

            if (!includeHidden && IsSubtreeHidden(id))
            {
                continue;
            }

            string tag = Tag(node);
            if (tag is "script" or "style" or "template" or "noscript")
            {
                continue;
            }

            if (id != root && IsNestedControl(node))
            {
                continue;
            }

            bool hidden = !includeHidden && IsHidden(id);
            if (node.Data is TextData textData)
            {
                if (!hidden)
                {
                    text.Append(textData.Contents);
                }

                continue;
            }

            if (!hidden && TextAlternative(node) is { } alternative)
            {
                text.Append(' ').Append(alternative).Append(' ');
                continue;
            }

            if (tag is "br" or "p" or "div" or "li" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            {
                text.Append(' ');
                pending.Push((id, true));
            }

            List<NodeId> children = dom.Children(id);
            for (int i = children.Count - 1; i >= 0; i--)
            {
                pending.Push((children[i], false));
            }
        }

        return FlatString(text.ToString());
    }

    // ===== name, value, properties =====

    /// <summary>Compute the accessible name for a node.</summary>
    private string? ComputeName(DomTree dom, Node node)
    {
        if (node.Data is TextData text)
        {
            return NonEmpty(text.Contents);
        }

        if (node.GetAttribute("aria-labelledby") is { } labelledBy)
        {
            var seen = new HashSet<NodeId>();
            var references = new List<NodeId>();
            foreach (string reference in labelledBy.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (_elementsById.TryGetValue(reference, out NodeId referenced) && seen.Add(referenced))
                {
                    references.Add(referenced);
                }
            }

            if (references.Count != 0)
            {
                var parts = new List<string>(references.Count);
                foreach (NodeId referenced in references)
                {
                    Node? referencedNode = dom.GetNode(referenced);
                    if (referencedNode is not null && TextAlternative(referencedNode) is { } alternative)
                    {
                        parts.Add(alternative);
                        continue;
                    }

                    string content = ContentName(dom, referenced, IsHidden(referenced));
                    if (content.Length != 0)
                    {
                        parts.Add(content);
                        continue;
                    }

                    parts.Add(NonEmpty(referencedNode?.GetAttribute("title")) ?? string.Empty);
                }

                return FlatString(string.Join(' ', parts));
            }
        }

        if (NonEmpty(node.GetAttribute("aria-label")) is { } label)
        {
            return label;
        }

        if (_labels.TryGetValue(node.Id, out List<NodeId>? labels))
        {
            var parts = new List<string>(labels.Count);
            foreach (NodeId labelId in labels)
            {
                parts.Add(ContentName(dom, labelId, IsHidden(labelId)));
            }

            if (NonEmpty(string.Join(' ', parts)) is { } name)
            {
                return name;
            }
        }

        if (TextAlternative(node) is { } textAlternative)
        {
            return textAlternative;
        }

        if (MapRole(node.Data) is "button" or "link" or "heading" or "checkbox" or "radio" or "menuitem"
            or "tab" or "cell" or "row" or "LabelText" or "StaticText"
            && NonEmpty(ContentName(dom, node.Id, includeHidden: false)) is { } contentName)
        {
            return contentName;
        }

        if (NonEmpty(node.GetAttribute("title")) is { } title)
        {
            return title;
        }

        return Tag(node) is "input" or "textarea" ? NonEmpty(node.GetAttribute("placeholder")) : null;
    }

    /// <summary>Compute the accessible value for a node (for example the current input value).</summary>
    private static string? ComputeValue(DomTree dom, Node node)
    {
        if (node.Data is not ElementData element)
        {
            return null;
        }

        string tag = element.Name.Local;
        if (tag is "input" or "textarea" or "select")
        {
            // A password's value is never exposed (upstream 3c56f59).
            if (tag == "input" && InputType(node) == "password")
            {
                return null;
            }

            // The live value script set, then the content attribute. Upstream reads
            // form_control_state; the port's dirty value is the same state (FormStateMirror).
            if (dom.TryGetDirtyFormValue(node.Id, out string dirty))
            {
                return dirty;
            }

            return FindAttribute(element.Attrs, "value")?.Value;
        }

        return IsContentEditingHost(dom, node) ? dom.TextContent(node.Id) : null;
    }

    private static bool? ContentEditableKeyword(Node node)
    {
        if (node.Data is not ElementData element
            || FindAttribute(element.Attrs, "contenteditable") is not { } attr)
        {
            return null;
        }

        string value = attr.Value;
        if (value.Length == 0
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("plaintext-only", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return value.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    private static bool IsEffectivelyContentEditable(DomTree dom, NodeId nodeId)
    {
        NodeId? current = nodeId;
        while (current is { } id)
        {
            if (dom.GetNode(id) is not { } node)
            {
                return false;
            }

            if (ContentEditableKeyword(node) is { } editable)
            {
                return editable;
            }

            current = node.Parent;
        }

        return false;
    }

    private static bool IsContentEditingHost(DomTree dom, Node node) =>
        IsEffectivelyContentEditable(dom, node.Id)
        && (node.Parent is not { } parent || !IsEffectivelyContentEditable(dom, parent));

    /// <summary>"true" / "false" for a boolean ARIA state, null for anything else.</summary>
    private static bool? AriaBoolean(Node node, string attribute) =>
        node.GetAttribute(attribute) switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };

    /// <summary>Compute accessibility properties for a node.</summary>
    private static List<JsonObject> ComputeProperties(DomTree dom, Node node)
    {
        if (node.Data is not ElementData element)
        {
            return [];
        }

        string tag = element.Name.Local;
        List<PocketCalculator.Dom.Attribute> attrs = element.Attrs;
        var props = new List<JsonObject>();

        bool focusable = tag is "a" or "button" or "input" or "select" or "textarea" or "details" or "summary"
            || attrs.Exists(a => a.Name.Local is "tabindex" or "contenteditable");
        if (focusable)
        {
            props.Add(Property("focusable", AxValueBoolean(true)));
        }

        if (tag is "input" or "textarea"
            || attrs.Exists(a => a.Name.Local == "contenteditable"
                && !string.Equals(a.Value, "false", StringComparison.Ordinal)))
        {
            props.Add(Property("editable", AxValueBoolean(true)));
        }

        // checked: the live state of a native checkbox or radio (mixed for an indeterminate
        // checkbox), else aria-checked (upstream 3c56f59).
        string inputType = tag == "input" ? InputType(node) : string.Empty;
        if (inputType is "checkbox" or "radio")
        {
            JsonNode checkedValue = inputType == "checkbox" && dom.IsDirtyFormIndeterminate(node.Id)
                ? JsonValue.Create("mixed")
                : JsonValue.Create(dom.TryGetDirtyFormChecked(node.Id, out bool dirtyChecked)
                    ? dirtyChecked
                    : node.GetAttribute("checked") is not null);
            props.Add(Property("checked", new JsonObject { ["type"] = "tristate", ["value"] = checkedValue }));
        }
        else if (node.GetAttribute("aria-checked") is { } ariaChecked)
        {
            JsonNode? value = ariaChecked switch
            {
                "true" => JsonValue.Create(true),
                "false" => JsonValue.Create(false),
                "mixed" when MapRole(node.Data) == "checkbox" => JsonValue.Create("mixed"),
                _ => null,
            };
            if (value is not null)
            {
                props.Add(Property("checked", new JsonObject { ["type"] = "tristate", ["value"] = value }));
            }
        }

        if (node.GetAttribute("disabled") is not null)
        {
            props.Add(Property("disabled", AxValueBoolean(true)));
        }
        else if (AriaBoolean(node, "aria-disabled") is { } disabled)
        {
            props.Add(Property("disabled", AxValueBoolean(disabled)));
        }

        foreach ((string property, string attribute) in (ReadOnlySpan<(string, string)>)
            [("expanded", "aria-expanded"), ("selected", "aria-selected"), ("pressed", "aria-pressed")])
        {
            if (AriaBoolean(node, attribute) is { } value)
            {
                props.Add(Property(property, AxValueBoolean(value)));
            }
        }

        if (tag.Length > 1
            && tag[0] == 'h'
            && uint.TryParse(tag.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out uint level)
            && level is >= 1 and <= 6)
        {
            props.Add(Property("level", AxValueInteger(level)));
        }

        if (node.GetAttribute("required") is not null)
        {
            props.Add(Property("required", AxValueBoolean(true)));
        }
        else if (AriaBoolean(node, "aria-required") is { } required)
        {
            props.Add(Property("required", AxValueBoolean(required)));
        }

        if (tag == "textarea")
        {
            props.Add(Property("multiline", AxValueBoolean(true)));
        }

        return props;
    }

    private static PocketCalculator.Dom.Attribute? FindAttribute(List<PocketCalculator.Dom.Attribute> attrs, string local)
    {
        for (int i = 0; i < attrs.Count; i++)
        {
            if (string.Equals(attrs[i].Name.Local, local, StringComparison.Ordinal))
            {
                return attrs[i];
            }
        }

        return null;
    }
}
