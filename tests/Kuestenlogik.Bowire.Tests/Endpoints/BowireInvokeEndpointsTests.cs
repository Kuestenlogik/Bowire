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
/// Integration coverage for <see cref="BowireInvokeEndpoints"/> — the unary
/// POST /api/invoke path and the SSE GET /api/invoke/stream path. Dispatch runs
/// through the static protocol registry, driven via
/// <see cref="BowireEndpointHelpers.SetRegistry"/> with a configurable fake
/// plugin (success / throw / streaming). Joins StaticEndpointState
/// (DisableParallelization) so the registry swap never races the rest of the
/// suite.
/// </summary>
[Collection("StaticEndpointState")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope — app + client disposed by the caller.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic.")]
public sealed class BowireInvokeEndpointsTests
{
    private static readonly Uri InvokeUri = new("/api/invoke", UriKind.Relative);

    private sealed class StubProtocol(
        Func<InvokeResult>? invoke = null,
        bool invokeThrows = false,
        IEnumerable<string>? streamFrames = null) : IBowireProtocol
    {
        public string Id => "grpc";
        public string Name => "Stub";
        public string IconSvg => "<svg/>";
        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());
        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages, bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            if (invokeThrows) throw new InvalidOperationException("invoke boom");
            return Task.FromResult(invoke?.Invoke() ?? new InvokeResult("""{"ok":true}""", 3, "OK", new Dictionary<string, string>()));
        }
#pragma warning disable CS1998
        public async IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages, bool showInternalServices, Dictionary<string, string>? metadata = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var f in streamFrames ?? []) yield return f;
        }
#pragma warning restore CS1998
        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method, bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
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
        app.MapBowireInvokeEndpoints(new BowireOptions(), "");
        await app.StartAsync(ct).ConfigureAwait(false);
        return new Host(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // ------------------------------- unary POST -------------------------------

    [Fact]
    public async Task Invoke_success_returns_plugin_response()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct, new StubProtocol());
        using var resp = await h.Http.PostAsync(InvokeUri, Json("""{ "service": "S", "method": "M", "protocol": "grpc", "messages": ["{}"] }"""), ct);
        resp.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.Equal("OK", body.GetProperty("status").GetString());
        Assert.Contains("ok", body.GetProperty("response").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_plugin_throwing_maps_to_502()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct, new StubProtocol(invokeThrows: true));
        using var resp = await h.Http.PostAsync(InvokeUri, Json("""{ "service": "S", "method": "M", "protocol": "grpc" }"""), ct);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task Invoke_with_no_plugin_is_502()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct); // empty registry
        using var resp = await h.Http.PostAsync(InvokeUri, Json("""{ "service": "S", "method": "M", "protocol": "grpc" }"""), ct);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task Invoke_malformed_json_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct, new StubProtocol());
        using var resp = await h.Http.PostAsync(InvokeUri, Json("{ not json"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Invoke_null_body_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct, new StubProtocol());
        using var resp = await h.Http.PostAsync(InvokeUri, Json("null"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Invoke_transcoded_without_rest_plugin_is_501()
    {
        var ct = TestContext.Current.CancellationToken;
        // Stub protocol is not an IInlineHttpInvoker → FindHttpInvoker() is null.
        await using var h = await StartAsync(ct, new StubProtocol());
        using var resp = await h.Http.PostAsync(InvokeUri, Json("""
            { "service": "S", "method": "M", "protocol": "grpc", "transcodedMethod": { "httpMethod": "GET", "httpPath": "/x" } }
            """), ct);
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
    }

    // ------------------------------- SSE stream -------------------------------

    [Fact]
    public async Task Stream_missing_service_or_method_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct, new StubProtocol());
        using var resp = await h.Http.GetAsync(new Uri("/api/invoke/stream?service=S", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Stream_no_plugin_emits_error_event()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct); // empty registry
        using var resp = await h.Http.GetAsync(new Uri("/api/invoke/stream?service=S&method=M&protocol=grpc", UriKind.Relative), ct);
        resp.EnsureSuccessStatusCode();
        Assert.Contains("event: error", await resp.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_success_emits_frames_then_done()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct, new StubProtocol(streamFrames: ["""{"i":0}""", """{"i":1}"""]));
        using var resp = await h.Http.GetAsync(
            new Uri("/api/invoke/stream?service=S&method=M&protocol=grpc", UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        var text = await resp.Content.ReadAsStringAsync(ct);
        // The frame JSON is string-escaped inside the SSE envelope; assert on
        // the envelope's own (unescaped) index field + the terminal event.
        Assert.Contains("\"index\":0", text, StringComparison.Ordinal);
        Assert.Contains("\"index\":1", text, StringComparison.Ordinal);
        Assert.Contains("event: done", text, StringComparison.Ordinal);
    }
}
