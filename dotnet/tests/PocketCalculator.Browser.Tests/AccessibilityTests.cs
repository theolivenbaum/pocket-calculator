using System.Text.Json.Nodes;
using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// The xUnit port of the <c>#[cfg(test)] mod tests</c> in
/// <c>crates/obscura-browser/src/accessibility.rs</c> (upstream f81c296 and 3c56f59; the
/// content-editing test is upstream d17e3dc and is also in the CDP suite).
/// </summary>
public sealed class AccessibilityTests
{
    private static JsonObject AxNodeFor(List<JsonObject> nodes, NodeId backendNodeId) =>
        nodes.Find(node => node["backendDOMNodeId"]!.GetValue<uint>() == backendNodeId.Raw)
        ?? throw new InvalidOperationException("element is present in the AX tree");

    private static NodeId Id(DomTree dom, string id) => dom.GetElementById(id)!.Value;

    private static void AssertAxValue(List<JsonObject> nodes, DomTree dom, string id, string? expected)
    {
        JsonObject node = AxNodeFor(nodes, Id(dom, id));
        string? actual = node.TryGetPropertyValue("value", out JsonNode? value)
            ? value!["value"]!.GetValue<string>()
            : null;
        Assert.True(expected == actual, $"unexpected AX value for #{id}: {actual}");
    }

    private static void AssertName(DomTree dom, string id, string? expected)
    {
        JsonObject node = AxNodeFor(AccessibilityTree.BuildAxNodes(dom), Id(dom, id));
        string? actual = node.TryGetPropertyValue("name", out JsonNode? name)
            ? name!["value"]!.GetValue<string>()
            : null;
        Assert.True(expected == actual, $"#{id}: expected {expected ?? "null"}, got {actual ?? "null"}");
    }

    private static JsonNode? PropertyValue(JsonObject node, string name)
    {
        foreach (JsonNode? property in node["properties"]!.AsArray())
        {
            if (property!["name"]!.GetValue<string>() == name)
            {
                return property["value"]!["value"];
            }
        }

        throw new InvalidOperationException($"missing {name}");
    }

    private static bool InTree(List<JsonObject> nodes, NodeId id) =>
        nodes.Exists(node => node["backendDOMNodeId"]!.GetValue<uint>() == id.Raw);

    /// <summary>
    /// Values measured on Chromium 1243 with Accessibility.getFullAXTree (2026-11-18), where the
    /// Rust reference differs: tristate strings, a select's selected text and collapsed state, a
    /// numeric slider value, no value on a submit input, no focusable on a disabled control, an
    /// empty LabelText name and the paragraph role.
    /// </summary>
    [Fact]
    public void ControlValuesAndRolesMatchChromium()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            "<input id=c type=checkbox checked aria-label=C><select id=s aria-label=Pick><option>One</option><option selected>Two</option></select>"
            + "<input id=r type=range aria-label=Vol value=3 min=0 max=10><input id=g type=submit value=Go>"
            + "<button id=b disabled>B</button><label id=l>L</label><p id=p>P</p>");
        List<JsonObject> nodes = AccessibilityTree.BuildAxNodes(dom);
        JsonObject Node(string id) => AxNodeFor(nodes, Id(dom, id));
        Assert.Equal("true", PropertyValue(Node("c"), "checked")!.GetValue<string>());
        Assert.Equal("""{"type":"string","value":"Two"}""", Node("s")["value"]!.ToJsonString());
        Assert.False(PropertyValue(Node("s"), "expanded")!.GetValue<bool>());
        Assert.Equal("""{"type":"number","value":3}""", Node("r")["value"]!.ToJsonString());
        Assert.Null(Node("g")["value"]);
        Assert.DoesNotContain(
            Node("b")["properties"]!.AsArray(), property => property!["name"]!.GetValue<string>() == "focusable");
        Assert.Equal("LabelText", Node("l")["role"]!["value"]!.GetValue<string>());
        Assert.Equal("", Node("l")["name"]?["value"]?.GetValue<string>() ?? "");
        Assert.Equal("paragraph", Node("p")["role"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void LiveControlStateOverridesInitialAttributes()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            "<input id=c type=checkbox checked><input id=f value=old><input id=p type=password value=secret>");
        NodeId checkbox = Id(dom, "c");
        dom.SetDirtyFormChecked(checkbox, false);
        dom.SetDirtyFormValue(Id(dom, "f"), "fresh");
        List<JsonObject> nodes = AccessibilityTree.BuildAxNodes(dom);
        // Chromium's tristate is a string (deviation from Rust's boolean).
        Assert.Equal("false", PropertyValue(AxNodeFor(nodes, checkbox), "checked")!.GetValue<string>());
        AssertAxValue(nodes, dom, "f", "fresh");
        AssertAxValue(nodes, dom, "p", null);
        dom.SetDirtyFormIndeterminate(checkbox, true);
        nodes = AccessibilityTree.BuildAxNodes(dom);
        Assert.Equal("mixed", PropertyValue(AxNodeFor(nodes, checkbox), "checked")!.GetValue<string>());
    }

    [Fact]
    public void AriaControlStatesPreserveFalseAndMixedValues()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            "<div id=tab role=tab aria-selected=true></div><div id=field role=textbox aria-required=false></div><button id=exp aria-expanded=false aria-pressed=true aria-disabled=false></button><div id=mixed role=checkbox aria-checked=mixed></div>");
        List<JsonObject> nodes = AccessibilityTree.BuildAxNodes(dom);
        foreach ((string id, string name, string expected) in new[]
        {
            ("tab", "selected", "true"), ("field", "required", "false"), ("exp", "expanded", "false"),
            ("exp", "pressed", "true"), ("exp", "disabled", "false"), ("mixed", "checked", "\"mixed\""),
        })
        {
            Assert.True(
                expected == PropertyValue(AxNodeFor(nodes, Id(dom, id)), name)!.ToJsonString(),
                $"#{id}: {name}");
        }
    }

    [Fact]
    public void NamesFromContentPreserveInlineWordsAndNormalizeWhitespace()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """
            <button id="button" title="tooltip"> Save <b>changes</b> now </button>
                        <a id="link" href="/">Read <span>the <em>docs</em></span></a>
                        <h2 id="heading">Access<b>ibility</b>   test</h2>
                        <div id="generic">Do not duplicate all descendant text</div>
            """);
        AssertName(dom, "button", "Save changes now");
        AssertName(dom, "link", "Read the docs");
        AssertName(dom, "heading", "Accessibility test");
        AssertName(dom, "generic", null);
    }

    [Fact]
    public void HtmlLabelsFollowControlAssociationAndDocumentOrder()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """
            <label for="email"> Work <b>email</b> </label>
                        <input id="email" placeholder="fallback"><label for="email">address</label>
                        <label>Accept <span>terms</span><input id="accept" type="checkbox"></label>
                        <label for="email">Elsewhere<input id="wrong"></label>
                        <label>First<input id="first"><input id="second"></label>
                        <label for="missing">No association<input id="missing-for"></label>
            """);
        AssertName(dom, "email", "Work email address Elsewhere");
        AssertName(dom, "accept", "Accept terms");
        AssertName(dom, "wrong", null);
        AssertName(dom, "first", "First");
        AssertName(dom, "second", null);
        AssertName(dom, "missing-for", null);
    }

    [Fact]
    public void AriaLabelledbyPrecedesAriaLabelAndNativeNames()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """
            <span id="one">First</span><span id="two">Second</span>
                        <label for="target">Native</label>
                        <button id="target" aria-labelledby="two missing one two" aria-label="ARIA">Content</button>
                        <button id="invalid" aria-labelledby="missing" aria-label=" ARIA   fallback ">Content</button>
                        <button id="blank" aria-label="  ">Content</button>
                        <span id="empty"></span><button id="empty-ref" aria-labelledby="empty" aria-label="fallback">Content</button>
                        <button id="self" aria-labelledby="self one" aria-label="Self">Content</button>
                        <span id="cycle-a" aria-labelledby="cycle-b">A</span><span id="cycle-b" aria-labelledby="cycle-a">B</span>
                        <button id="cycle" aria-labelledby="cycle-a">Content</button>
            """);
        AssertName(dom, "target", "Second First");
        AssertName(dom, "invalid", "ARIA fallback");
        AssertName(dom, "blank", "Content");
        AssertName(dom, "empty-ref", string.Empty);
        AssertName(dom, "self", "Self First");
        AssertName(dom, "cycle", "A");
    }

    [Fact]
    public void HiddenReferencesNameControlsWithoutExposingHiddenNodes()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """
            <span id="hidden" hidden>Hidden <span hidden>reference</span></span>
                        <span id="visible">Visible <span hidden>noise</span></span>
                        <button id="referenced" aria-labelledby="hidden visible">Content</button>
                        <label hidden for="native">Hidden native</label><input id="native">
                        <button id="content">Shown<span aria-hidden="true"> noise</span><script>noise</script></button>
            """);
        AssertName(dom, "referenced", "Hidden reference Visible");
        AssertName(dom, "native", "Hidden native");
        AssertName(dom, "content", "Shown");
        Assert.False(InTree(AccessibilityTree.BuildAxNodes(dom), Id(dom, "hidden")));
    }

    [Fact]
    public void AriaReferencesPreserveLinkText()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """
            <span id="label">Read <a href="/terms">terms</a><a href="/secret" hidden> secret</a></span>
                        <button id="native-link" aria-labelledby="label" aria-label="Fallback">Continue</button>
                        <span id="role-label">Review <span role="link">privacy</span></span>
                        <button id="role-link" aria-labelledby="role-label">Continue</button>
                        <span id="hidden-label" hidden>Read <a href="/terms" hidden>hidden terms</a></span>
                        <button id="hidden-link" aria-labelledby="hidden-label">Continue</button>
            """);
        AssertName(dom, "native-link", "Read terms");
        AssertName(dom, "role-link", "Review privacy");
        AssertName(dom, "hidden-link", "Read hidden terms");
    }

    [Fact]
    public void NativeLabelsPreserveLinkText()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """
            <label for="explicit">Read <a href="/terms">terms</a> and <span role="link">privacy</span></label>
                        <input id="explicit">
                        <label>Accept <a href="/terms">terms</a> and <span role="link">privacy</span><input id="implicit" type="checkbox"></label>
                        <label for="hidden" hidden>Read <a href="/terms" hidden>hidden terms</a></label><input id="hidden">
            """);
        AssertName(dom, "explicit", "Read terms and privacy");
        AssertName(dom, "implicit", "Accept terms and privacy");
        AssertName(dom, "hidden", "Read hidden terms");
    }

    [Fact]
    public void NestedButtonsRemainExcludedFromContentNames()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """<button id="outer">Outer</button><button id="inner" role="link">Nested action</button>""");
        NodeId outer = Id(dom, "outer");
        NodeId inner = Id(dom, "inner");
        dom.AppendChild(outer, inner);
        Assert.Equal(outer, dom.GetNode(inner)!.Parent);
        AssertName(dom, "outer", "Outer");
        AssertName(dom, "inner", "Nested action");
    }

    [Fact]
    public void NestedControlsDoNotPolluteLabelsOrContentNames()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """
            <label for="field">Account <a href="/">Help</a><select><option>Noise</option></select><textarea>Noise</textarea><span role="textbox">Noise</span><span contenteditable="true">Noise</span><a href="/" role="button">Noise</a><button role="link">Noise</button></label>
                        <input id="field"><div role="button" id="button">Open <span role="button">Nested action</span><img alt="folder"></div>
            """);
        AssertName(dom, "field", "Account Help");
        AssertName(dom, "button", "Open folder");
    }

    [Fact]
    public void HtmlAndAriaHiddenSubtreesAreAbsent()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """
            <div hidden="false"><button id="html">Hidden</button></div>
                        <div aria-hidden="TrUe"><button id="aria" aria-hidden="false">Hidden</button></div>
                        <input id="input" type="hidden"><button id="visible" aria-hidden="false">Visible</button>
            """);
        List<JsonObject> nodes = AccessibilityTree.BuildAxNodes(dom);
        foreach (string id in new[] { "html", "aria", "input" })
        {
            Assert.False(InTree(nodes, Id(dom, id)), id);
        }

        AssertName(dom, "visible", "Visible");
    }

    [Fact]
    public void NativeAlternativesAndAriaOverrideContent()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """
            <button id="aria" aria-label="Named">Content</button>
                        <button id="image"><img alt="Download"></button>
                        <img id="decorative" alt="" title="Not a name">
                        <input id="submit" type="submit" value="Send">
                        <input id="password" type="password" value="secret" placeholder="Password">
                        <label for="select">Choice</label><select id="select"><option>Not the name</option></select>
            """);
        AssertName(dom, "aria", "Named");
        AssertName(dom, "image", "Download");
        AssertName(dom, "decorative", string.Empty);
        AssertName(dom, "submit", "Send");
        AssertName(dom, "password", "Password");
        AssertName(dom, "select", "Choice");
    }

    [Fact]
    public void ContentEditingHostsExposeAxValuesWithoutDuplicatingDescendants()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """
            <div id="true" contenteditable="TrUe">true</div>
                           <div id="empty-keyword" contenteditable>empty keyword</div>
                           <div id="plaintext" contenteditable="PlAiNtExT-OnLy">plaintext</div>
                           <div id="empty-host" contenteditable></div>
                           <div id="host" contenteditable="true">host<span id="inherited"> inherited</span><span id="invalid" contenteditable="bogus"> invalid</span><span id="nested-explicit" contenteditable="plaintext-only"> nested</span></div>
                           <div id="disabled" contenteditable="FaLsE">disabled<span id="invalid-disabled" contenteditable="bogus"> inherited off</span><span id="reenabled" contenteditable="TRUE">reenabled<span id="reenabled-child"> child</span></span></div>
            """);
        List<JsonObject> nodes = AccessibilityTree.BuildAxNodes(dom);
        AssertAxValue(nodes, dom, "true", "true");
        AssertAxValue(nodes, dom, "empty-keyword", "empty keyword");
        AssertAxValue(nodes, dom, "plaintext", "plaintext");
        AssertAxValue(nodes, dom, "empty-host", string.Empty);
        AssertAxValue(nodes, dom, "host", "host inherited invalid nested");
        AssertAxValue(nodes, dom, "inherited", null);
        AssertAxValue(nodes, dom, "invalid", null);
        AssertAxValue(nodes, dom, "nested-explicit", null);
        AssertAxValue(nodes, dom, "disabled", null);
        AssertAxValue(nodes, dom, "invalid-disabled", null);
        AssertAxValue(nodes, dom, "reenabled", "reenabled child");
        AssertAxValue(nodes, dom, "reenabled-child", null);
    }
}
