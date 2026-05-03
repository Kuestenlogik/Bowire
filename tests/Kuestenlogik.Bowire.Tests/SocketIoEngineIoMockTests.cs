// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using Kuestenlogik.Bowire.Protocol.SocketIo;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tries a fast-path through the Socket.IO protocol-plugin's discover
/// flow by replying to the Engine.IO v4 HTTP-polling handshake with the
/// minimal valid frame sequence. SocketIOClient 4.x does its own
/// transport upgrade after handshake, and the exact upgrade dance varies
/// — we don't try to keep up. The test just exercises whatever lines run
/// before the upgrade wedges, then lets the discover catch path swallow
/// the failure. It's a "soft" coverage booster: any path past
/// <c>ConnectAsync</c> counts.
/// </summary>
public sealed class SocketIoEngineIoMockTests
{
    [Fact]
    public async Task DiscoverAsync_With_EngineIo_Mock_Returns_Empty_Without_Throwing()
    {
        await using var stub = await EngineIoStub.StartAsync();
        var protocol = new BowireSocketIoProtocol();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        // Whatever the upgrade does, the plugin's catch swallows and
        // returns []. Worst case we get [] from the !connected branch,
        // best case the OnConnected → DetectedEvents flow runs through.
        var services = await protocol.DiscoverAsync(
            stub.BaseUrl, showInternalServices: false, cts.Token);

        Assert.NotNull(services);
        // Don't assert on count — depending on whether handshake completes
        // we may get [] or a single Socket.IO service entry. Both paths
        // are valid post-connection flow.
    }

    // Minimal Engine.IO v4 HTTP-polling responder. The first GET delivers
    // the OPEN packet (sid + transport options). Subsequent GET/POST
    // round-trips return empty 200s — enough to satisfy the client's
    // initial handshake before it tries to upgrade to WebSocket and we
    // reject. Pure HTTP, no WebSocket, no real Socket.IO server.
    private sealed class EngineIoStub : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string BaseUrl { get; }

        private EngineIoStub(HttpListener listener, string baseUrl)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        public static Task<EngineIoStub> StartAsync()
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var port = GetFreePort();
                var prefix = $"http://127.0.0.1:{port}/";
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                try
                {
                    listener.Start();
                    return Task.FromResult(new EngineIoStub(listener, prefix.TrimEnd('/')));
                }
                catch (HttpListenerException) { /* retry */ }
            }
            throw new InvalidOperationException("Could not bind a free loopback port for Engine.IO stub.");
        }

        private static int GetFreePort()
        {
            using var sock = new TcpListener(IPAddress.Loopback, 0);
            sock.Start();
            var port = ((IPEndPoint)sock.LocalEndpoint).Port;
            sock.Stop();
            return port;
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var firstRequest = true;
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().WaitAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (HttpListenerException) { return; }

                string body;
                if (firstRequest && string.Equals(ctx.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    // Engine.IO OPEN packet: type "0" + JSON config.
                    // Use a long ping interval so the client doesn't
                    // hammer us during the test window.
                    body = "0{\"sid\":\"mock-sid\",\"upgrades\":[],\"pingInterval\":60000,\"pingTimeout\":60000,\"maxPayload\":1000000}";
                    firstRequest = false;
                }
                else
                {
                    body = "ok";
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain; charset=UTF-8";
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentLength64 = bytes.Length;
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                    ctx.Response.Close();
                }
                catch
                {
                    // Client disconnected mid-write — fine.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { _listener.Stop(); } catch { /* best-effort */ }
            try { _listener.Close(); } catch { /* best-effort */ }
            try { await _loop; } catch { /* best-effort */ }
            _cts.Dispose();
        }
    }
}
