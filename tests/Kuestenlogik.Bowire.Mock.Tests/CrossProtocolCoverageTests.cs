// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Phase 2g verifies that three protocol families (OData, MCP unary,
/// MCP notifications via SSE) already replay correctly through the
/// existing REST / SSE infrastructure, without protocol-specific code
/// paths. GraphQL subscriptions are excluded here and tracked under the
/// follow-on Phase-2h slice because their transport (graphql-transport-ws)
/// requires protocol-aware id rewriting.
/// </summary>
public sealed class CrossProtocolCoverageTests
{
    private static IHost BuildHost(BowireRecording recording)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                    .Configure(app =>
                    {
                        app.UseBowireMock(recording, opts =>
                        {
                            opts.Watch = false;
                            opts.PassThroughOnMiss = false;
                            opts.ReplaySpeed = 0;
                        });
                        app.Run(async ctx =>
                        {
                            ctx.Response.StatusCode = 418;
                            await ctx.Response.WriteAsync("fallthrough");
                        });
                    });
            })
            .Start();
    }

    [Fact]
    public async Task Odata_MetadataCall_ReplaysAsRestBody()
    {
        // OData is REST with a canonical $metadata path + specific response
        // shape. As far as the mock is concerned it's just a GET with JSON
        // back — no protocol-specific replayer needed.
        const string metadataResponse = """
        {"@odata.context":"$metadata","value":[{"name":"Products","kind":"EntitySet"}]}
        """;
        var rec = new BowireRecording
        {
            Id = "rec_odata",
            Name = "odata",
            RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "step_meta",
                    Protocol = "odata",
                    Service = "Catalog",
                    Method = "Metadata",
                    MethodType = "Unary",
                    HttpPath = "/odata/$metadata",
                    HttpVerb = "GET",
                    Status = "OK",
                    Response = metadataResponse
                }
            }
        };

        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/odata/$metadata", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("@odata.context", body, StringComparison.Ordinal);
        Assert.Contains("Products", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_UnaryToolCall_ReplaysAsRest()
    {
        // MCP's streamable-HTTP transport is a POST returning either
        // application/json (single response) or a framed SSE stream. For a
        // unary tools/call, the JSON-body variant is exactly a REST unary
        // call — no protocol-specific replayer needed.
        const string toolsCallResponse = """
        {"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"42"}]}}
        """;
        var rec = new BowireRecording
        {
            Id = "rec_mcp",
            Name = "mcp",
            RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "step_call",
                    Protocol = "mcp",
                    Service = "tools",
                    Method = "tools/call",
                    MethodType = "Unary",
                    HttpPath = "/mcp",
                    HttpVerb = "POST",
                    Status = "OK",
                    Response = toolsCallResponse
                }
            }
        };

        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        using var content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"add","arguments":{"a":21,"b":21}}}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var resp = await client.PostAsync(new Uri("/mcp", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(1, json.RootElement.GetProperty("id").GetInt32());
        Assert.Contains("42", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_NotificationStream_ReplaysAsSse()
    {
        // MCP server-pushed notifications arrive over the same endpoint as
        // a framed SSE stream. The mock's SSE replay emits each recorded
        // frame as `data: <payload>\n\n`, which is exactly the on-wire
        // shape an MCP client expects.
        var rec = new BowireRecording
        {
            Id = "rec_mcp_notif",
            Name = "mcp notifications",
            RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "step_notif",
                    Protocol = "mcp",
                    Service = "notifications",
                    Method = "notifications/progress",
                    MethodType = "ServerStreaming",
                    HttpPath = "/mcp/sse",
                    HttpVerb = "GET",
                    Status = "OK",
                    ReceivedMessages = new List<BowireRecordingFrame>
                    {
                        new() { Index = 0, TimestampMs = 0, Data = """{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":"t1","value":0.1}}""" },
                        new() { Index = 1, TimestampMs = 5, Data = """{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":"t1","value":0.5}}""" },
                        new() { Index = 2, TimestampMs = 10, Data = """{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":"t1","value":1.0}}""" }
                    }
                }
            }
        };

        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/mcp/sse", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // All three progress notifications delivered in order, each as a
        // single SSE event.
        Assert.Contains("\"value\":0.1", body, StringComparison.Ordinal);
        Assert.Contains("\"value\":0.5", body, StringComparison.Ordinal);
        Assert.Contains("\"value\":1", body, StringComparison.Ordinal);
        Assert.True(
            body.IndexOf("0.1", StringComparison.Ordinal) <
            body.IndexOf("1.0", StringComparison.Ordinal));
    }
}
