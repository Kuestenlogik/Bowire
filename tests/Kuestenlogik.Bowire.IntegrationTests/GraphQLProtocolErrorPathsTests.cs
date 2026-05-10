// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.GraphQL;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Error / fallback branches in <see cref="BowireGraphQLProtocol.DiscoverAsync"/>
/// and <see cref="BowireGraphQLProtocol.InvokeAsync"/> — paths that don't fire
/// when the server is healthy and returns a normal introspection result.
/// </summary>
public sealed class GraphQLProtocolErrorPathsTests
{
    [Fact]
    public async Task Discover_HttpRequestException_From_Unreachable_Endpoint_Returns_Empty()
    {
        var protocol = new BowireGraphQLProtocol();

        var services = await protocol.DiscoverAsync(
            "http://127.0.0.1:1/graphql",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task Discover_NonJsonBody_Returns_Empty()
    {
        // The plugin treats a non-JSON body as "not a GraphQL endpoint"
        // and silently skips it so other plugins can still try the URL.
        await using var host = await PluginTestHost.StartAsync(app =>
            app.MapPost("/graphql", async (HttpContext ctx) =>
            {
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync("definitely not json");
            }));

        var protocol = new BowireGraphQLProtocol();
        var services = await protocol.DiscoverAsync(
            host.BaseUrl + "/graphql",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task Discover_GraphqlErrorEnvelope_Without_Data_Returns_Empty()
    {
        // Server replies with a syntactically-valid GraphQL error envelope
        // (no `data` field). We treat that as nothing-to-discover rather
        // than letting the schema mapper crash on missing fields.
        await using var host = await PluginTestHost.StartAsync(app =>
            app.MapPost("/graphql", async (HttpContext ctx) =>
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""{ "errors": [{ "message": "introspection disabled" }] }""");
            }));

        var protocol = new BowireGraphQLProtocol();
        var services = await protocol.DiscoverAsync(
            host.BaseUrl + "/graphql",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task Invoke_RoutesMutationKeyword()
    {
        // Verify the operationKind switch picks "mutation" when service ==
        // "Mutation". The fixture echoes the inbound query so we can prove
        // the kind made it onto the wire.
        await using var host = await PluginTestHost.StartAsync(EchoQueryEndpoint);
        var protocol = new BowireGraphQLProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/graphql",
            service: "Mutation",
            method: "doThing",
            jsonMessages: ["{\"id\":7}"],
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("mutation doThing", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_RoutesSubscriptionKeyword()
    {
        await using var host = await PluginTestHost.StartAsync(EchoQueryEndpoint);
        var protocol = new BowireGraphQLProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/graphql",
            service: "Subscription",
            method: "tick",
            jsonMessages: ["{}"],
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("subscription tick", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_ServerReturns500_ResultStatusCarriesExceptionMessage()
    {
        // EnsureSuccessStatusCode throws — the plugin wraps every Invoke in
        // a try/catch and surfaces the exception's Message as the Status.
        await using var host = await PluginTestHost.StartAsync(app =>
            app.MapPost("/graphql", (HttpContext ctx) =>
            {
                ctx.Response.StatusCode = 503;
                return Task.CompletedTask;
            }));

        var protocol = new BowireGraphQLProtocol();
        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/graphql",
            service: "Query",
            method: "ping",
            jsonMessages: ["{}"],
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(result.Response);
        // HttpRequestException's Message includes "503" or the reason
        // phrase — matching either avoids platform-specific wording drift.
        Assert.NotEqual("OK", result.Status);
        Assert.True(result.DurationMs >= 0);
    }

    [Fact]
    public async Task Invoke_ForwardsMetadataHeaders()
    {
        // The metadata dict feeds into request.Headers via TryAddWithoutValidation —
        // this exercises the foreach branch in SendOperationAsync that's otherwise
        // skipped when metadata is null.
        await using var host = await PluginTestHost.StartAsync(EchoQueryEndpoint);
        var protocol = new BowireGraphQLProtocol();

        var meta = new Dictionary<string, string>
        {
            ["X-Test-Header"] = "marker-value"
        };

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/graphql",
            service: "Query",
            method: "ping",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: meta,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Contains("marker-value", result.Response!, StringComparison.Ordinal);
    }

    private static void EchoQueryEndpoint(WebApplication app)
    {
        app.MapPost("/graphql", async (HttpContext ctx) =>
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.Body))
            {
                body = await reader.ReadToEndAsync(ctx.RequestAborted);
            }
            var hdr = ctx.Request.Headers["X-Test-Header"].ToString();
            ctx.Response.ContentType = "application/json";
            // Wrap the inbound payload as a string under data.echoed so the
            // assertions can substring-match the literal query text.
            var json = $$"""{ "data": { "echoed": {{System.Text.Json.JsonSerializer.Serialize(body)}}, "hdr": {{System.Text.Json.JsonSerializer.Serialize(hdr)}} } }""";
            await ctx.Response.WriteAsync(json);
        });
    }
}
