// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Phase 2h: a recorded GraphQL subscription (captured via the UI's
/// graphql-transport-ws client) replays against a fresh client by speaking
/// the handshake (connection_init / connection_ack), accepting whatever
/// subscription id the client picks, and rewriting every outgoing
/// next/complete frame's id to match.
/// </summary>
public sealed class GraphQlSubscriptionReplayTests : IDisposable
{
    private readonly string _tempDir;

    public GraphQlSubscriptionReplayTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-mock-gqlsub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Subscription_RewritesIdAndReplaysFramesInOrder()
    {
        // Recorded frames carry the id the *recorder's* subscribe used
        // ("server-side-id"). The replayer must rewrite this to whatever
        // the current client sends during subscribe.
        var recording = new
        {
            id = "rec_gql",
            name = "graphql sub",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_sub",
                    protocol = "graphql",
                    service = "GraphQL",
                    method = "Subscription.bookAdded",
                    methodType = "ServerStreaming",
                    status = "OK",
                    response = (string?)null,
                    httpPath = "/graphql",
                    httpVerb = "GET",
                    receivedMessages = new object[]
                    {
                        Frame(0, 0, new
                        {
                            type = "text",
                            text = new
                            {
                                type = "next",
                                id = "recorded-id",
                                payload = new { data = new { bookAdded = new { title = "Moby Dick" } } }
                            }
                        }),
                        Frame(1, 5, new
                        {
                            type = "text",
                            text = new
                            {
                                type = "next",
                                id = "recorded-id",
                                payload = new { data = new { bookAdded = new { title = "Dune" } } }
                            }
                        }),
                        Frame(2, 10, new
                        {
                            type = "text",
                            text = new { type = "complete", id = "recorded-id" }
                        })
                    }
                }
            }
        };

        var path = Path.Combine(_tempDir, "rec.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol("graphql-transport-ws");
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/graphql"), TestContext.Current.CancellationToken);

        Assert.Equal("graphql-transport-ws", client.SubProtocol);

        await SendJsonAsync(client, """{"type":"connection_init"}""", TestContext.Current.CancellationToken);
        var ack = await ReadJsonAsync(client, TestContext.Current.CancellationToken);
        Assert.Equal("connection_ack", ack.GetProperty("type").GetString());

        const string clientId = "client-chosen-7b3c";
        var subscribe = "{\"type\":\"subscribe\",\"id\":\"" + clientId +
                        "\",\"payload\":{\"query\":\"subscription { bookAdded { title } }\"}}";
        await SendJsonAsync(client, subscribe, TestContext.Current.CancellationToken);

        // First next frame
        var f1 = await ReadJsonAsync(client, TestContext.Current.CancellationToken);
        Assert.Equal("next", f1.GetProperty("type").GetString());
        Assert.Equal(clientId, f1.GetProperty("id").GetString());
        Assert.Equal("Moby Dick",
            f1.GetProperty("payload").GetProperty("data").GetProperty("bookAdded").GetProperty("title").GetString());

        // Second next frame
        var f2 = await ReadJsonAsync(client, TestContext.Current.CancellationToken);
        Assert.Equal("next", f2.GetProperty("type").GetString());
        Assert.Equal(clientId, f2.GetProperty("id").GetString());
        Assert.Equal("Dune",
            f2.GetProperty("payload").GetProperty("data").GetProperty("bookAdded").GetProperty("title").GetString());

        // complete frame
        var f3 = await ReadJsonAsync(client, TestContext.Current.CancellationToken);
        Assert.Equal("complete", f3.GetProperty("type").GetString());
        Assert.Equal(clientId, f3.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Subscription_SynthesisesCompleteWhenRecordingMissesIt()
    {
        // A recording that only captured a single `next` frame (subscription
        // was still open when the recorder stopped) should still terminate
        // cleanly for the client — the replayer appends a complete so the
        // client-side iterator ends.
        var recording = new
        {
            id = "rec_gql2",
            name = "graphql sub no-complete",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_sub2",
                    protocol = "graphql",
                    service = "GraphQL",
                    method = "Subscription.ticker",
                    methodType = "ServerStreaming",
                    status = "OK",
                    response = (string?)null,
                    httpPath = "/graphql",
                    httpVerb = "GET",
                    receivedMessages = new object[]
                    {
                        Frame(0, 0, new
                        {
                            type = "text",
                            text = new
                            {
                                type = "next",
                                id = "orig",
                                payload = new { data = new { ticker = 1 } }
                            }
                        })
                    }
                }
            }
        };

        var path = Path.Combine(_tempDir, "rec2.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol("graphql-transport-ws");
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/graphql"), TestContext.Current.CancellationToken);

        await SendJsonAsync(client, """{"type":"connection_init"}""", TestContext.Current.CancellationToken);
        _ = await ReadJsonAsync(client, TestContext.Current.CancellationToken); // ack

        const string clientId = "xyz";
        await SendJsonAsync(client,
            "{\"type\":\"subscribe\",\"id\":\"" + clientId +
            "\",\"payload\":{\"query\":\"subscription{ticker}\"}}",
            TestContext.Current.CancellationToken);

        var next = await ReadJsonAsync(client, TestContext.Current.CancellationToken);
        Assert.Equal("next", next.GetProperty("type").GetString());
        Assert.Equal(clientId, next.GetProperty("id").GetString());

        var complete = await ReadJsonAsync(client, TestContext.Current.CancellationToken);
        Assert.Equal("complete", complete.GetProperty("type").GetString());
        Assert.Equal(clientId, complete.GetProperty("id").GetString());
    }

    // ---- helpers ----

    private static object Frame(int index, long ts, object data) =>
        new { index, timestampMs = ts, data };

    private static async Task SendJsonAsync(ClientWebSocket client, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await client.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(ClientWebSocket client, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Socket closed while reading JSON");
            await ms.WriteAsync(buffer.AsMemory(0, result.Count), cts.Token);
        } while (!result.EndOfMessage);

        var text = Encoding.UTF8.GetString(ms.ToArray());
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
