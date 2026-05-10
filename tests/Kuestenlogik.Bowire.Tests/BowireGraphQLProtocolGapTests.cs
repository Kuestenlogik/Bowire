// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.GraphQL;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Closes the last few uncovered branches in
/// <see cref="BowireGraphQLProtocol.InvokeStreamAsync"/> — the "WebSocket
/// plugin not loaded" error envelope and the graphql-transport-ws
/// frame-shape fallbacks. Drives the dispatch via the test-only
/// <see cref="BowireGraphQLProtocol.RegistryFactory"/> seam so we don't
/// need a real assembly-discovery wobble.
///
/// <para>
/// Serialised because the seam is a process-global static. Other
/// integration tests that touch InvokeStreamAsync don't go through this
/// fixture, so the disabled scope is local to these two tests.
/// </para>
/// </summary>
[Collection(nameof(BowireGraphQLProtocolGapFixture))]
public sealed class BowireGraphQLProtocolGapTests
{
    [Fact]
    public async Task Subscribe_Without_WebSocket_Plugin_Yields_Error_Envelope()
    {
        var emptyRegistry = new BowireProtocolRegistry();
        var prev = BowireGraphQLProtocol.RegistryFactory;
        BowireGraphQLProtocol.RegistryFactory = () => emptyRegistry;
        try
        {
            var protocol = new BowireGraphQLProtocol();
            var frames = new List<string>();
            await foreach (var frame in protocol.InvokeStreamAsync(
                serverUrl: "http://localhost/graphql",
                service: "Subscription",
                method: "onChat",
                jsonMessages: ["{}"],
                showInternalServices: false,
                metadata: new Dictionary<string, string>
                {
                    [BowireGraphQLProtocol.SubscriptionTransportMetadataKey] = "ws"
                },
                ct: TestContext.Current.CancellationToken))
            {
                frames.Add(frame);
            }

            Assert.Single(frames);
            Assert.Contains("WebSocket plugin", frames[0], StringComparison.Ordinal);
        }
        finally
        {
            BowireGraphQLProtocol.RegistryFactory = prev;
        }
    }

    [Fact]
    public async Task Subscribe_Via_GraphQLTransportWs_Skips_Unparseable_Frames()
    {
        // Stub channel emits, in order:
        //   1. "not valid json"                       → catch on outer Deserialize (line 371)
        //   2. {"type":"text","text":""}              → empty-string continue (line 386)
        //   3. {"type":"text","text":"still bad"}     → catch on inner Deserialize (line 388)
        //   4. {"type":"connection_ack"}              → ack arm, sends subscribe
        //   5. {"type":"complete","id":"1"}           → clean exit
        var stubProtocol = new StubWebSocketProtocol(
            "not valid json",
            """{"type":"text","text":""}""",
            """{"type":"text","text":"still bad"}""",
            """{"type":"connection_ack"}""",
            """{"type":"complete","id":"1"}""");
        var registry = new BowireProtocolRegistry();
        registry.Register(stubProtocol);

        var prev = BowireGraphQLProtocol.RegistryFactory;
        BowireGraphQLProtocol.RegistryFactory = () => registry;
        try
        {
            var protocol = new BowireGraphQLProtocol();
            var frames = new List<string>();
            await foreach (var frame in protocol.InvokeStreamAsync(
                serverUrl: "http://localhost/graphql",
                service: "Subscription",
                method: "onChat",
                jsonMessages: ["{}"],
                showInternalServices: false,
                metadata: new Dictionary<string, string>
                {
                    [BowireGraphQLProtocol.SubscriptionTransportMetadataKey] = "ws"
                },
                ct: TestContext.Current.CancellationToken))
            {
                frames.Add(frame);
            }

            // None of the skipped frames produced a `next` payload, and the
            // complete frame exits the loop cleanly without yielding.
            Assert.Empty(frames);
            // The ack must have triggered the subscribe envelope going out.
            Assert.Contains(stubProtocol.Channel.SentMessages,
                m => m.Contains(""""type":"subscribe"""", StringComparison.Ordinal));
        }
        finally
        {
            BowireGraphQLProtocol.RegistryFactory = prev;
        }
    }

    [Fact]
    public async Task Sse_Cancellation_Mid_ReadLine_Triggers_Inner_Catch_And_Exits()
    {
        // Strategy: serve one complete SSE record so the client emits a
        // frame and we know the consumer loop is past the SendAsync/headers
        // phase. Then signal "first frame seen" and cancel — the client's
        // next ReadLineAsync is now waiting on bytes that never come,
        // which is exactly when the inner OperationCanceledException catch
        // (line 296) in StreamViaSseAsync fires.
        var port = GetFreePort();
        var url = $"http://localhost:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();

        var stallExit = new TaskCompletionSource();

        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.StatusCode = 200;
            ctx.Response.SendChunked = true;
            // One complete SSE record produces a frame, then a partial
            // second line (no terminating newline) leaves the client's
            // next ReadLineAsync deterministically pending on `\n` — so
            // when we cancel the inner OperationCanceledException catch
            // on line 305 has no race window to dodge.
            var firstFrame = Encoding.UTF8.GetBytes("data: x\n\ndata: y");
            await ctx.Response.OutputStream.WriteAsync(firstFrame, TestContext.Current.CancellationToken);
            await ctx.Response.OutputStream.FlushAsync(TestContext.Current.CancellationToken);
            // Hold the connection open so the client's next ReadLineAsync
            // stays pending until the test cancels.
            await stallExit.Task;
            try { ctx.Response.Close(); } catch { /* connection may already be gone */ }
        }, TestContext.Current.CancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var firstFrameSeen = new TaskCompletionSource();

        var protocol = new BowireGraphQLProtocol();
        var consumerTask = Task.Run(async () =>
        {
            var frames = new List<string>();
            try
            {
                await foreach (var frame in protocol.InvokeStreamAsync(
                    serverUrl: url.TrimEnd('/'),
                    service: "Subscription",
                    method: "onPing",
                    jsonMessages: ["{}"],
                    showInternalServices: false,
                    metadata: new Dictionary<string, string>
                    {
                        [BowireGraphQLProtocol.SubscriptionTransportMetadataKey] = "sse"
                    },
                    ct: cts.Token))
                {
                    frames.Add(frame);
                    firstFrameSeen.TrySetResult();
                }
            }
            catch (OperationCanceledException)
            {
                // The [EnumeratorCancellation]-honouring foreach may surface
                // the cancellation here; the inner catch on line 296 is what
                // we care about, the outer rethrow is incidental.
            }
            return frames;
        }, TestContext.Current.CancellationToken);

        await firstFrameSeen.Task;
        // Let the iterator resume past the yield, evaluate the next while-
        // check, and enter the next ReadLineAsync await. Without this gap
        // the cancel can sneak in between the yield and the next while-
        // check, and the loop exits via the outer `while (!ct.IsCancellation
        // Requested)` instead of the inner OCE catch on line 305.
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        var collected = await consumerTask;
        stallExit.TrySetResult();
        try { await serverTask; } catch { /* listener tear-down */ }
        listener.Stop();

        Assert.Single(collected); // the first complete frame
    }

    private static int GetFreePort()
    {
        // Reserve a TCP port by binding loopback to port 0; the OS picks
        // an unused ephemeral port. Stopping the listener releases it for
        // HttpListener to reuse. CA2000 can't see that Stop() effectively
        // disposes the socket on this code path.
#pragma warning disable CA2000
        var l = new TcpListener(IPAddress.Loopback, 0);
#pragma warning restore CA2000
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class StubWebSocketProtocol : IBowireProtocol, IInlineWebSocketChannel
    {
        public StubWebSocketProtocol(params string[] frames)
        {
            Channel = new StubWebSocketChannel(frames);
        }

        public StubWebSocketChannel Channel { get; }

        public string Id => "stubws";
        public string Name => "Stub WS";
        public string IconSvg => "<svg/>";

        public Task<IBowireChannel> OpenAsync(
            string url, IReadOnlyList<string>? subProtocols,
            Dictionary<string, string>? headers, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel>(Channel);

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", []));

#pragma warning disable CS1998
        public async IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    internal sealed class StubWebSocketChannel : IBowireChannel
    {
        private readonly Queue<string> _frames;

        public StubWebSocketChannel(IEnumerable<string> frames)
        {
            _frames = new Queue<string>(frames);
        }

        public List<string> SentMessages { get; } = [];

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public bool IsClientStreaming => true;
        public bool IsServerStreaming => true;
        public int SentCount { get; private set; }
        public bool IsClosed { get; private set; }
        public long ElapsedMs => 0;
        public string? NegotiatedSubProtocol => "graphql-transport-ws";

        public Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default)
        {
            SentMessages.Add(jsonMessage);
            SentCount++;
            return Task.FromResult(true);
        }

        public Task CloseAsync(CancellationToken ct = default)
        {
            IsClosed = true;
            return Task.CompletedTask;
        }

#pragma warning disable CS1998 // async without await — the queue is preloaded
        public async IAsyncEnumerable<string> ReadResponsesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            while (_frames.TryDequeue(out var f))
            {
                yield return f;
            }
        }
#pragma warning restore CS1998

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>xUnit collection definition serialising the RegistryFactory seam.</summary>
[CollectionDefinition(nameof(BowireGraphQLProtocolGapFixture))]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit collection definition must be public.")]
public sealed class BowireGraphQLProtocolGapFixture { }
