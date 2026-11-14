// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.GraphQL;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// A GraphQL schema uploaded as SDL, against a server that refuses
/// introspection (#994).
/// </summary>
/// <remarks>
/// These store the SDL the way the upload endpoint does and then discover
/// and call with nothing else said, which is what the workbench does after a
/// file was dropped on it.
/// </remarks>
public sealed class GraphQLUploadedSdlIntegrationTests : IDisposable
{
    private const string HarborSdl = """
        type Ship { id: ID! name: String! flag: String }
        type Query { ship(id: ID!): Ship }
        """;

    private readonly TempUserRoot _storage = new("graphql-uploaded-sdl");

    public void Dispose()
    {
        _storage.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void UploadHarborSdl()
        => SchemaUploadStore.AddBytes("graphql-sdl", Encoding.UTF8.GetBytes(HarborSdl), "harbor.graphql", ".graphql");

    [Fact]
    public async Task Discovery_Lists_The_Uploaded_Operations_For_A_Server_That_Refuses_Introspection()
    {
        UploadHarborSdl();
        await using var host = await PluginTestHost.StartAsync(IntrospectionRefused);

        using var graphql = new BowireGraphQLProtocol();
        var registry = new BowireProtocolRegistry();
        registry.Register(graphql);
        var url = host.BaseUrl + "/graphql";

        var probe = await BowireDiscoveryProbe.RunAsync(
            registry, url, pluginHint: null, showInternalServices: false,
            TimeSpan.FromSeconds(30), ct: TestContext.Current.CancellationToken);

        var query = Assert.Single(probe.Services);
        Assert.Equal("Query", query.Name);
        Assert.Equal("graphql", query.Source);
        Assert.True(query.IsUploaded);
        // The SDL names no server: the URL being discovered is the origin.
        Assert.Equal(url, query.OriginUrl);
        Assert.Equal(["ship"], query.Methods.Select(m => m.Name));
    }

    [Fact]
    public async Task A_Call_Selects_The_Fields_The_Uploaded_Schema_Declares()
    {
        UploadHarborSdl();
        await using var host = await PluginTestHost.StartAsync(IntrospectionRefused);

        using var graphql = new BowireGraphQLProtocol();
        var result = await graphql.InvokeAsync(
            host.BaseUrl + "/graphql", "Query", "ship",
            ["""{"id":"101"}"""],
            showInternalServices: false, metadata: null, TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        using var response = JsonDocument.Parse(result.Response!);
        var sent = response.RootElement.GetProperty("data").GetProperty("echoed").GetString()!;
        // Typed from the SDL, where the stub would have said String.
        Assert.Contains("$id: ID!", sent, StringComparison.Ordinal);
        // Real fields, where the stub would have selected __typename.
        Assert.Contains("name", sent, StringComparison.Ordinal);
        Assert.Contains("flag", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("__typename", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_An_Upload_The_Same_Call_Falls_Back_To_The_Stub()
    {
        await using var host = await PluginTestHost.StartAsync(IntrospectionRefused);

        using var graphql = new BowireGraphQLProtocol();
        var result = await graphql.InvokeAsync(
            host.BaseUrl + "/graphql", "Query", "ship",
            ["""{"id":"101"}"""],
            showInternalServices: false, metadata: null, TestContext.Current.CancellationToken);

        using var response = JsonDocument.Parse(result.Response!);
        var sent = response.RootElement.GetProperty("data").GetProperty("echoed").GetString()!;
        Assert.Contains("__typename", sent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Answers every operation by echoing it, except introspection, which it
    /// refuses the way a production server does.
    /// </summary>
    private static void IntrospectionRefused(WebApplication app)
    {
        app.MapPost("/graphql", async (HttpContext ctx) =>
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.Body))
            {
                body = await reader.ReadToEndAsync(ctx.RequestAborted);
            }

            ctx.Response.ContentType = "application/json";
            if (body.Contains("__schema", StringComparison.Ordinal))
            {
                await ctx.Response.WriteAsync("""{ "errors": [{ "message": "introspection disabled" }] }""");
                return;
            }

            await ctx.Response.WriteAsync(
                $$"""{ "data": { "echoed": {{JsonSerializer.Serialize(body)}} } }""");
        });
    }
}
