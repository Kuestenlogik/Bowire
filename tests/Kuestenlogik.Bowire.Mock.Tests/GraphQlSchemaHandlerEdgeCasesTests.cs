// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.GraphQL.Mock;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Coverage for the error / fallback branches in
/// <see cref="GraphQlSchemaHandler"/> — paths the happy-path schema-only
/// tests don't trip: malformed JSON request bodies, parser failures,
/// non-query operation kinds, custom scalar samples, missing query root,
/// fallthrough on non-graphql paths, and the synchronous LoadAsync
/// guard rails. Drives the handler directly via a stub
/// <see cref="HttpContext"/> so the full mock-server pipeline isn't in the
/// way of the failure modes we're asserting.
/// </summary>
public sealed class GraphQlSchemaHandlerEdgeCasesTests : IDisposable
{
    private readonly string _tempDir;

    public GraphQlSchemaHandlerEdgeCasesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-graphql-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ThrowsFileNotFound()
    {
        var path = Path.Combine(_tempDir, "no-such-file.graphql");
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            GraphQlSchemaHandler.LoadAsync(path, NullLogger.Instance, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_NullPath_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            GraphQlSchemaHandler.LoadAsync("", NullLogger.Instance, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryHandleAsync_NonPostMethod_FallsThrough()
    {
        var handler = await LoadHandler("type Query { ping: String }");
        var ctx = BuildContext("GET", "/graphql", body: "");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_NonGraphqlPath_FallsThrough()
    {
        var handler = await LoadHandler("type Query { ping: String }");
        var ctx = BuildContext("POST", "/other", body: """{"query":"{ping}"}""");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_MalformedJson_RespondsWithErrorEnvelope()
    {
        var handler = await LoadHandler("type Query { ping: String }");
        var ctx = BuildContext("POST", "/graphql", body: "{ broken");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Contains("not valid JSON", ReadResponseBody(ctx), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_MissingQueryField_RespondsWithErrorEnvelope()
    {
        var handler = await LoadHandler("type Query { ping: String }");
        var ctx = BuildContext("POST", "/graphql", body: """{"variables":{}}""");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Contains("missing a 'query' field", ReadResponseBody(ctx), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_WhitespaceQuery_RespondsWithErrorEnvelope()
    {
        var handler = await LoadHandler("type Query { ping: String }");
        var ctx = BuildContext("POST", "/graphql", body: """{"query":"   "}""");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Contains("missing a 'query' field", ReadResponseBody(ctx), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_QueryParseError_SurfacesEscapedMessage()
    {
        var handler = await LoadHandler("type Query { ping: String }");
        // Newlines / quotes in the parser error must be JSON-escaped before
        // they're embedded in the response envelope; otherwise the body
        // would be invalid JSON itself.
        var ctx = BuildContext("POST", "/graphql", body: "{\"query\":\"{\\\"weird\\\":\\n\"}");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(handled);
        var body = ReadResponseBody(ctx);
        Assert.Contains("Query parse error", body, StringComparison.Ordinal);
        // Body must round-trip as valid JSON despite the special chars.
        using var _ = JsonDocument.Parse(body);
    }

    [Fact]
    public async Task TryHandleAsync_NoOperation_RespondsWithErrorEnvelope()
    {
        var handler = await LoadHandler("type Query { ping: String }");
        // A document containing only a fragment definition has no
        // OperationDefinition — the handler must return the dedicated
        // "no operation found" error.
        var ctx = BuildContext("POST", "/graphql",
            body: """{"query":"fragment F on Query { ping }"}""");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Contains("No operation found", ReadResponseBody(ctx), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_SubscriptionKind_ReportsNotSupported()
    {
        var handler = await LoadHandler("type Query { ping: String }");
        var ctx = BuildContext("POST", "/graphql",
            body: """{"query":"subscription { ping }"}""");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Contains("Query operations only", ReadResponseBody(ctx), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_SchemaWithoutQueryRoot_RespondsNoQueryRoot()
    {
        // schema { mutation: M } intentionally drops the query root entry —
        // the handler must fall through to the "Schema has no Query root"
        // error rather than crashing on a missing dictionary key.
        var handler = await LoadHandler("""
            schema { mutation: M }
            type M { noop: String }
            """);

        var ctx = BuildContext("POST", "/graphql", body: """{"query":"{ ping }"}""");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Contains("no Query root type", ReadResponseBody(ctx), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_FieldNotInParent_EmitsNullForKey()
    {
        // The selection asks for `missing` which is not a field on the
        // Query root. The handler must emit `null` rather than crashing.
        var handler = await LoadHandler("type Query { ping: String }");
        var ctx = BuildContext("POST", "/graphql", body: """{"query":"{ ping missing }"}""");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(handled);
        var body = ReadResponseBody(ctx);
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("missing").ValueKind);
    }

    [Fact]
    public async Task TryHandleAsync_InterfaceFieldRendersNull()
    {
        // Interfaces / unions can't be sample-resolved without runtime
        // type knowledge — the handler returns null for such fields so the
        // client doesn't read undefined.
        var handler = await LoadHandler("""
            type Query { node: Node }
            interface Node { id: ID! }
            """);
        var ctx = BuildContext("POST", "/graphql", body: """{"query":"{ node }"}""");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(handled);
        var body = ReadResponseBody(ctx);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("data").GetProperty("node").ValueKind);
    }

    [Fact]
    public async Task TryHandleAsync_CustomScalars_GetTypeAppropriateSamples()
    {
        // Each branch of ScalarSample — scalars not declared in the schema
        // resolve as built-in scalar names and pick up the matching sample.
        var handler = await LoadHandler("""
            type Query {
              created: DateTime
              uuid: UUID
              email: Email
              url: URL
              json: JSON
              custom: SomeUnknown
            }
            """);
        var ctx = BuildContext("POST", "/graphql",
            body: """{"query":"{ created uuid email url json custom }"}""");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(handled);
        var body = ReadResponseBody(ctx);
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("2026-01-01T00:00:00Z", data.GetProperty("created").GetString());
        Assert.Equal("00000000-0000-0000-0000-000000000000", data.GetProperty("uuid").GetString());
        Assert.Equal("sample@example.com", data.GetProperty("email").GetString());
        Assert.Equal("https://example.com", data.GetProperty("url").GetString());
        Assert.Equal("{}", data.GetProperty("json").GetString());
        // Unknown scalar falls back to "sample".
        Assert.Equal("sample", data.GetProperty("custom").GetString());
    }

    [Fact]
    public async Task TryHandleAsync_ExampleDirective_Wins_Over_Type_Sample()
    {
        // #559: a field-level @example(value: ...) is served in preference to
        // the type-driven "sample" / 1 / etc.
        var handler = await LoadHandler("""
            directive @example(value: String) on FIELD_DEFINITION
            type Query {
              status: String @example(value: "shipped")
              count: Int @example(value: 7)
              plain: String
            }
            """);
        var ctx = BuildContext("POST", "/graphql",
            body: """{"query":"{ status count plain }"}""");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(handled);
        var body = ReadResponseBody(ctx);
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("shipped", data.GetProperty("status").GetString());
        Assert.Equal(7, data.GetProperty("count").GetInt32());
        // A field without @example still gets the type-driven sample.
        Assert.Equal("sample", data.GetProperty("plain").GetString());
    }

    [Fact]
    public async Task TryHandleAsync_ExampleDirective_Null_Serves_Json_Null()
    {
        // #559: @example(value: null) serves an explicit JSON null — the
        // declared example wins over the non-null "sample" type default.
        var handler = await LoadHandler("""
            directive @example(value: String) on FIELD_DEFINITION
            type Query {
              note: String @example(value: null)
            }
            """);
        var ctx = BuildContext("POST", "/graphql", body: """{"query":"{ note }"}""");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(handled);
        using var doc = JsonDocument.Parse(ReadResponseBody(ctx));
        Assert.Equal(JsonValueKind.Null,
            doc.RootElement.GetProperty("data").GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task TryHandleAsync_ExampleDirective_Object_Served_Verbatim()
    {
        // #559: an object @example is served exactly as declared (the operator
        // said what to serve; it bypasses the type-driven object generation).
        var handler = await LoadHandler("""
            directive @example(value: String) on FIELD_DEFINITION
            type User { name: String }
            type Query {
              user: User @example(value: { name: "Ada", role: "admin" })
            }
            """);
        var ctx = BuildContext("POST", "/graphql", body: """{"query":"{ user { name } }"}""");

        var handled = await handler.TryHandleAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(handled);
        using var doc = JsonDocument.Parse(ReadResponseBody(ctx));
        var user = doc.RootElement.GetProperty("data").GetProperty("user");
        Assert.Equal("Ada", user.GetProperty("name").GetString());
        Assert.Equal("admin", user.GetProperty("role").GetString());
    }

    // ---- helpers ----

    private async Task<GraphQlSchemaHandler> LoadHandler(string sdl)
    {
        var path = Path.Combine(_tempDir, $"schema-{Guid.NewGuid():N}.graphql");
        await File.WriteAllTextAsync(path, sdl, TestContext.Current.CancellationToken);
        return await GraphQlSchemaHandler.LoadAsync(path, NullLogger.Instance, TestContext.Current.CancellationToken);
    }

    private static DefaultHttpContext BuildContext(string method, string path, string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadResponseBody(HttpContext ctx)
    {
        var ms = (MemoryStream)ctx.Response.Body;
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public void FlattenType_Null_Falls_Back_To_String()
    {
        // The SDL parser only emits NamedType / ListType / NonNullType, so
        // the default arm at the end of FlattenType is genuinely defensive.
        // Drive it with null — the switch's positive patterns all miss and
        // execution reaches the fallback.
        var (named, isList) = GraphQlSchemaHandler.FlattenType(null);

        Assert.Equal("String", named);
        Assert.False(isList);
    }
}
