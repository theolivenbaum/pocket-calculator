namespace PocketCalculator.Dom;

/// <summary>
/// The legacy table and select collections (upstream d8f1467, 25700b9, 59276a3): which rows,
/// sections, cells and options an HTML table or select owns, read from the tree in one op
/// call so the shim does not walk the children itself.
/// </summary>
public sealed partial class DomTree
{
    /// <summary>The local name of an HTML-namespace element, or null for anything else.</summary>
    private string? HtmlLocalName(NodeId id) =>
        Slot(id)?.Data is ElementData { Name: { Ns: Namespaces.Html } name } ? name.Local : null;

    /// <summary>
    /// HTML table rows: header sections first, body and direct rows in tree order, footer
    /// sections last. Nested tables do not contribute rows. Null when
    /// <paramref name="table"/> is not an HTML table.
    /// </summary>
    public List<NodeId>? TableRows(NodeId table)
    {
        if (HtmlLocalName(table) != "table")
        {
            return null;
        }

        var head = new List<NodeId>();
        var body = new List<NodeId>();
        List<NodeId>? foot = null;
        var steps = _nodes.Count;
        for (var child = Slot(table)!.FirstChild; child is { } id && steps-- > 0; child = Slot(id)?.NextSibling)
        {
            List<NodeId> rows;
            switch (HtmlLocalName(id))
            {
                case "tr":
                    body.Add(id);
                    continue;
                case "thead":
                    rows = head;
                    break;
                case "tbody":
                    rows = body;
                    break;
                case "tfoot":
                    rows = foot ??= [];
                    break;
                default:
                    continue;
            }

            AddHtmlChildren(id, rows, "tr", null);
        }

        head.AddRange(body);
        if (foot is not null)
        {
            head.AddRange(foot);
        }

        return head;
    }

    /// <summary>Appends the HTML children of <paramref name="parent"/> named <paramref name="a"/> or <paramref name="b"/>.</summary>
    private void AddHtmlChildren(NodeId parent, List<NodeId> into, string a, string? b)
    {
        var steps = _nodes.Count;
        for (var child = Slot(parent)?.FirstChild; child is { } id && steps-- > 0; child = Slot(id)?.NextSibling)
        {
            var local = HtmlLocalName(id);
            if (local is not null && (local == a || local == b))
            {
                into.Add(id);
            }
        }
    }

    /// <summary>The table interface an HTML element has, or null.</summary>
    public string? TableInterface(NodeId node) => HtmlLocalName(node) switch
    {
        "table" => "HTMLTableElement",
        "thead" or "tbody" or "tfoot" => "HTMLTableSectionElement",
        "tr" => "HTMLTableRowElement",
        "td" or "th" => "HTMLTableCellElement",
        _ => null,
    };

    /// <summary>The table's child sections named <paramref name="section"/> (thead, tbody or tfoot).</summary>
    public List<NodeId>? TableSections(NodeId table, string section)
    {
        if (section is not ("thead" or "tbody" or "tfoot") || HtmlLocalName(table) != "table")
        {
            return null;
        }

        var result = new List<NodeId>();
        AddHtmlChildren(table, result, section, null);
        return result;
    }

    /// <summary>A table section's child rows.</summary>
    public List<NodeId>? TableSectionRows(NodeId section)
    {
        if (HtmlLocalName(section) is not ("thead" or "tbody" or "tfoot"))
        {
            return null;
        }

        var result = new List<NodeId>();
        AddHtmlChildren(section, result, "tr", null);
        return result;
    }

    /// <summary>A row's child cells.</summary>
    public List<NodeId>? TableRowCells(NodeId row)
    {
        if (HtmlLocalName(row) != "tr")
        {
            return null;
        }

        var result = new List<NodeId>();
        AddHtmlChildren(row, result, "td", "th");
        return result;
    }

    /// <summary>
    /// A row's <c>rowIndex</c>, or its <c>sectionRowIndex</c> when
    /// <paramref name="sectionOnly"/>; -1 when the row is in no table or section. Null when
    /// <paramref name="row"/> is not an HTML row.
    /// </summary>
    public int? TableRowIndex(NodeId row, bool sectionOnly)
    {
        if (HtmlLocalName(row) != "tr")
        {
            return null;
        }

        if (Slot(row)!.Parent is not { } parent)
        {
            return -1;
        }

        var rows = HtmlLocalName(parent) switch
        {
            "table" => TableRows(parent),
            "thead" or "tbody" or "tfoot" when sectionOnly => TableSectionRows(parent),
            "thead" or "tbody" or "tfoot" => Slot(parent)!.Parent is { } table ? TableRows(table) : null,
            _ => null,
        };
        return rows?.IndexOf(row) ?? -1;
    }

    /// <summary>A cell's <c>cellIndex</c>: -1 outside a row, null when not an HTML cell.</summary>
    public int? TableCellIndex(NodeId cell)
    {
        if (HtmlLocalName(cell) is not ("td" or "th"))
        {
            return null;
        }

        var cells = Slot(cell)!.Parent is { } row ? TableRowCells(row) : null;
        return cells?.IndexOf(cell) ?? -1;
    }

    /// <summary>
    /// The options an HTML select owns: its descendants in tree order, not entering a nested
    /// select, datalist or option. Null when <paramref name="select"/> is not an HTML select,
    /// or when the walk exceeds the node count (a cyclic tree).
    /// </summary>
    public List<NodeId>? SelectOptions(NodeId select)
    {
        if (HtmlLocalName(select) != "select")
        {
            return null;
        }

        var options = new List<NodeId>();
        var remaining = _nodes.Count;
        var node = Slot(select)!.FirstChild;
        while (node is { } id)
        {
            if (remaining-- == 0)
            {
                Console.Error.WriteLine("pocket-calculator: select-options traversal cap hit - tree has a cycle");
                return null;
            }

            var local = HtmlLocalName(id);
            var descend = false;
            if (local == "option")
            {
                options.Add(id);
            }
            else if (local is not ("select" or "datalist"))
            {
                descend = true;
            }

            // Pre-order step within the select's subtree.
            var current = Slot(id)!;
            if (descend && current.FirstChild is { } first)
            {
                node = first;
                continue;
            }

            node = null;
            for (var at = id; at != select;)
            {
                var slot = Slot(at);
                if (slot is null)
                {
                    break;
                }

                if (slot.NextSibling is { } next)
                {
                    node = next;
                    break;
                }

                if (slot.Parent is not { } up || remaining-- == 0)
                {
                    break;
                }

                at = up;
            }
        }

        return options;
    }

    /// <summary>
    /// An option's <c>index</c> within the select that owns it, 0 when no select does. Null
    /// when <paramref name="option"/> is not an HTML option, or the ancestor walk exceeds the
    /// node count.
    /// </summary>
    public int? OptionIndex(NodeId option)
    {
        if (HtmlLocalName(option) != "option")
        {
            return null;
        }

        var remaining = _nodes.Count;
        for (var parent = Slot(option)!.Parent; parent is { } id; parent = Slot(id)?.Parent)
        {
            if (remaining-- == 0)
            {
                Console.Error.WriteLine("pocket-calculator: option-index traversal cap hit - tree has a cycle");
                return null;
            }

            switch (HtmlLocalName(id))
            {
                case "select":
                    var options = SelectOptions(id);
                    return options is null ? null : Math.Max(0, options.IndexOf(option));
                case "datalist":
                case "option":
                    return 0;
            }
        }

        return 0;
    }
}
