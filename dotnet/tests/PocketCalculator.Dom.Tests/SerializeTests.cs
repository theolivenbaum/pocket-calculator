using PocketCalculator.Dom;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// Port of the <c>#[cfg(test)] mod tests</c> block in crates/obscura-dom/src/serialize.rs.
/// </summary>
public class SerializeTests
{
    [Fact]
    public void TestOuterHtml()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="test"><p>Hello</p></div>""");
        var div = tree.GetElementById("test")!.Value;
        var html = tree.OuterHtml(div);
        Assert.Contains("""<div id="test">""", html, StringComparison.Ordinal);
        Assert.Contains("<p>Hello</p>", html, StringComparison.Ordinal);
        Assert.Contains("</div>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TestInnerHtml()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="test"><p>Hello</p><p>World</p></div>""");
        var div = tree.GetElementById("test")!.Value;
        var html = tree.InnerHtml(div);
        Assert.Contains("<p>Hello</p>", html, StringComparison.Ordinal);
        Assert.Contains("<p>World</p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<div", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TestSerializeAttributes()
    {
        var tree = HtmlParsing.ParseHtml("""<a href="https://example.com" class="link">Click</a>""");
        var a = tree.QuerySelector("a")!.Value;
        var html = tree.OuterHtml(a);
        Assert.Contains("href=\"https://example.com\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"link\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TestSerializeSpecialChars()
    {
        var tree = HtmlParsing.ParseHtml("<p>Hello &amp; World &lt;3</p>");
        var p = tree.QuerySelector("p")!.Value;
        var html = tree.OuterHtml(p);
        Assert.Contains("&amp;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TestVoidElements()
    {
        var tree = HtmlParsing.ParseHtml("""<img src="test.png"><br>""");
        var img = tree.QuerySelector("img")!.Value;
        var html = tree.OuterHtml(img);
        Assert.Contains("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("</img>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentSerializationNeutralizesAllTerminatorForms()
    {
        // A comment ends on any of "-->", "--!>", a leading ">", or a leading "->".
        // document.createComment(...) accepts arbitrary strings, so a scripted comment can carry
        // each closing form followed by a real tag. Serializing must keep the payload inside a
        // single comment; if it closes early, the trailing "<img>" becomes live markup (mXSS).
        string[] payloads =
        [
            "><img src=x onerror=alert(1)>", // leading ">" abrupt-closes empty comment
            "-><img src=x>",                 // leading "->" abrupt-closes
            "a--!><img src=x>",              // internal "--!>" closes (incorrectly-closed)
            "a--><img src=x>",               // internal "-->" closes (the previously-fixed form)
        ];

        foreach (var payload in payloads)
        {
            var tree = HtmlParsing.ParseHtml("""<div id="host"></div>""");
            var host = tree.GetElementById("host")!.Value;
            var comment = tree.NewNode(NodeData.Comment(payload));
            tree.AppendChild(host, comment);

            var serialized = tree.OuterHtml(host);

            // Re-parsing the serialized markup must not surface an <img>: the payload has to stay
            // inside the comment.
            var reparsed = HtmlParsing.ParseHtml(serialized);
            Assert.True(
                reparsed.QuerySelector("img") is null,
                $"payload {payload} escaped the comment; serialized = {serialized}");

            // And the serialized comment data must carry no raw ">".
            var inner = serialized[(serialized.IndexOf("<!--", StringComparison.Ordinal) + 4)..];
            inner = inner[..inner.IndexOf("-->", StringComparison.Ordinal)];
            Assert.False(
                inner.Contains('>', StringComparison.Ordinal),
                $"comment data still contains a raw '>': {serialized}");
        }
    }

    [Fact]
    public void HostSerializationExcludesItsNativeShadowTree()
    {
        var tree = HtmlParsing.ParseHtml("""<x-card id="host"><span id="light">light</span></x-card>""");
        var host = tree.GetElementById("host")!.Value;
        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        var shadow = tree.NewNode(NodeData.Element(QualName.Html("strong")));
        var shadowText = tree.NewNode(NodeData.Text("shadow"));
        tree.AppendChild(root, shadow);
        tree.AppendChild(shadow, shadowText);

        Assert.Equal("""<span id="light">light</span>""", tree.InnerHtml(host));
        Assert.Equal(
            """<x-card id="host"><span id="light">light</span></x-card>""",
            tree.OuterHtml(host));
        Assert.Equal("<strong>shadow</strong>", tree.InnerHtml(root));
    }

    private static NodeId Element(DomTree tree, string ns, string local, List<PocketCalculator.Dom.Attribute>? attrs = null) =>
        tree.NewNode(new ElementData(new QualName(null, ns, local), attrs ?? [], null, false));

    // upstream 8dbf7e1: attribute_values_and_text_are_escaped_as_the_standard_says
    [Fact]
    public void AttributeValuesAndTextAreEscapedAsTheStandardSays()
    {
        var tree = HtmlParsing.ParseHtml("<a title='&quot;&gt;&lt;img src=x&gt; a&nbsp;b &amp;amp;'>c&nbsp;d</a>");
        const string link = "<a title=\"&quot;&gt;&lt;img src=x&gt; a&nbsp;b &amp;amp;\">c&nbsp;d</a>";
        var out1 = tree.OuterHtml(tree.QuerySelector("a")!.Value);
        Assert.Equal(link, out1);
        var again = HtmlParsing.ParseHtml(out1);
        Assert.Equal(link, again.OuterHtml(again.QuerySelector("a")!.Value));
    }

    // upstream 8dbf7e1: an_attribute_value_cannot_end_a_raw_text_parent
    [Fact]
    public void AnAttributeValueCannotEndARawTextParent()
    {
        var tree = HtmlParsing.ParseHtml("<noscript id=host></noscript>");
        var host = tree.GetElementById("host")!.Value;
        var child = Element(tree, Namespaces.Html, "p",
            [new PocketCalculator.Dom.Attribute(QualName.Attr("title"), "</noscript><img src=x onerror=1>")]);
        tree.AppendChild(host, child);
        var out1 = tree.OuterHtml(host);
        Assert.Equal("<noscript id=\"host\"><p title=\"&lt;/noscript&gt;&lt;img src=x onerror=1&gt;\"></p></noscript>", out1);
        Assert.Null(HtmlParsing.ParseHtml(out1).QuerySelector("img"));
    }

    // upstream cf5067e: elements_that_serialize_as_void_write_no_children_and_no_end_tag
    [Fact]
    public void ElementsThatSerializeAsVoidWriteNoChildrenAndNoEndTag()
    {
        var tree = HtmlParsing.ParseHtml("<span id=host></span>");
        var host = tree.GetElementById("host")!.Value;
        foreach (var tag in new[] { "br", "img", "basefont", "bgsound", "frame", "keygen", "param" })
        {
            var element = Element(tree, Namespaces.Html, tag);
            tree.AppendChild(element, Element(tree, Namespaces.Html, "b"));
            tree.AppendChild(host, element);
            Assert.Equal(string.Empty, tree.InnerHtml(element));
            Assert.Equal($"<{tag}>", tree.OuterHtml(element));
        }

        Assert.Equal("<br><img><basefont><bgsound><frame><keygen><param>", tree.InnerHtml(host));
    }

    // upstream cf5067e: a_void_name_in_another_namespace_is_an_ordinary_element
    [Fact]
    public void AVoidNameInAnotherNamespaceIsAnOrdinaryElement()
    {
        var tree = HtmlParsing.ParseHtml("<span id=host></span>");
        var br = Element(tree, Namespaces.Svg, "br");
        tree.AppendChild(br, Element(tree, Namespaces.Svg, "g"));
        Assert.Equal("<br><g></g></br>", tree.OuterHtml(br));
        Assert.Equal("<g></g>", tree.InnerHtml(br));
    }

    // upstream f2bf4c3: namespaced_attributes_are_written_with_their_fixed_prefix
    [Fact]
    public void NamespacedAttributesAreWrittenWithTheirFixedPrefix()
    {
        var tree = HtmlParsing.ParseHtml("<span id=host></span>");
        static PocketCalculator.Dom.Attribute A(string ns, string? prefix, string local) => new(new QualName(prefix, ns, local), "v");
        var svg = Element(tree, Namespaces.Svg, "svg",
        [
            A(Namespaces.Xml, "abc", "lang"),
            A(Namespaces.XLink, null, "href"),
            A(Namespaces.XmlNs, null, "xmlns"),
            A(Namespaces.XmlNs, "x", "foo"),
            A(Namespaces.None, null, "plain"),
            A("urn:other", "o", "k"),
        ]);
        Assert.Equal("<svg xml:lang=\"v\" xlink:href=\"v\" xmlns=\"v\" xmlns:foo=\"v\" plain=\"v\" o:k=\"v\"></svg>", tree.OuterHtml(svg));
    }
}
