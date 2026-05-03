// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Phase 3d extension: GraphQL schema-only mode. Given an SDL file,
/// the mock answers every POST /graphql request by parsing the query
/// and returning a sample-valued JSON payload shaped to match the
/// selection set. Unlike OpenAPI / gRPC schema-only modes the response
/// is synthesised per request (not pre-baked into a recording), so
/// each test exercises a different selection set to cover schema walk,
/// scalars, aliases, lists, enums, and __typename.
/// </summary>
public sealed class GraphQlSchemaOnlyModeTests : IDisposable
{
    private readonly string _tempDir;

    public GraphQlSchemaOnlyModeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-graphql-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private string WriteSchema(string name, string sdl)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, sdl);
        return path;
    }

    [Fact]
    public async Task Query_ScalarFields_ReturnsSampleValues()
    {
        var path = WriteSchema("scalars.graphql", """
            type Query {
              ping: String
              count: Int
              ratio: Float
              enabled: Boolean
              id: ID
            }
            """);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { GraphQlSchemaPath = path, Port = 0, Watch = false, SchemaSources = new IBowireMockSchemaSource[] { new GraphQlMockSchemaSource() }, LiveSchemaHandlers = new IBowireMockLiveSchemaHandler[] { new GraphQlMockSchemaSource() } },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.PostAsJsonAsync(
            new Uri($"http://127.0.0.1:{server.Port}/graphql"),
            new { query = "{ ping count ratio enabled id }" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var data = json.GetProperty("data");
        Assert.Equal("sample", data.GetProperty("ping").GetString());
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        Assert.Equal(1.5, data.GetProperty("ratio").GetDouble());
        Assert.True(data.GetProperty("enabled").GetBoolean());
        Assert.Equal("id-1", data.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Query_NestedObjectWithSelection_ShapedToMatchSelection()
    {
        var path = WriteSchema("users.graphql", """
            type Query { user: User }
            type User {
              id: ID!
              name: String!
              email: String!
              phone: String
            }
            """);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { GraphQlSchemaPath = path, Port = 0, Watch = false, SchemaSources = new IBowireMockSchemaSource[] { new GraphQlMockSchemaSource() }, LiveSchemaHandlers = new IBowireMockLiveSchemaHandler[] { new GraphQlMockSchemaSource() } },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.PostAsJsonAsync(
            new Uri($"http://127.0.0.1:{server.Port}/graphql"),
            new { query = "{ user { id name } }" }, TestContext.Current.CancellationToken);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var user = json.GetProperty("data").GetProperty("user");
        Assert.Equal("id-1", user.GetProperty("id").GetString());
        Assert.Equal("sample", user.GetProperty("name").GetString());
        // email / phone were NOT selected, so the response must not carry them.
        Assert.False(user.TryGetProperty("email", out _));
        Assert.False(user.TryGetProperty("phone", out _));
    }

    [Fact]
    public async Task Query_ListField_EmitsThreeItems()
    {
        var path = WriteSchema("posts.graphql", """
            type Query { posts: [Post!]! }
            type Post {
              id: ID!
              title: String!
            }
            """);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { GraphQlSchemaPath = path, Port = 0, Watch = false, SchemaSources = new IBowireMockSchemaSource[] { new GraphQlMockSchemaSource() }, LiveSchemaHandlers = new IBowireMockLiveSchemaHandler[] { new GraphQlMockSchemaSource() } },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.PostAsJsonAsync(
            new Uri($"http://127.0.0.1:{server.Port}/graphql"),
            new { query = "{ posts { id title } }" }, TestContext.Current.CancellationToken);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var posts = json.GetProperty("data").GetProperty("posts");
        Assert.Equal(JsonValueKind.Array, posts.ValueKind);
        Assert.Equal(3, posts.GetArrayLength());
        Assert.Equal("id-1", posts[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Query_Alias_RenamesResponseKey()
    {
        var path = WriteSchema("alias.graphql", """
            type Query { hello: String }
            """);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { GraphQlSchemaPath = path, Port = 0, Watch = false, SchemaSources = new IBowireMockSchemaSource[] { new GraphQlMockSchemaSource() }, LiveSchemaHandlers = new IBowireMockLiveSchemaHandler[] { new GraphQlMockSchemaSource() } },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.PostAsJsonAsync(
            new Uri($"http://127.0.0.1:{server.Port}/graphql"),
            new { query = "{ greeting: hello }" }, TestContext.Current.CancellationToken);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var data = json.GetProperty("data");
        Assert.True(data.TryGetProperty("greeting", out var greeting));
        Assert.Equal("sample", greeting.GetString());
        Assert.False(data.TryGetProperty("hello", out _));
    }

    [Fact]
    public async Task Query_Typename_ReturnsParentTypeName()
    {
        var path = WriteSchema("typename.graphql", """
            type Query { user: User }
            type User { id: ID! }
            """);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { GraphQlSchemaPath = path, Port = 0, Watch = false, SchemaSources = new IBowireMockSchemaSource[] { new GraphQlMockSchemaSource() }, LiveSchemaHandlers = new IBowireMockLiveSchemaHandler[] { new GraphQlMockSchemaSource() } },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.PostAsJsonAsync(
            new Uri($"http://127.0.0.1:{server.Port}/graphql"),
            new { query = "{ user { __typename id } }" }, TestContext.Current.CancellationToken);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var user = json.GetProperty("data").GetProperty("user");
        Assert.Equal("User", user.GetProperty("__typename").GetString());
        Assert.Equal("id-1", user.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Query_EnumField_ReturnsFirstEnumValue()
    {
        var path = WriteSchema("enum.graphql", """
            type Query { status: Status }
            enum Status { ACTIVE INACTIVE PENDING }
            """);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { GraphQlSchemaPath = path, Port = 0, Watch = false, SchemaSources = new IBowireMockSchemaSource[] { new GraphQlMockSchemaSource() }, LiveSchemaHandlers = new IBowireMockLiveSchemaHandler[] { new GraphQlMockSchemaSource() } },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.PostAsJsonAsync(
            new Uri($"http://127.0.0.1:{server.Port}/graphql"),
            new { query = "{ status }" }, TestContext.Current.CancellationToken);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("ACTIVE", json.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Query_CustomSchemaBlock_RenamesQueryRoot()
    {
        // Schema defines a non-default root type name; the handler must
        // honour the rename or it will return "Schema has no Query root".
        var path = WriteSchema("custom-root.graphql", """
            schema { query: Root }
            type Root { name: String }
            """);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { GraphQlSchemaPath = path, Port = 0, Watch = false, SchemaSources = new IBowireMockSchemaSource[] { new GraphQlMockSchemaSource() }, LiveSchemaHandlers = new IBowireMockLiveSchemaHandler[] { new GraphQlMockSchemaSource() } },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.PostAsJsonAsync(
            new Uri($"http://127.0.0.1:{server.Port}/graphql"),
            new { query = "{ name }" }, TestContext.Current.CancellationToken);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("sample", json.GetProperty("data").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Mutation_ReturnsExplicitNotSupportedError()
    {
        var path = WriteSchema("mutation.graphql", """
            type Query { hello: String }
            type Mutation { doThing: String }
            """);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { GraphQlSchemaPath = path, Port = 0, Watch = false, SchemaSources = new IBowireMockSchemaSource[] { new GraphQlMockSchemaSource() }, LiveSchemaHandlers = new IBowireMockLiveSchemaHandler[] { new GraphQlMockSchemaSource() } },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.PostAsJsonAsync(
            new Uri($"http://127.0.0.1:{server.Port}/graphql"),
            new { query = "mutation { doThing }" }, TestContext.Current.CancellationToken);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var errors = json.GetProperty("errors");
        Assert.Equal(JsonValueKind.Array, errors.ValueKind);
        Assert.Contains("Query operations only", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task UnmatchedPath_FallsThroughTo404()
    {
        // GraphQL handler only owns POST /graphql — every other route
        // falls through to the mock's 404 terminal (no recording to match).
        var path = WriteSchema("fallthrough.graphql", """
            type Query { hello: String }
            """);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { GraphQlSchemaPath = path, Port = 0, Watch = false, SchemaSources = new IBowireMockSchemaSource[] { new GraphQlMockSchemaSource() }, LiveSchemaHandlers = new IBowireMockLiveSchemaHandler[] { new GraphQlMockSchemaSource() } },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.GetAsync(
            new Uri($"http://127.0.0.1:{server.Port}/graphql"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var resp2 = await http.GetAsync(
            new Uri($"http://127.0.0.1:{server.Port}/other"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp2.StatusCode);
    }

    [Fact]
    public async Task StartAsync_RejectsRecordingAndGraphQlSchema()
    {
        var path = WriteSchema("guard.graphql", "type Query { hello: String }");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MockServer.StartAsync(
                new MockServerOptions
                {
                    RecordingPath = "some.json",
                    GraphQlSchemaPath = path,
                    Port = 0,
                    Watch = false
                },
                TestContext.Current.CancellationToken));
        Assert.Contains("exactly one", ex.Message, StringComparison.Ordinal);
    }
}
