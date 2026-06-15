// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.Sse;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.Sse.Tests;

/// <summary>
/// Fills the remaining BowireSseProtocol gaps:
/// <list type="bullet">
///   <item><see cref="BowireSseProtocol.Description"/> is exposed (one-liner
///     property that the discovery surface uses);</item>
///   <item><c>InvokeStreamAsync</c> resolves the URL via the
///     SSE-prefix-strip branch when the caller passes the synthesised
///     <c>SSE/...</c> shape (matches the legacy fullName format);</item>
///   <item><c>InvokeStreamAsync</c> resolves a raw method name that
///     already starts with <c>/</c> verbatim (the no-leading-slash else
///     arm), and a bare method name without a slash gets <c>/</c>
///     prepended;</item>
///   <item><c>InvokeStreamAsync</c> honours an absolute <c>http://</c>
///     URL override in the request body, and a relative <c>/path</c>
///     override that re-anchors on the supplied <c>serverUrl</c>.</item>
/// </list>
/// Each test points at a real in-process Kestrel SSE endpoint so the URL
/// the protocol resolves is actually walked end-to-end.
/// </summary>
[Collection<SseTestGroup>]
public sealed class CoverageTo95Tests : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _baseUrl;

    public CoverageTo95Tests()
    {
        BowireSseProtocol.ClearRegisteredEndpoints();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.ConfigureKestrel((Action<KestrelServerOptions>)(o =>
            o.Listen(IPAddress.Loopback, 0)));
        builder.Logging.ClearProviders();
        _app = builder.Build();

        _app.MapGet("/events/heartbeat", WriteOne);
        _app.MapGet("/events/legacy",   WriteOne);
        _app.MapGet("/events/raw-path", WriteOne);
        _app.MapGet("/events/override", WriteOne);

        _app.Start();
        var addressFeature = _app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        _baseUrl = addressFeature!.Addresses.First();
    }

    public async ValueTask DisposeAsync()
    {
        BowireSseProtocol.ClearRegisteredEndpoints();
        await _app.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static async Task WriteOne(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream";
        await ctx.Response.WriteAsync(
            "data: ok\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    [Fact]
    public void Description_is_a_non_empty_one_liner()
    {
        // Pins the property that powers the workbench's protocol picker
        // card. Empty / null here would surface as a blank card subtitle.
        var protocol = new BowireSseProtocol();
        Assert.False(string.IsNullOrWhiteSpace(protocol.Description));
        Assert.Contains("Server-Sent Events", protocol.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStreamAsync_strips_SSE_prefix_from_method_full_name()
    {
        // Method "SSE/events/heartbeat" → URL = base + "/events/heartbeat".
        // This is the legacy fullName parse path (no matching registration).
        var protocol = new BowireSseProtocol();
        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            _baseUrl,
            service: "anything",
            method: "SSE/events/heartbeat",
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            events.Add(evt);
            break;
        }

        var json = Assert.Single(events);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("Data").GetString());
    }

    [Fact]
    public async Task InvokeStreamAsync_keeps_method_verbatim_when_it_starts_with_slash()
    {
        // Hits the no-leading-slash else branch with "already starts" path
        // → URL = base + method as-is.
        var protocol = new BowireSseProtocol();
        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            _baseUrl,
            service: "any",
            method: "/events/raw-path",
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            events.Add(evt);
            break;
        }

        var json = Assert.Single(events);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("Data").GetString());
    }

    [Fact]
    public async Task InvokeStreamAsync_honours_absolute_http_url_override_from_body()
    {
        // Server URL is bogus, but the body's "url" field carries an
        // absolute http URL — that wins over the serverUrl + method
        // concatenation, exercising the http-prefix branch.
        var protocol = new BowireSseProtocol();
        var absoluteOverride = _baseUrl + "/events/override";
        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            serverUrl: "http://127.0.0.1:1",
            service: "any",
            method: "/never-used",
            jsonMessages: ["{\"url\":\"" + absoluteOverride + "\"}"],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            events.Add(evt);
            break;
        }

        var json = Assert.Single(events);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("Data").GetString());
    }

    [Fact]
    public async Task InvokeStreamAsync_anchors_relative_slash_url_override_on_server_url()
    {
        // Override is "/events/override" → relative branch — re-anchors
        // on serverUrl, not the original method route.
        var protocol = new BowireSseProtocol();
        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            serverUrl: _baseUrl,
            service: "any",
            method: "/never-used",
            jsonMessages: ["{\"url\":\"/events/override\"}"],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            events.Add(evt);
            break;
        }

        var json = Assert.Single(events);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("Data").GetString());
    }
}
