// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.GraphQL;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for the synchronous private helpers inside
/// <see cref="BowireGraphQLProtocol"/>: subscription-transport
/// header extraction, http→ws scheme rewrite, request-body parsing,
/// variable extraction, the stub-method builder, and the JSON-kind
/// inference used when reverse-engineering an operation from the
/// runtime variables. Async network paths stay covered by the
/// integration-style tests; this file lifts the pure-function
/// surface that doesn't need a live GraphQL server.
/// </summary>
public sealed class GraphQLHelperTests
{
    // ---- Identity / metadata surface ----

    [Fact]
    public void Identity_Properties_Are_Stable()
    {
        var protocol = new BowireGraphQLProtocol();

        Assert.Equal("GraphQL", protocol.Name);
        Assert.Equal("graphql", protocol.Id);
        Assert.NotNull(protocol.IconSvg);
        Assert.Contains("<svg", protocol.IconSvg, StringComparison.Ordinal);
    }

    [Fact]
    public void SubscriptionTransportMetadataKey_Is_Public_Constant()
    {
        Assert.Equal("X-Bowire-GraphQL-Subscription-Transport",
            BowireGraphQLProtocol.SubscriptionTransportMetadataKey);
    }

    [Fact]
    public void Initialize_Accepts_Null_ServiceProvider()
    {
        var protocol = new BowireGraphQLProtocol();

        protocol.Initialize(null);
    }

    // ---- Inert async surface ----

    [Fact]
    public async Task DiscoverAsync_Empty_Url_Returns_Empty()
    {
        var protocol = new BowireGraphQLProtocol();

        var services = await protocol.DiscoverAsync(
            "", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task OpenChannelAsync_Returns_Null()
    {
        // GraphQL plugin always streams through InvokeStreamAsync; the
        // channel API is intentionally inert.
        var protocol = new BowireGraphQLProtocol();

        var channel = await protocol.OpenChannelAsync(
            "http://example.com/graphql", "Query", "user",
            showInternalServices: false, metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(channel);
    }

    // ---- ExtractTransportPreference (private static) ----

    [Fact]
    public void ExtractTransportPreference_Null_Metadata_Returns_Both_Null()
    {
        var (pref, headers) = InvokeExtractTransportPreference(null);

        Assert.Null(pref);
        Assert.Null(headers);
    }

    [Fact]
    public void ExtractTransportPreference_No_Magic_Key_Returns_Original_Headers()
    {
        var meta = new Dictionary<string, string> { ["Authorization"] = "Bearer x" };

        var (pref, headers) = InvokeExtractTransportPreference(meta);

        Assert.Null(pref);
        Assert.NotNull(headers);
        Assert.Equal("Bearer x", headers!["Authorization"]);
    }

    [Theory]
    [InlineData("ws", "ws")]
    [InlineData("WS", "ws")]
    [InlineData("Ws", "ws")]
    [InlineData("sse", "sse")]
    [InlineData("SSE", "sse")]
    [InlineData("Sse", "sse")]
    public void ExtractTransportPreference_Normalises_Recognised_Aliases(string raw, string normalised)
    {
        var meta = new Dictionary<string, string>
        {
            [BowireGraphQLProtocol.SubscriptionTransportMetadataKey] = raw
        };

        var (pref, headers) = InvokeExtractTransportPreference(meta);

        Assert.Equal(normalised, pref);
        Assert.NotNull(headers);
        // Magic key was stripped — only Authorization etc. would remain.
        Assert.False(headers!.ContainsKey(BowireGraphQLProtocol.SubscriptionTransportMetadataKey));
    }

    [Fact]
    public void ExtractTransportPreference_Unknown_Value_Passes_Through_Trimmed()
    {
        // Lets future transports (e.g. graphql-multipart) be tried via
        // metadata without code change — `NormalizePreference` just trims.
        var meta = new Dictionary<string, string>
        {
            [BowireGraphQLProtocol.SubscriptionTransportMetadataKey] = "  custom  "
        };

        var (pref, _) = InvokeExtractTransportPreference(meta);

        Assert.Equal("custom", pref);
    }

    [Fact]
    public void ExtractTransportPreference_Case_Insensitive_Header_Match()
    {
        var meta = new Dictionary<string, string>
        {
            ["x-bowire-graphql-subscription-transport"] = "ws"
        };

        var (pref, headers) = InvokeExtractTransportPreference(meta);

        Assert.Equal("ws", pref);
        Assert.NotNull(headers);
        Assert.Empty(headers!);
    }

    // ---- HttpToWs (private static) ----

    [Theory]
    [InlineData("http://example.com/graphql", "ws://example.com/graphql")]
    [InlineData("https://example.com/graphql", "wss://example.com/graphql")]
    [InlineData("HTTP://EXAMPLE.COM", "ws://EXAMPLE.COM")]
    [InlineData("HTTPS://EXAMPLE.COM", "wss://EXAMPLE.COM")]
    public void HttpToWs_Maps_Http_Schemes_To_Ws_Schemes(string input, string expected)
    {
        Assert.Equal(expected, InvokeHttpToWs(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ws://x")]
    [InlineData("wss://x")]
    [InlineData("ftp://x")]
    public void HttpToWs_Leaves_Other_Schemes_And_Empty_Untouched(string input)
    {
        Assert.Equal(input, InvokeHttpToWs(input));
    }

    // ---- TryParseFullRequest (private static) ----

    [Fact]
    public void TryParseFullRequest_Empty_List_Returns_False()
    {
        Assert.False(InvokeTryParseFullRequest([], out var query));
        Assert.Equal("", query);
    }

    [Fact]
    public void TryParseFullRequest_Whitespace_Body_Returns_False()
    {
        Assert.False(InvokeTryParseFullRequest(["   "], out var query));
        Assert.Equal("", query);
    }

    [Fact]
    public void TryParseFullRequest_Returns_Query_When_Present()
    {
        Assert.True(InvokeTryParseFullRequest(
            ["{\"query\":\"{ user { id } }\",\"variables\":{}}"], out var query));
        Assert.Equal("{ user { id } }", query);
    }

    [Fact]
    public void TryParseFullRequest_Missing_Query_Field_Returns_False()
    {
        Assert.False(InvokeTryParseFullRequest(
            ["{\"variables\":{\"id\":1}}"], out var query));
        Assert.Equal("", query);
    }

    [Fact]
    public void TryParseFullRequest_Non_String_Query_Returns_False()
    {
        Assert.False(InvokeTryParseFullRequest(
            ["{\"query\":42}"], out var query));
        Assert.Equal("", query);
    }

    [Fact]
    public void TryParseFullRequest_Non_Object_Root_Returns_False()
    {
        Assert.False(InvokeTryParseFullRequest(["[\"oops\"]"], out _));
    }

    [Fact]
    public void TryParseFullRequest_Empty_Query_String_Returns_False()
    {
        Assert.False(InvokeTryParseFullRequest(["{\"query\":\"\"}"], out _));
    }

    [Fact]
    public void TryParseFullRequest_Malformed_Json_Returns_False()
    {
        // The catch swallows JsonException and yields a clean failure
        // signal so the caller can fall back to the discovered-method
        // operation builder.
        Assert.False(InvokeTryParseFullRequest(["{ broken"], out _));
    }

    // ---- ExtractVariables (private static) ----

    [Fact]
    public void ExtractVariables_Returns_Variables_Object()
    {
        var v = InvokeExtractVariables(
            ["{\"query\":\"{x}\",\"variables\":{\"id\":42,\"flag\":true}}"]);

        Assert.Equal(JsonValueKind.Object, v.ValueKind);
        Assert.Equal(42, v.GetProperty("id").GetInt32());
        Assert.True(v.GetProperty("flag").GetBoolean());
    }

    [Fact]
    public void ExtractVariables_Missing_Variables_Returns_Default()
    {
        var v = InvokeExtractVariables(["{\"query\":\"{x}\"}"]);

        Assert.Equal(JsonValueKind.Undefined, v.ValueKind);
    }

    [Fact]
    public void ExtractVariables_Non_Object_Variables_Returns_Default()
    {
        // The spec only allows objects under `variables` — anything else
        // is treated as absent so the operation still runs with zero args.
        var v = InvokeExtractVariables(["{\"query\":\"{x}\",\"variables\":[1,2]}"]);

        Assert.Equal(JsonValueKind.Undefined, v.ValueKind);
    }

    [Fact]
    public void ExtractVariables_Malformed_Json_Returns_Default()
    {
        var v = InvokeExtractVariables(["{ broken"]);

        Assert.Equal(JsonValueKind.Undefined, v.ValueKind);
    }

    // ---- BuildStubInput (private static) ----

    [Fact]
    public void BuildStubInput_Builds_Field_Per_Variable()
    {
        var msg = InvokeBuildStubInput("{\"id\":42,\"name\":\"Alice\",\"active\":true,\"price\":9.99,\"tags\":[1,2]}");

        Assert.Equal("Variables", msg.Name);
        Assert.Equal(5, msg.Fields.Count);

        var idField = msg.Fields.Single(f => f.Name == "id");
        Assert.Equal("int64", idField.Type);
        Assert.False(idField.IsRepeated);

        var priceField = msg.Fields.Single(f => f.Name == "price");
        Assert.Equal("double", priceField.Type);

        var nameField = msg.Fields.Single(f => f.Name == "name");
        Assert.Equal("string", nameField.Type);

        var activeField = msg.Fields.Single(f => f.Name == "active");
        Assert.Equal("bool", activeField.Type);

        var tagsField = msg.Fields.Single(f => f.Name == "tags");
        Assert.Equal("string", tagsField.Type);
        Assert.True(tagsField.IsRepeated);
    }

    [Fact]
    public void BuildStubInput_Malformed_Json_Returns_Empty_Fields()
    {
        var msg = InvokeBuildStubInput("{ broken");

        Assert.Empty(msg.Fields);
    }

    [Fact]
    public void BuildStubInput_Non_Object_Json_Returns_Empty_Fields()
    {
        var msg = InvokeBuildStubInput("[1,2,3]");

        Assert.Empty(msg.Fields);
    }

    [Fact]
    public void BuildStubInput_Object_Variable_Is_Message_Type()
    {
        var msg = InvokeBuildStubInput("{\"filter\":{\"min\":1,\"max\":10}}");

        var filter = Assert.Single(msg.Fields);
        Assert.Equal("filter", filter.Name);
        Assert.Equal("message", filter.Type);
    }

    // ---- InferGraphQLType (private static) ----

    [Theory]
    [InlineData("true", JsonValueKind.True, "bool", false)]
    [InlineData("false", JsonValueKind.False, "bool", false)]
    [InlineData("42", JsonValueKind.Number, "int64", false)]
    [InlineData("3.14", JsonValueKind.Number, "double", false)]
    [InlineData("\"hello\"", JsonValueKind.String, "string", false)]
    [InlineData("[1,2]", JsonValueKind.Array, "string", true)]
    [InlineData("{\"k\":1}", JsonValueKind.Object, "message", false)]
    [InlineData("null", JsonValueKind.Null, "string", false)]
    public void InferGraphQLType_Maps_Each_JsonValueKind(string json, JsonValueKind expectedKind, string expectedType, bool expectedRepeated)
    {
        using var doc = JsonDocument.Parse(json);
        var element = doc.RootElement.Clone();
        Assert.Equal(expectedKind, element.ValueKind);

        var (type, repeated) = InvokeInferGraphQLType(element);

        Assert.Equal(expectedType, type);
        Assert.Equal(expectedRepeated, repeated);
    }

    // ---- Reflection plumbing ----

    private static (string? Preference, Dictionary<string, string>? Headers)
        InvokeExtractTransportPreference(Dictionary<string, string>? metadata)
    {
        var method = typeof(BowireGraphQLProtocol).GetMethod(
            "ExtractTransportPreference", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, [metadata])!;
        var tuple = (System.Runtime.CompilerServices.ITuple)result;
        return ((string?)tuple[0], (Dictionary<string, string>?)tuple[1]);
    }

    private static string InvokeHttpToWs(string url) =>
        (string)typeof(BowireGraphQLProtocol)
            .GetMethod("HttpToWs", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [url])!;

    private static bool InvokeTryParseFullRequest(List<string> jsonMessages, out string query)
    {
        var args = new object?[] { jsonMessages, "" };
        var result = (bool)typeof(BowireGraphQLProtocol)
            .GetMethod("TryParseFullRequest", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args)!;
        query = (string)args[1]!;
        return result;
    }

    private static JsonElement InvokeExtractVariables(List<string> jsonMessages) =>
        (JsonElement)typeof(BowireGraphQLProtocol)
            .GetMethod("ExtractVariables", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [jsonMessages])!;

    private static BowireMessageInfo InvokeBuildStubInput(string variablesJson) =>
        (BowireMessageInfo)typeof(BowireGraphQLProtocol)
            .GetMethod("BuildStubInput", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [variablesJson])!;

    private static (string Type, bool Repeated) InvokeInferGraphQLType(JsonElement value)
    {
        var result = typeof(BowireGraphQLProtocol)
            .GetMethod("InferGraphQLType", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [value])!;
        var tuple = (System.Runtime.CompilerServices.ITuple)result;
        return ((string)tuple[0]!, (bool)tuple[1]!);
    }
}
