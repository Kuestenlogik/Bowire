// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Which kind an uploaded file is, decided from what the loaded plugins
/// declare (#994).
/// </summary>
public sealed class UploadedSchemaImportsTests
{
    private static ReadOnlyMemory<byte> Doc(string text) => Encoding.UTF8.GetBytes(text);

    private static BowireProtocolRegistry Registry(params IBowireProtocol[] protocols)
    {
        var registry = new BowireProtocolRegistry();
        foreach (var protocol in protocols) registry.Register(protocol);
        return registry;
    }

    private static ImportingProtocol Events() => new("events", marker: "asyncapi",
        new BowireUploadedSchemaKind("asyncapi", "AsyncAPI", [".json", ".yaml", ".yml"]));

    private static ImportingProtocol Rpc() => new("rpc", marker: "openrpc",
        new BowireUploadedSchemaKind("openrpc", "OpenRPC", [".json"]));

    private static ImportingProtocol Graph() => new("graph", marker: "type Query",
        new BowireUploadedSchemaKind("graphql-sdl", "GraphQL SDL", [".graphql", ".gql"]));

    [Fact]
    public void Kinds_Lists_Proto_Always_And_OpenApi_Only_With_The_Rest_Plugin()
    {
        var without = UploadedSchemaImports.Kinds(Registry(Graph()));
        Assert.Equal(["proto", "graphql-sdl"], without.Select(k => k.Kind.Kind));

        var with = UploadedSchemaImports.Kinds(Registry(Graph(), new PlainProtocol("rest")));
        Assert.Equal(["proto", "graphql-sdl", "openapi"], with.Select(k => k.Kind.Kind));
        Assert.Equal("graph", with[1].PluginId);
    }

    [Fact]
    public void Kinds_Keeps_The_First_Plugin_That_Declares_A_Kind()
    {
        var second = new ImportingProtocol("other", marker: "x",
            new BowireUploadedSchemaKind("graphql-sdl", "Another SDL", [".sdl"]));

        var kinds = UploadedSchemaImports.Kinds(Registry(Graph(), second));

        var sdl = Assert.Single(kinds, k => k.Kind.Kind == "graphql-sdl");
        Assert.Equal("graph", sdl.PluginId);
    }

    [Fact]
    public void Route_Takes_The_Only_Kind_That_Declares_The_Extension_Without_Looking_Inside()
    {
        // Not SDL at all: the reader says what is wrong with it, which is
        // more use than "not recognised".
        var kind = UploadedSchemaImports.Route(
            Registry(Graph(), new PlainProtocol("rest")), "schema.GraphQL", Doc("{ not sdl }"), out var refusal);

        Assert.Equal("graphql-sdl", kind?.Kind.Kind);
        Assert.Null(refusal);
    }

    [Fact]
    public void Route_Asks_The_Plugins_When_Several_Kinds_Share_An_Extension()
    {
        var registry = Registry(Events(), Rpc(), new PlainProtocol("rest"));

        Assert.Equal("asyncapi",
            UploadedSchemaImports.Route(registry, "a.json", Doc("""{"asyncapi":"3.0.0"}"""), out _)?.Kind.Kind);
        Assert.Equal("openrpc",
            UploadedSchemaImports.Route(registry, "a.json", Doc("""{"openrpc":"1.3.2"}"""), out _)?.Kind.Kind);
    }

    [Fact]
    public void Route_Falls_Back_To_OpenApi_For_Json_Nobody_Claims()
    {
        var kind = UploadedSchemaImports.Route(
            Registry(Events(), Rpc(), new PlainProtocol("rest")), "petstore.json",
            Doc("""{"openapi":"3.0.0"}"""), out var refusal);

        Assert.Equal("openapi", kind?.Kind.Kind);
        Assert.Null(kind?.Reader);
        Assert.Null(refusal);
    }

    [Fact]
    public void Route_Refuses_Json_Nobody_Claims_On_A_Host_Without_The_Rest_Plugin()
    {
        var kind = UploadedSchemaImports.Route(
            Registry(Events(), Rpc()), "petstore.json", Doc("""{"openapi":"3.0.0"}"""), out var refusal);

        Assert.Null(kind);
        Assert.Contains("petstore.json", refusal, StringComparison.Ordinal);
        Assert.Contains("AsyncAPI", refusal, StringComparison.Ordinal);
        Assert.Contains("OpenRPC", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAPI", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_Offers_An_Undeclared_Extension_To_Every_Plugin()
    {
        var kind = UploadedSchemaImports.Route(
            Registry(Events(), Graph()), "schema.txt", Doc("type Query { ping: String }"), out _);

        Assert.Equal("graphql-sdl", kind?.Kind.Kind);
    }

    [Fact]
    public void Route_Refuses_A_File_Nobody_Takes_And_Names_What_Can_Be_Uploaded()
    {
        var kind = UploadedSchemaImports.Route(
            Registry(Graph()), "notes.txt", Doc("just some notes"), out var refusal);

        Assert.Null(kind);
        Assert.Contains("notes.txt", refusal, StringComparison.Ordinal);
        Assert.Contains("GraphQL SDL (.graphql, .gql)", refusal, StringComparison.Ordinal);
        Assert.Contains(".proto", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_Treats_A_Sniff_That_Throws_As_A_No()
    {
        var throwing = new ImportingProtocol("bad", marker: null,
            new BowireUploadedSchemaKind("bad", "Bad", [".json"]));

        var kind = UploadedSchemaImports.Route(
            Registry(throwing, Rpc()), "a.json", Doc("""{"openrpc":"1.3.2"}"""), out _);

        Assert.Equal("openrpc", kind?.Kind.Kind);
    }

    private class PlainProtocol(string id) : IBowireProtocol
    {
        public string Id { get; } = id;
        public string Name => Id;
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", new Dictionary<string, string>()));

        public IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<string>();

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default) => Task.FromResult<IBowireChannel?>(null);
    }

    /// <summary>
    /// A plugin that imports one kind and recognises it by a marker in the
    /// text; a null marker makes the sniff throw.
    /// </summary>
    private sealed class ImportingProtocol(string id, string? marker, BowireUploadedSchemaKind kind)
        : PlainProtocol(id), IBowireUploadedSchemaReader
    {
        public IReadOnlyList<BowireUploadedSchemaKind> UploadedSchemaKinds { get; } = [kind];

        public bool LooksLikeUploadedSchema(string kind, ReadOnlyMemory<byte> document)
            => marker is null
                ? throw new InvalidOperationException("sniff failed")
                : Encoding.UTF8.GetString(document.Span).Contains(marker, StringComparison.Ordinal);

        public IReadOnlyList<BowireServiceInfo> ReadUploadedSchema(
            string kind, ReadOnlyMemory<byte> document, string sourceName) => [];
    }
}
