// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// Integration coverage for <see cref="BowireChannelEndpoints"/> — the four
/// duplex-channel routes (open / send / close / SSE responses). open dispatches
/// through the static protocol registry (driven via
/// <see cref="BowireEndpointHelpers.SetRegistry"/> with a fake plugin); send /
/// close / responses operate on the static <c>ChannelStore</c>, seeded directly
/// with a fake channel. Joins the StaticEndpointState collection
/// (DisableParallelization) so the process-global registry + channel store never
/// race the rest of the suite.
/// </summary>
[Collection("StaticEndpointState")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope — app + client disposed by the caller.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic.")]
public sealed class BowireChannelEndpointsTests
{
    private static readonly Uri OpenUri = new("/api/channel/open", UriKind.Relative);

    // ---- fakes ----

    private sealed class FakeChannel(string id, bool closed = false, bool throwOnSend = false, IEnumerable<string>? responses = null)
        : IBowireChannel
    {
        private readonly List<string> _responses = responses?.ToList() ?? [];
        public string Id { get; } = id;
        public bool IsClientStreaming => true;
        public bool IsServerStreaming => true;
        public int SentCount { get; private set; }
        public bool IsClosed { get; private set; } = closed;
        public long ElapsedMs => 5;
        public Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default)
        {
            if (throwOnSend) throw new InvalidOperationException("send boom");
            SentCount++;
            return Task.FromResult(true);
        }
        public Task CloseAsync(CancellationToken ct = default) { IsClosed = true; return Task.CompletedTask; }
#pragma warning disable CS1998
        public async IAsyncEnumerable<string> ReadResponsesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var r in _responses) yield return r;
        }
#pragma warning restore CS1998
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubProtocol(Func<IBowireChannel?>? channelFactory = null, bool openThrows = false) : IBowireProtocol
    {
        public string Id => "grpc";
        public string Name => "Stub";
        public string IconSvg => "<svg/>";
        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());
        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages, bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", new Dictionary<string, string>()));
#pragma warning disable CS1998
        public async IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages, bool showInternalServices, Dictionary<string, string>? metadata = null, [EnumeratorCancellation] CancellationToken ct = default)
        { yield break; }
#pragma warning restore CS1998
        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method, bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            if (openThrows) throw new InvalidOperationException("open boom");
            return Task.FromResult(channelFactory?.Invoke());
        }
    }

    private sealed record Host(WebApplication App, HttpClient Http) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync().ConfigureAwait(false);
            BowireEndpointHelpers.ResetRegistry();
        }
    }

    private static async Task<Host> StartAsync(CancellationToken ct, IBowireProtocol? protocol = null)
    {
        var reg = new BowireProtocolRegistry();
        if (protocol is not null) reg.Register(protocol);
        BowireEndpointHelpers.SetRegistry(reg);

        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = b.Build();
        app.MapBowireChannelEndpoints(new BowireOptions(), "");
        await app.StartAsync(ct).ConfigureAwait(false);
        return new Host(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    private static async Task<JsonElement> ReadJson(HttpResponseMessage r, CancellationToken ct) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.Clone();

    // ------------------------------- open -------------------------------

    [Fact]
    public async Task Open_missing_service_or_method_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.PostAsync(OpenUri, Json("""{ "service": "S" }"""), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Open_with_no_plugin_is_502()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct); // empty registry
        using var resp = await h.Http.PostAsync(OpenUri, Json("""{ "service": "S", "method": "M", "protocol": "grpc" }"""), ct);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task Open_success_registers_channel()
    {
        var ct = TestContext.Current.CancellationToken;
        var channelId = "chan-" + Guid.NewGuid().ToString("N");
        await using var h = await StartAsync(ct, new StubProtocol(() => new FakeChannel(channelId)));
        try
        {
            using var resp = await h.Http.PostAsync(OpenUri, Json("""{ "service": "S", "method": "M", "protocol": "grpc" }"""), ct);
            resp.EnsureSuccessStatusCode();
            var body = await ReadJson(resp, ct);
            Assert.Equal(channelId, body.GetProperty("channelId").GetString());
            Assert.True(body.GetProperty("clientStreaming").GetBoolean());
            Assert.NotNull(ChannelStore.Get(channelId));
        }
        finally { ChannelStore.Remove(channelId); }
    }

    [Fact]
    public async Task Open_unary_method_returns_channel_unsupported_400()
    {
        var ct = TestContext.Current.CancellationToken;
        // protocol returns a null channel → the method maps to a unary call.
        await using var h = await StartAsync(ct, new StubProtocol(() => null));
        using var resp = await h.Http.PostAsync(OpenUri, Json("""{ "service": "S", "method": "M", "protocol": "grpc" }"""), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Open_plugin_throwing_maps_to_502()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct, new StubProtocol(openThrows: true));
        using var resp = await h.Http.PostAsync(OpenUri, Json("""{ "service": "S", "method": "M", "protocol": "grpc" }"""), ct);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    // ------------------------------- send -------------------------------

    [Fact]
    public async Task Send_round_trip_and_error_cases()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        var okId = "ok-" + Guid.NewGuid().ToString("N");
        var closedId = "closed-" + Guid.NewGuid().ToString("N");
        var boomId = "boom-" + Guid.NewGuid().ToString("N");
        ChannelStore.Add(new FakeChannel(okId));
        ChannelStore.Add(new FakeChannel(closedId, closed: true));
        ChannelStore.Add(new FakeChannel(boomId, throwOnSend: true));
        try
        {
            // 400 missing message
            using (var r = await h.Http.PostAsync(new Uri($"/api/channel/{okId}/send", UriKind.Relative), Json("{}"), ct))
                Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            // 404 unknown channel
            using (var r = await h.Http.PostAsync(new Uri("/api/channel/nope/send", UriKind.Relative), Json("""{ "message": "hi" }"""), ct))
                Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            // 400 closed channel
            using (var r = await h.Http.PostAsync(new Uri($"/api/channel/{closedId}/send", UriKind.Relative), Json("""{ "message": "hi" }"""), ct))
                Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            // 500 send throws
            using (var r = await h.Http.PostAsync(new Uri($"/api/channel/{boomId}/send", UriKind.Relative), Json("""{ "message": "hi" }"""), ct))
                Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
            // 200 sent
            using (var r = await h.Http.PostAsync(new Uri($"/api/channel/{okId}/send", UriKind.Relative), Json("""{ "message": "hi" }"""), ct))
            {
                r.EnsureSuccessStatusCode();
                var body = await ReadJson(r, ct);
                Assert.True(body.GetProperty("sent").GetBoolean());
            }
        }
        finally
        {
            ChannelStore.Remove(okId); ChannelStore.Remove(closedId); ChannelStore.Remove(boomId);
        }
    }

    // ------------------------------- close -------------------------------

    [Fact]
    public async Task Close_unknown_is_404_and_known_is_200()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using (var r = await h.Http.PostAsync(new Uri("/api/channel/nope/close", UriKind.Relative), content: null, ct))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        var id = "close-" + Guid.NewGuid().ToString("N");
        ChannelStore.Add(new FakeChannel(id));
        try
        {
            using var r = await h.Http.PostAsync(new Uri($"/api/channel/{id}/close", UriKind.Relative), content: null, ct);
            r.EnsureSuccessStatusCode();
            var body = await ReadJson(r, ct);
            Assert.True(body.GetProperty("closed").GetBoolean());
        }
        finally { ChannelStore.Remove(id); }
    }

    // ------------------------------- responses (SSE) -------------------------------

    [Fact]
    public async Task Responses_unknown_channel_is_404()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var r = await h.Http.GetAsync(new Uri("/api/channel/nope/responses", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Responses_streams_frames_then_done()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        var id = "resp-" + Guid.NewGuid().ToString("N");
        // The endpoint removes + disposes the channel when the stream ends.
        ChannelStore.Add(new FakeChannel(id, responses: ["""{"n":1}""", """{"n":2}"""]));

        using var r = await h.Http.GetAsync(new Uri($"/api/channel/{id}/responses", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, ct);
        r.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", r.Content.Headers.ContentType?.MediaType);
        var text = await r.Content.ReadAsStringAsync(ct);
        // The frame JSON is string-escaped inside the SSE envelope; assert on
        // the envelope's own (unescaped) index field + the terminal event.
        Assert.Contains("\"index\":0", text, StringComparison.Ordinal);
        Assert.Contains("event: done", text, StringComparison.Ordinal);
    }
}
