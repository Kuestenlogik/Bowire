// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.GraphQL;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Bowire's GraphQL client discovering Bowire's own GraphQL mock (#710).
/// </summary>
/// <remarks>
/// <para>
/// This is the round trip that did not work. The mock answered
/// <c>__typename</c> but not <c>__schema</c>, so GraphQL was the one
/// protocol where "start the mock, point the workbench at it, press
/// Discover" came back empty — and empty, because <c>DiscoverAsync</c>
/// treats an errors envelope as "nothing here" so other plugins still get
/// a turn at the same URL. Silence, not a message.
/// </para>
/// <para>
/// Driven end to end rather than against the builder in isolation: the
/// whole point is that the two halves meet. A unit test of the builder
/// would have passed while the handler still never called it.
/// </para>
/// </remarks>
public sealed class GraphQlMockDiscoveryTests : IDisposable
{
    private readonly string _tempDir;

    public GraphQlMockDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-graphql-introspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private const string HarbourSdl = """
        type Query {
          berth(id: ID!): Berth
          berths(limit: Int = 10, status: BerthStatus): [Berth!]!
        }

        type Mutation {
          assignBerth(input: AssignInput!): Berth
        }

        type Berth {
          id: ID!
          name: String!
          status: BerthStatus
          "Kept for the old console."
          code: String @deprecated(reason: "Use name.")
        }

        input AssignInput {
          berthId: ID!
          vesselName: String!
        }

        enum BerthStatus {
          FREE
          OCCUPIED
        }
        """;

    private string WriteSchema(string sdl)
    {
        var path = Path.Combine(_tempDir, "schema.graphql");
        File.WriteAllText(path, sdl);
        return path;
    }

    private static Task<MockServer> StartAsync(string schemaPath, CancellationToken ct)
        => MockServer.StartAsync(
            new MockServerOptions
            {
                GraphQlSchemaPath = schemaPath,
                Port = 0,
                Watch = false,
                SchemaSources = [new GraphQlMockSchemaSource()],
                LiveSchemaHandlers = [new GraphQlMockSchemaSource()],
            },
            ct);

    [Fact]
    public async Task The_Workbench_Can_Discover_The_Mock_It_Just_Started()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartAsync(WriteSchema(HarbourSdl), ct);
        using var protocol = new BowireGraphQLProtocol();

        var services = await protocol.DiscoverAsync(
            $"http://127.0.0.1:{server.Port}/graphql", showInternalServices: false, ct);

        // Query and Mutation, not Subscription: the schema declares no
        // subscription root, and naming one would build a service whose
        // every call fails.
        Assert.Equal(["Query", "Mutation"], services.Select(s => s.Name).ToArray());

        var query = services.Single(s => s.Name == "Query");
        Assert.Contains(query.Methods, m => m.Name == "berth");
        Assert.Contains(query.Methods, m => m.Name == "berths");
    }

    [Fact]
    public async Task An_Argument_Arrives_Typed_And_Knows_Whether_It_Is_Required()
    {
        // This is what the lossy runtime index could not have produced. It
        // flattens a type to a name plus is-a-list and drops arguments, so
        // the introspection answer is built from the SDL tree instead.
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartAsync(WriteSchema(HarbourSdl), ct);
        using var protocol = new BowireGraphQLProtocol();

        var services = await protocol.DiscoverAsync(
            $"http://127.0.0.1:{server.Port}/graphql", showInternalServices: false, ct);

        var berth = services.Single(s => s.Name == "Query").Methods.Single(m => m.Name == "berth");
        var id = Assert.Single(berth.InputType.Fields);
        Assert.Equal("id", id.Name);
        Assert.True(id.Required, "ID! is required");

        var berths = services.Single(s => s.Name == "Query").Methods.Single(m => m.Name == "berths");
        var limit = berths.InputType.Fields.Single(f => f.Name == "limit");
        Assert.False(limit.Required, "Int with a default is optional");
    }

    [Fact]
    public async Task The_Round_Trip_Ends_In_A_Call_That_Answers()
    {
        // Discovery that cannot be acted on is not worth much. Discover a
        // method, invoke it the way the workbench would, get sample data
        // back -- against the same process, over HTTP.
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartAsync(WriteSchema(HarbourSdl), ct);
        using var protocol = new BowireGraphQLProtocol();
        var url = $"http://127.0.0.1:{server.Port}/graphql";

        var services = await protocol.DiscoverAsync(url, showInternalServices: false, ct);
        Assert.NotEmpty(services);

        var result = await protocol.InvokeAsync(
            url, service: "Query", method: "berth",
            jsonMessages: ["""{"query":"query OneBerth { berth(id: \"b1\") { id name } }"}"""],
            showInternalServices: false, ct: ct);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        using var doc = JsonDocument.Parse(result.Response!);
        var berth = doc.RootElement.GetProperty("data").GetProperty("berth");
        Assert.Equal("id-1", berth.GetProperty("id").GetString());
        Assert.Equal("sample", berth.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Deprecation_And_Enum_Values_Survive_The_Trip()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartAsync(WriteSchema(HarbourSdl), ct);
        using var http = new HttpClient();

        var resp = await http.PostAsJsonAsync(
            new Uri($"http://127.0.0.1:{server.Port}/graphql"),
            new { query = GraphQlIntrospectionProbe }, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var types = json.GetProperty("data").GetProperty("__schema").GetProperty("types").EnumerateArray().ToList();

        var berth = types.Single(t => t.GetProperty("name").GetString() == "Berth");
        var code = berth.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "code");
        Assert.True(code.GetProperty("isDeprecated").GetBoolean());
        Assert.Equal("Use name.", code.GetProperty("deprecationReason").GetString());

        var status = types.Single(t => t.GetProperty("name").GetString() == "BerthStatus");
        Assert.Equal("ENUM", status.GetProperty("kind").GetString());
        Assert.Equal(
            ["FREE", "OCCUPIED"],
            status.GetProperty("enumValues").EnumerateArray()
                .Select(v => v.GetProperty("name").GetString()).ToArray());
    }

    [Fact]
    public async Task The_Built_In_Scalars_Are_Listed_Even_Though_No_Schema_Declares_Them()
    {
        // Not cosmetic. A field of type String names a type; if the type
        // table has no entry for it, a client walking the table to build an
        // input form finds nothing there.
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartAsync(WriteSchema(HarbourSdl), ct);
        using var http = new HttpClient();

        var resp = await http.PostAsJsonAsync(
            new Uri($"http://127.0.0.1:{server.Port}/graphql"),
            new { query = GraphQlIntrospectionProbe }, ct);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var names = json.GetProperty("data").GetProperty("__schema").GetProperty("types")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("String", names);
        Assert.Contains("Int", names);
        Assert.Contains("Float", names);
        Assert.Contains("Boolean", names);
        Assert.Contains("ID", names);
    }

    [Fact]
    public async Task A_Field_Called_Schema_Deeper_In_A_Query_Is_Still_An_Ordinary_Field()
    {
        // __schema is a meta-field on the query root. Anywhere else the name
        // belongs to whoever declared it, and answering the introspection
        // document there would be wrong.
        var ct = TestContext.Current.CancellationToken;
        var path = WriteSchema("""
            type Query { wrapper: Wrapper }
            type Wrapper { __schema: String }
            """);
        await using var server = await StartAsync(path, ct);
        using var http = new HttpClient();

        var resp = await http.PostAsJsonAsync(
            new Uri($"http://127.0.0.1:{server.Port}/graphql"),
            new { query = "{ wrapper { __schema } }" }, ct);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var data = json.GetProperty("data");
        Assert.False(data.TryGetProperty("__schema", out _), "introspection must not hijack this");
        Assert.Equal("sample", data.GetProperty("wrapper").GetProperty("__schema").GetString());
    }

    [Fact]
    public async Task A_Variables_Only_Call_Selects_Real_Fields_Now()
    {
        // #710 gap 4, and the reason gap 3 had to land first. A caller with
        // no UI sends variables and lets the plugin write the operation.
        // Until now that operation selected __typename and nothing else, so
        // the answer was the name of the type. Nothing here discovers
        // first: the plugin introspects the endpoint itself.
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartAsync(WriteSchema(HarbourSdl), ct);
        using var protocol = new BowireGraphQLProtocol();

        var result = await protocol.InvokeAsync(
            $"http://127.0.0.1:{server.Port}/graphql",
            service: "Query", method: "berth",
            jsonMessages: ["""{"id":"b1"}"""],
            showInternalServices: false, ct: ct);

        Assert.Equal("OK", result.Status);
        using var doc = JsonDocument.Parse(result.Response!);
        var berth = doc.RootElement.GetProperty("data").GetProperty("berth");
        Assert.Equal("id-1", berth.GetProperty("id").GetString());
        Assert.Equal("sample", berth.GetProperty("name").GetString());
        // What the old behaviour would have returned instead.
        Assert.False(berth.TryGetProperty("__typename", out _));
    }

    [Fact]
    public async Task A_Server_Without_Introspection_Still_Gets_A_Valid_Operation()
    {
        // The fallback has to stay honest. Pointed at something that is not
        // a GraphQL endpoint at all, the plugin cannot learn the shape --
        // and must still send an operation the server can parse rather than
        // failing on our side.
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartAsync(WriteSchema(HarbourSdl), ct);
        using var protocol = new BowireGraphQLProtocol();

        var result = await protocol.InvokeAsync(
            $"http://127.0.0.1:{server.Port}/not-graphql",
            service: "Query", method: "berth",
            jsonMessages: ["""{"id":"b1"}"""],
            showInternalServices: false, ct: ct);

        // The call fails at the transport, not while building the request:
        // a status that names the HTTP problem, not a NullReference.
        Assert.NotEqual("OK", result.Status);
        Assert.NotNull(result.Status);
    }

    /// <summary>
    /// A cut-down introspection query. The mock answers the whole document
    /// whatever is selected, so the selection here only has to name
    /// <c>__schema</c> at the root.
    /// </summary>
    private const string GraphQlIntrospectionProbe = "{ __schema { types { name } } }";
}
