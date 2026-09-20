// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;

namespace Kuestenlogik.Bowire.Protocol.Soap.Tests;

/// <summary>
/// The workbench form's JSON on its way into a SOAP envelope.
/// </summary>
/// <remarks>
/// <para>
/// Found by driving the SOAP sample's workbench in a browser: filling
/// <c>a = 2</c> and <c>b = 3</c> on <c>Calculator/Add</c> and pressing
/// Execute answered <c>&lt;result&gt;0&lt;/result&gt;</c> with HTTP 200. The
/// form collected the arguments, the browser sent them, and the envelope
/// dropped them.
/// </para>
/// <para>
/// The cause is an ordering one, which is why it survived: the XML branch
/// *accepts* JSON. <c>&lt;root&gt;{"a":2}&lt;/root&gt;</c> is well-formed XML
/// whose content is a text node, so the payload became
/// <c>&lt;Add&gt;{"a":2,"b":3}&lt;/Add&gt;</c>, the server found none of its
/// parts and computed from defaults. A wrong number, never an error — no
/// status code to gate on, which is why the sample smoke test (it asserts
/// only that an invoke is not 404/405/5xx) reported the sample clean.
/// </para>
/// </remarks>
public sealed class SoapFormPayloadTests
{
    private const string Ns = "http://example.com/calc";

    private static XElement Operation(string json, string ns = Ns)
    {
        var envelope = XDocument.Parse(
            SoapEnvelopeBuilder.BuildRequestEnvelope("Add", ns, json, "1.1"));
        return envelope.Descendants().First(e => e.Name.LocalName == "Add");
    }

    private static string? Part(XElement op, string name)
        => op.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    [Fact]
    public void The_Forms_Arguments_Become_The_Operations_Parts()
    {
        // The case from the browser, exactly.
        var op = Operation("""{"a":"2","b":"3"}""");

        Assert.Equal("2", Part(op, "a"));
        Assert.Equal("3", Part(op, "b"));
    }

    [Fact]
    public void The_Payload_Is_Not_Left_As_Text_Inside_The_Operation()
    {
        // The shape of the bug, asserted directly: a server reading child
        // elements must not find one text node where the parts should be.
        var op = Operation("""{"a":"2","b":"3"}""");

        Assert.Equal(2, op.Elements().Count());
        Assert.DoesNotContain("{", op.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Part_Carries_The_Operations_Namespace()
    {
        // Document/literal puts the parts in the target namespace. Writing
        // them unqualified is what a hand-rolled mock tolerates and a real
        // stack rejects — the sample would have passed either way.
        var op = Operation("""{"a":"2"}""");

        Assert.Equal(XNamespace.Get(Ns) + "a", op.Elements().Single().Name);
    }

    [Fact]
    public void A_Namespaceless_Service_Gets_Namespaceless_Parts()
    {
        var op = Operation("""{"a":"2"}""", ns: "");

        Assert.Equal(XNamespace.None + "a", op.Elements().Single().Name);
    }

    [Fact]
    public void Numbers_And_Booleans_Travel_As_Their_Own_Text()
    {
        // A JSON editor sends real types where the form sends strings; the
        // envelope is text either way and must not print `"2"` with quotes.
        var op = Operation("""{"count":2,"flag":true,"ratio":1.5}""");

        Assert.Equal("2", Part(op, "count"));
        Assert.Equal("true", Part(op, "flag"));
        Assert.Equal("1.5", Part(op, "ratio"));
    }

    [Fact]
    public void A_Nested_Object_Nests()
    {
        var op = Operation("""{"order":{"id":"7","qty":"2"}}""");

        var order = op.Elements().Single();
        Assert.Equal("order", order.Name.LocalName);
        Assert.Equal("7", order.Elements().First(e => e.Name.LocalName == "id").Value);
    }

    [Fact]
    public void A_List_Repeats_Its_Element_Name()
    {
        // How a WSDL expresses maxOccurs > 1.
        var op = Operation("""{"item":["a","b","c"]}""");

        Assert.Equal(["a", "b", "c"], op.Elements().Select(e => e.Value));
        Assert.All(op.Elements(), e => Assert.Equal("item", e.Name.LocalName));
    }

    [Fact]
    public void A_Blank_Part_Is_Sent_Empty_Rather_Than_Dropped()
    {
        // "named it and left it blank" is not "never mentioned it", and a
        // blank form field means the first one.
        var op = Operation("""{"a":null,"b":""}""");

        Assert.Equal(2, op.Elements().Count());
        Assert.Equal("", Part(op, "a"));
        Assert.Equal("", Part(op, "b"));
    }

    [Fact]
    public void An_Empty_Object_Sends_A_Bare_Operation()
    {
        // The no-arguments method, and what the smoke test drives.
        var op = Operation("{}");

        Assert.Empty(op.Elements());
        Assert.Equal("", op.Value);
    }

    // ---- the XML path is still there ----

    [Fact]
    public void A_Hand_Written_Xml_Fragment_Still_Goes_Through_Verbatim()
    {
        // Callers who want control of namespace and part order had this and
        // must keep it; the JSON branch only claims payloads starting with
        // a brace.
        var op = Operation("<a>2</a><b>3</b>");

        Assert.Equal("2", Part(op, "a"));
        Assert.Equal("3", Part(op, "b"));
    }

    [Fact]
    public void Text_That_Is_Neither_Stays_The_Operations_Content()
    {
        var op = Operation("just some text");

        Assert.Empty(op.Elements());
        Assert.Equal("just some text", op.Value);
    }

    [Fact]
    public void A_Payload_That_Only_Looks_Like_Json_Falls_Back_Rather_Than_Failing()
    {
        // Starts with a brace, is not JSON. Refusing the whole call would be
        // worse than sending what the caller typed.
        var op = Operation("{not json at all");

        Assert.Equal("{not json at all", op.Value);
    }

    [Fact]
    public void A_Json_Array_Is_Not_An_Argument_List()
    {
        // Only an object names its parts. An array has nothing to call them,
        // so it takes the text path rather than inventing names.
        var op = Operation("""["a","b"]""");

        Assert.Empty(op.Elements());
    }

    [Fact]
    public void A_Key_That_Cannot_Be_An_Element_Name_Is_Skipped_Not_Fatal()
    {
        // A JSON key is any string; an XML name is not. The rest of the
        // payload still has to reach the server.
        var op = Operation("""{"ok":"1","not a name":"2","":"3"}""");

        Assert.Equal("1", Part(op, "ok"));
        Assert.Single(op.Elements());
    }
}
