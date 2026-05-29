// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Threading.Channels;
using Kuestenlogik.Bowire.Plugins.Sidecar;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Kuestenlogik.Bowire.IntegrationTests.Plugins;

/// <summary>
/// Drives <see cref="SidecarBowireProtocol"/> over the HTTP/SSE transport
/// against an in-process fake sidecar service: JSON-RPC requests POST to
/// the endpoint, notifications stream back over SSE. Proves the dual-
/// transport story end-to-end without a separate process.
/// </summary>
public sealed class SidecarHttpTransportIntegrationTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string _endpoint = "";

    // SSE events the fake pushes to the (single) connected client.
    private readonly Channel<string> _sse =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        // SSE notification stream — held open, flushes each enqueued event.
        _app.MapGet("/rpc", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            try
            {
                await foreach (var evt in _sse.Reader.ReadAllAsync(ctx.RequestAborted))
                {
                    await ctx.Response.WriteAsync($"data: {evt}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { /* client went away */ }
        });

        // JSON-RPC request endpoint.
        _app.MapPost("/rpc", async (HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString();
            var id = root.TryGetProperty("id", out var idEl) ? idEl.Clone() : default;
            var p = root.TryGetProperty("params", out var pEl) ? pEl : default;

            object result;
            switch (method)
            {
                case "initialize":
                    result = new { name = "Http Echo", id = "httpecho", iconSvg = "<svg/>", settings = Array.Empty<object>() };
                    break;
                case "ping":
                    result = "pong";
                    break;
                case "shutdown":
                    result = true;
                    break;
                case "discover":
                    result = new[]
                    {
                        new
                        {
                            name = "Echo",
                            package = "httpecho",
                            source = "httpecho",
                            originUrl = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("serverUrl", out var su) ? su.GetString() : null,
                            methods = new[]
                            {
                                new { name = "echo", fullName = "Echo/echo", clientStreaming = false, serverStreaming = false, methodType = "Unary",
                                      inputType = new { name = "I", fullName = "I", fields = Array.Empty<object>() },
                                      outputType = new { name = "O", fullName = "O", fields = Array.Empty<object>() } },
                            },
                        },
                    };
                    break;
                case "invoke":
                    var msg = p.TryGetProperty("jsonMessages", out var jm) && jm.ValueKind == JsonValueKind.Array && jm.GetArrayLength() > 0
                        ? jm[0].GetString() : "";
                    result = new { response = "http-echo: " + msg, durationMs = 1, status = "OK", metadata = new { via = "http" } };
                    break;
                case "invokeStream":
                    var streamId = p.GetProperty("streamId").GetString();
                    result = new { streamId };
                    // Push the stream frames over SSE after replying.
                    for (var i = 1; i <= 3; i++)
                        await _sse.Writer.WriteAsync(
                            JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "$/stream/data", @params = new { streamId, message = $"htick-{i}" } }),
                            ctx.RequestAborted);
                    await _sse.Writer.WriteAsync(
                        JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "$/stream/end", @params = new { streamId, error = (object?)null } }),
                        ctx.RequestAborted);
                    break;
                default:
                    await ctx.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "method not found" } });
                    return;
            }
            await ctx.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, result });
        });

        await _app.StartAsync();
        var addr = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _endpoint = addr + "/rpc";
    }

    public async ValueTask DisposeAsync()
    {
        _sse.Writer.TryComplete();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private SidecarBowireProtocol BuildPlugin() => new(
        new SidecarPluginManifest(
            PackageId: "Kuestenlogik.Bowire.Tests.HttpSidecar",
            Protocol: new SidecarProtocolMetadata("httpecho", "Http Echo"),
            Transport: "http",
            Url: _endpoint),
        pluginDir: Path.GetTempPath());

    [Fact]
    public async Task Http_Discover_Round_Trips()
    {
        var plugin = BuildPlugin();
        var services = await plugin.DiscoverAsync("httpecho://demo", false, TestContext.Current.CancellationToken);
        var svc = Assert.Single(services);
        Assert.Equal("Echo", svc.Name);
        Assert.Equal("httpecho://demo", svc.OriginUrl);
        await DisposeTransport(plugin);
    }

    [Fact]
    public async Task Http_Invoke_Round_Trips()
    {
        var plugin = BuildPlugin();
        var result = await plugin.InvokeAsync("httpecho://demo", "Echo", "Echo/echo",
            ["hi"], false, null, TestContext.Current.CancellationToken);
        Assert.Equal("OK", result.Status);
        Assert.Equal("http-echo: hi", result.Response);
        Assert.Equal("http", result.Metadata["via"]);
        await DisposeTransport(plugin);
    }

    [Fact]
    public async Task Http_InvokeStream_Receives_Frames_Over_Sse()
    {
        var plugin = BuildPlugin();
        var got = new List<string>();
        await foreach (var frame in plugin.InvokeStreamAsync("httpecho://demo", "Echo", "Echo/echo",
            [], false, null, TestContext.Current.CancellationToken))
        {
            got.Add(frame);
            if (got.Count >= 3) break;
        }
        Assert.Equal(["htick-1", "htick-2", "htick-3"], got);
        await DisposeTransport(plugin);
    }

    [Fact]
    public async Task Http_First_Call_Reflects_Sidecar_Metadata()
    {
        var plugin = BuildPlugin();
        _ = await plugin.DiscoverAsync("httpecho://x", false, TestContext.Current.CancellationToken);
        Assert.Equal("httpecho", plugin.Id);
        Assert.Equal("Http Echo", plugin.Name);
        Assert.Equal("<svg/>", plugin.IconSvg);
        await DisposeTransport(plugin);
    }

    private static async Task DisposeTransport(SidecarBowireProtocol plugin)
    {
        try
        {
            var t = await plugin.EnsureStartedAsync(CancellationToken.None);
            await t.DisposeAsync();
        }
        catch { /* best-effort */ }
    }
}
