// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.GraphQL;
using Kuestenlogik.Bowire.Protocol.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end coverage of <see cref="BowireGraphQLProtocol.InvokeStreamAsync"/>
/// across both subscription transports: graphql-sse (single-connection POST
/// with <c>text/event-stream</c>) and graphql-transport-ws (WebSocket sub-
/// protocol with connection_init / connection_ack / subscribe / next /
/// complete handshake). Also covers the failure / fallback envelopes that
/// fire when a transport can't be reached.
/// </summary>
public sealed class GraphQLProtocolStreamingTests
{
    [Fact]
    public async Task InvokeStream_ForcedSse_YieldsNextEventsAndStopsOnComplete()
    {
        await using var host = await PluginTestHost.StartAsync(MapSseSubscription);
        var protocol = new BowireGraphQLProtocol();

        var metadata = new Dictionary<string, string>
        {
            [BowireGraphQLProtocol.SubscriptionTransportMetadataKey] = "sse"
        };

        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            host.BaseUrl + "/graphql",
            service: "Subscription",
            method: "tick",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: metadata,
            ct: TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        Assert.Equal(2, events.Count);
        Assert.Contains("first", events[0], StringComparison.Ordinal);
        Assert.Contains("second", events[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_ForcedSse_TolaratesCommentsAndSkipsNonNextEvents()
    {
        await using var host = await PluginTestHost.StartAsync(MapSseSubscriptionWithKeepalive);
        var protocol = new BowireGraphQLProtocol();

        var metadata = new Dictionary<string, string>
        {
            [BowireGraphQLProtocol.SubscriptionTransportMetadataKey] = "sse",
            ["Authorization"] = "Bearer test"
        };

        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            host.BaseUrl + "/graphql",
            service: "Subscription",
            method: "tick",
            jsonMessages: ["{ \"query\": \"subscription { tick }\" }"],
            showInternalServices: false,
            metadata: metadata,
            ct: TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var single = Assert.Single(events);
        Assert.Contains("payload-A", single, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_ForcedSse_NonSuccessStatus_EmitsErrorEnvelope()
    {
        await using var host = await PluginTestHost.StartAsync(app =>
            app.MapPost("/graphql", (HttpContext ctx) =>
            {
                ctx.Response.StatusCode = 500;
                return Task.CompletedTask;
            }));
        var protocol = new BowireGraphQLProtocol();

        var metadata = new Dictionary<string, string>
        {
            [BowireGraphQLProtocol.SubscriptionTransportMetadataKey] = "SSE"
        };

        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            host.BaseUrl + "/graphql",
            service: "Subscription",
            method: "tick",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: metadata,
            ct: TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var only = Assert.Single(events);
        Assert.Contains("graphql-sse", only, StringComparison.Ordinal);
        Assert.Contains("error", only, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_GraphQlTransportWs_HandshakeAndYieldFrames()
    {
        // Real Kestrel host so ClientWebSocket can connect through a real
        // socket (TestServer's pipe doesn't support upgrades).
        await using var host = await PluginTestHost.StartAsync(MapTransportWsSubscription);
        // Touch the WebSocket plugin so the inline-channel registry probe
        // finds it via the loaded-assembly scan in BowireProtocolRegistry.
        _ = new BowireWebSocketProtocol();

        var protocol = new BowireGraphQLProtocol();
        var metadata = new Dictionary<string, string>
        {
            [BowireGraphQLProtocol.SubscriptionTransportMetadataKey] = "ws"
        };

        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            host.BaseUrl + "/graphql",
            service: "Subscription",
            method: "tick",
            jsonMessages: ["{ \"query\": \"subscription { tick }\", \"variables\": { \"x\": 1 } }"],
            showInternalServices: false,
            metadata: metadata,
            ct: TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        Assert.Equal(2, events.Count);
        Assert.Contains("alpha", events[0], StringComparison.Ordinal);
        Assert.Contains("beta", events[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_GraphQlTransportWs_ServerSendsErrorFrame_YieldsErrorsEnvelope()
    {
        await using var host = await PluginTestHost.StartAsync(MapTransportWsErrorSubscription);
        _ = new BowireWebSocketProtocol();

        var protocol = new BowireGraphQLProtocol();
        var metadata = new Dictionary<string, string>
        {
            [BowireGraphQLProtocol.SubscriptionTransportMetadataKey] = "ws"
        };

        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            host.BaseUrl + "/graphql",
            service: "Subscription",
            method: "tick",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: metadata,
            ct: TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var only = Assert.Single(events);
        Assert.Contains("errors", only, StringComparison.Ordinal);
        Assert.Contains("boom", only, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_GraphQlTransportWs_ErrorFrameWithoutPayload_EmitsGenericError()
    {
        await using var host = await PluginTestHost.StartAsync(app =>
            app.Map("/graphql", async (HttpContext ctx) =>
            {
                if (!ctx.WebSockets.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    return;
                }
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync("graphql-transport-ws");
                _ = await ReceiveJsonAsync(ws, ctx.RequestAborted); // connection_init
                await SendJsonAsync(ws, "{\"type\":\"connection_ack\"}", ctx.RequestAborted);
                _ = await ReceiveJsonAsync(ws, ctx.RequestAborted); // subscribe
                // No payload on this error frame — the plugin's switch
                // arm hits the else branch and emits a generic error.
                await SendJsonAsync(ws, "{\"type\":\"error\",\"id\":\"1\"}", ctx.RequestAborted);
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ctx.RequestAborted); }
                catch { /* client may already be gone */ }
            }));
        _ = new BowireWebSocketProtocol();

        var protocol = new BowireGraphQLProtocol();
        var metadata = new Dictionary<string, string>
        {
            [BowireGraphQLProtocol.SubscriptionTransportMetadataKey] = "ws"
        };

        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            host.BaseUrl + "/graphql",
            service: "Subscription",
            method: "tick",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: metadata,
            ct: TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var only = Assert.Single(events);
        Assert.Contains("graphql-ws error", only, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_GraphQlTransportWs_ConnectFailure_EmitsErrorEnvelope()
    {
        // Point at an unreachable port — the inline channel will throw and
        // the plugin should yield a single { error: ... } envelope.
        _ = new BowireWebSocketProtocol();

        var protocol = new BowireGraphQLProtocol();
        var metadata = new Dictionary<string, string>
        {
            [BowireGraphQLProtocol.SubscriptionTransportMetadataKey] = "ws"
        };

        var events = new List<string>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        await foreach (var evt in protocol.InvokeStreamAsync(
            "http://127.0.0.1:1/graphql",
            service: "Subscription",
            method: "tick",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: metadata,
            ct: cts.Token))
        {
            events.Add(evt);
        }

        var only = Assert.Single(events);
        Assert.Contains("graphql-ws connect failed", only, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_ForcedSse_CancellationDuringRead_TerminatesQuietly()
    {
        // The SSE loop uses ReadLineAsync(ct) which throws
        // OperationCanceledException — the plugin catches that and
        // breaks cleanly instead of bubbling the cancellation up to
        // the caller.
        await using var host = await PluginTestHost.StartAsync(MapSseSubscriptionLongLived);
        var protocol = new BowireGraphQLProtocol();

        var metadata = new Dictionary<string, string>
        {
            [BowireGraphQLProtocol.SubscriptionTransportMetadataKey] = "sse"
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            host.BaseUrl + "/graphql",
            service: "Subscription",
            method: "tick",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: metadata,
            ct: cts.Token))
        {
            events.Add(evt);
            // Trigger cancellation after the first frame so the next
            // ReadLineAsync hits the OperationCanceledException catch.
            await cts.CancelAsync();
        }

        Assert.Single(events);
    }

    // ---- SSE server fixtures ----

    private static void MapSseSubscription(WebApplication app)
    {
        app.MapPost("/graphql", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            // Two events with explicit event:next, then explicit
            // event:complete to cleanly terminate the stream.
            var f1 = "event: next\ndata: {\"data\":{\"tick\":\"first\"}}\n\n";
            var f2 = "event: next\ndata: {\"data\":{\"tick\":\"second\"}}\n\n";
            var done = "event: complete\ndata: {}\n\n";
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(f1), ctx.RequestAborted);
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(f2), ctx.RequestAborted);
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(done), ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        });
    }

    private static void MapSseSubscriptionLongLived(WebApplication app)
    {
        app.MapPost("/graphql", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("event: next\ndata: {\"data\":{\"tick\":\"once\"}}\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            // Hold the connection open until the client cancels — no
            // further frames are written, so the plugin's reader sits in
            // ReadLineAsync until the linked CT fires.
            try { await Task.Delay(TimeSpan.FromSeconds(30), ctx.RequestAborted); }
            catch (OperationCanceledException) { /* expected */ }
        });
    }

    private static void MapSseSubscriptionWithKeepalive(WebApplication app)
    {
        app.MapPost("/graphql", async (HttpContext ctx) =>
        {
            // Verify the plugin forwards extra headers untouched.
            var auth = ctx.Request.Headers["Authorization"].ToString();
            ctx.Response.ContentType = "text/event-stream";

            // : is an SSE comment; the plugin should ignore it.
            await ctx.Response.WriteAsync(": keepalive\n", ctx.RequestAborted);
            // Default event (no `event:`) — the plugin treats null event type as next.
            await ctx.Response.WriteAsync($"data: {{\"data\":\"payload-A\",\"hdr\":\"{auth}\"}}\n\n", ctx.RequestAborted);
            // An ignorable non-next event mid-stream — proves the switch
            // skips it without yielding.
            await ctx.Response.WriteAsync("event: ka\ndata: {}\n\n", ctx.RequestAborted);
            // End cleanly so the loop terminates without cancellation.
            await ctx.Response.WriteAsync("event: complete\ndata: {}\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        });
    }

    // ---- WebSocket (graphql-transport-ws) server fixtures ----

    private static void MapTransportWsSubscription(WebApplication app)
    {
        app.Map("/graphql", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync("graphql-transport-ws");
            await DriveTransportWsAsync(ws, sendError: false, ctx.RequestAborted);
        });
    }

    private static void MapTransportWsErrorSubscription(WebApplication app)
    {
        app.Map("/graphql", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync("graphql-transport-ws");
            await DriveTransportWsAsync(ws, sendError: true, ctx.RequestAborted);
        });
    }

    private static async Task DriveTransportWsAsync(System.Net.WebSockets.WebSocket ws, bool sendError, CancellationToken ct)
    {
        // 1) Read connection_init (we don't validate the payload — the
        // plugin always sends a literal {"type":"connection_init"}).
        var init = await ReceiveJsonAsync(ws, ct);
        Assert.Equal("connection_init", init.GetProperty("type").GetString());

        // 2) Reply with a connection_ack.
        await SendJsonAsync(ws, "{\"type\":\"connection_ack\"}", ct);

        // 3) Plugin pings us right after ack — server pings client via
        // ping frame to exercise the pong reply branch.
        await SendJsonAsync(ws, "{\"type\":\"ping\"}", ct);

        // 4) The plugin should now send `subscribe` with id "1" carrying
        // either { query } or { query, variables }.
        var subscribe = await ReceiveJsonAsync(ws, ct);
        Assert.Equal("subscribe", subscribe.GetProperty("type").GetString());
        Assert.Equal("1", subscribe.GetProperty("id").GetString());

        // 5) Read the pong response to our earlier ping (proves the pong
        // branch executed).
        var pong = await ReceiveJsonAsync(ws, ct);
        Assert.Equal("pong", pong.GetProperty("type").GetString());

        if (sendError)
        {
            await SendJsonAsync(ws, "{\"type\":\"error\",\"id\":\"1\",\"payload\":[{\"message\":\"boom\"}]}", ct);
        }
        else
        {
            await SendJsonAsync(ws, "{\"type\":\"next\",\"id\":\"1\",\"payload\":{\"data\":{\"tick\":\"alpha\"}}}", ct);
            // Send a server-initiated pong (which the plugin ignores) to
            // exercise the pong-case in the switch.
            await SendJsonAsync(ws, "{\"type\":\"pong\"}", ct);
            await SendJsonAsync(ws, "{\"type\":\"next\",\"id\":\"1\",\"payload\":{\"data\":{\"tick\":\"beta\"}}}", ct);
            await SendJsonAsync(ws, "{\"type\":\"complete\",\"id\":\"1\"}", ct);
        }

        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct); }
        catch { /* client may have already disposed */ }
    }

    private static async Task SendJsonAsync(System.Net.WebSockets.WebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<JsonElement> ReceiveJsonAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct);
        } while (!result.EndOfMessage);
        var text = Encoding.UTF8.GetString(ms.ToArray());
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
