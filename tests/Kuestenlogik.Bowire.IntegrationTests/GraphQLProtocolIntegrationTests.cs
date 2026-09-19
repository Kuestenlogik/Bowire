// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.GraphQL;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end tests for <see cref="BowireGraphQLProtocol"/> against a
/// minimal hand-rolled GraphQL server: introspection round-trip, generated
/// query invocation, verbatim query passthrough.
/// </summary>
public sealed class GraphQLProtocolIntegrationTests
{
    private static readonly JsonSerializerOptions s_caseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task Discover_Builds_Query_And_Mutation_Services_From_Introspection()
    {
        await using var host = await PluginTestHost.StartAsync(MapGraphQLEndpoint);
        using var protocol = new BowireGraphQLProtocol();

        var services = await protocol.DiscoverAsync(host.BaseUrl + "/graphql", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Equal(["Query", "Mutation"], services.Select(s => s.Name).ToArray());

        var query = services.Single(s => s.Name == "Query");
        Assert.Contains(query.Methods, m => m.Name == "ping");

        var mutation = services.Single(s => s.Name == "Mutation");
        Assert.Contains(mutation.Methods, m => m.Name == "echo");
        var echo = mutation.Methods.Single(m => m.Name == "echo");
        Assert.Single(echo.InputType.Fields);
        Assert.True(echo.InputType.Fields[0].Required);
        Assert.Equal("string", echo.InputType.Fields[0].Type);
    }

    [Fact]
    public async Task Invoke_VariablesOnly_BuildsAndSendsGeneratedQuery()
    {
        await using var host = await PluginTestHost.StartAsync(MapGraphQLEndpoint);
        using var protocol = new BowireGraphQLProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/graphql",
            service: "Query",
            method: "ping",
            jsonMessages: ["{}"],
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("\"pong\"", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_VerbatimQueryShape_PassesQueryThroughUnchanged()
    {
        await using var host = await PluginTestHost.StartAsync(MapGraphQLEndpoint);
        using var protocol = new BowireGraphQLProtocol();

        // The "method" name is `ping`, but the body specifies a verbatim
        // `echo` mutation — the plugin must forward that operation literally
        // instead of regenerating one based on the method name.
        const string verbatim = """
            { "query": "mutation echo($text: String!) { echo(text: $text) }",
              "variables": { "text": "verbatim wins" } }
            """;

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/graphql",
            service: "Query",
            method: "ping",
            jsonMessages: [verbatim],
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("verbatim wins", result.Response, StringComparison.Ordinal);
    }

    /// <summary>
    /// Minimal GraphQL server. Returns a tiny introspection result with
    /// Query.ping and Mutation.echo, and routes incoming operations by
    /// substring match — enough for the tests above. The introspection
    /// response is hand-rolled JSON because anonymous types with leading
    /// underscores (`__schema`) round-trip unpredictably through
    /// JsonNamingPolicy.CamelCase.
    /// </summary>
    // ---- #710: operationName on the wire ----

    [Fact]
    public async Task A_Single_Named_Operation_Names_Itself_On_The_Wire()
    {
        // Nobody asked for it. The plugin parses the document, finds one
        // operation, and sends its name -- which costs nothing and is what
        // every other GraphQL client does.
        await using var host = await PluginTestHost.StartAsync(MapGraphQLEndpoint);
        using var protocol = new BowireGraphQLProtocol();

        var result = await InvokeVerbatimAsync(protocol, host,
            """{"query":"query Only { whichOperation }"}""");

        Assert.Equal("OK", result.Status);
        Assert.Contains("\"whichOperation\": \"Only\"", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Several_Operations_Send_No_Name_Unless_One_Was_Chosen()
    {
        // The honest half of #710. With two operations and no choice the
        // plugin must not guess: guessing would run an operation nobody
        // picked and report it as a success.
        await using var host = await PluginTestHost.StartAsync(MapGraphQLEndpoint);
        using var protocol = new BowireGraphQLProtocol();

        var result = await InvokeVerbatimAsync(protocol, host,
            """{"query":"query A { whichOperation }\nquery B { whichOperation }"}""");

        Assert.Equal("OK", result.Status);
        Assert.Contains("\"whichOperation\": null", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Chosen_Operation_Is_The_One_The_Server_Is_Told_To_Run()
    {
        // What the request builder's picker produces. Without this a
        // multi-operation document is simply not runnable.
        await using var host = await PluginTestHost.StartAsync(MapGraphQLEndpoint);
        using var protocol = new BowireGraphQLProtocol();

        var result = await InvokeVerbatimAsync(protocol, host,
            """{"query":"query A { whichOperation }\nquery B { whichOperation }","operationName":"B"}""");

        Assert.Equal("OK", result.Status);
        Assert.Contains("\"whichOperation\": \"B\"", result.Response, StringComparison.Ordinal);
    }

    private static Task<InvokeResult> InvokeVerbatimAsync(
        BowireGraphQLProtocol protocol, PluginTestHost host, string payload)
        => protocol.InvokeAsync(
            host.BaseUrl + "/graphql",
            service: "Query",
            method: "whichOperation",
            jsonMessages: [payload],
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

    private static void MapGraphQLEndpoint(WebApplication app)
    {
        app.MapPost("/graphql", async (HttpContext ctx) =>
        {
            var request = await JsonSerializer.DeserializeAsync<GraphQLRequest>(ctx.Request.Body, s_caseInsensitive);
            if (request?.Query is null)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            string responseJson;
            if (request.Query.Contains("__schema", StringComparison.Ordinal))
            {
                responseJson = IntrospectionResponse;
            }
            else if (request.Query.Contains("ping", StringComparison.Ordinal))
            {
                responseJson = """{ "data": { "ping": "pong" } }""";
            }
            else if (request.Query.Contains("echo", StringComparison.Ordinal))
            {
                var text = request.Variables.TryGetValue("text", out var t) ? t.GetString() ?? "" : "";
                responseJson = $$"""{ "data": { "echo": "{{text}}" } }""";
            }
            else if (request.Query.Contains("whichOperation", StringComparison.Ordinal))
            {
                // #710 — the server answers with what it was told to run.
                // A GraphQL server with several operations in one document
                // cannot proceed without this, so echoing it is the only
                // way to prove it arrived.
                responseJson = request.OperationName is null
                    ? """{ "data": { "whichOperation": null } }"""
                    : $$"""{ "data": { "whichOperation": "{{request.OperationName}}" } }""";
            }
            else
            {
                responseJson = """{ "errors": [{ "message": "unknown operation" }] }""";
            }

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(responseJson);
        });
    }

    private sealed class GraphQLRequest
    {
        public string? Query { get; set; }
        public Dictionary<string, JsonElement> Variables { get; set; } = new();

        /// <summary>#710 — what the server is now told, and was not before.</summary>
        public string? OperationName { get; set; }
    }

    private const string IntrospectionResponse = """
    {
      "data": {
        "__schema": {
          "queryType": { "name": "Query" },
          "mutationType": { "name": "Mutation" },
          "subscriptionType": null,
          "types": [
            {
              "kind": "OBJECT",
              "name": "Query",
              "fields": [
                {
                  "name": "ping",
                  "args": [],
                  "type": { "kind": "SCALAR", "name": "String", "ofType": null }
                }
              ],
              "inputFields": null,
              "enumValues": null
            },
            {
              "kind": "OBJECT",
              "name": "Mutation",
              "fields": [
                {
                  "name": "echo",
                  "args": [
                    {
                      "name": "text",
                      "type": {
                        "kind": "NON_NULL",
                        "name": null,
                        "ofType": { "kind": "SCALAR", "name": "String", "ofType": null }
                      }
                    }
                  ],
                  "type": { "kind": "SCALAR", "name": "String", "ofType": null }
                }
              ],
              "inputFields": null,
              "enumValues": null
            }
          ]
        }
      }
    }
    """;
}
