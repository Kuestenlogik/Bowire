// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kuestenlogik.Bowire.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// Where <see cref="ConnectInvoker"/> sends its requests: a Connect server
/// mounted under a path prefix receives every call shape there.
/// </summary>
public sealed class ConnectInvokerRoutingTests
{
    private const string Service = "test.RouteService";
    private const string Method = "Call";

    // ---- base URL path prefix (#897) -------------------------------

    [Fact]
    public async Task Unary_keeps_the_base_url_path_prefix()
    {
        await using var server = await PrefixServer.StartAsync(HttpProtocols.Http1);
        using var invoker = new ConnectInvoker(server.BaseUrl + "/api");

        var result = await invoker.InvokeUnaryAsync(
            Service, Method, StringValue.Descriptor, StringValue.Descriptor,
            "\"q\"", metadata: null, TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal("\"ok\"", result.Response);
    }

    [Fact]
    public async Task Server_streaming_keeps_the_base_url_path_prefix()
    {
        await using var server = await PrefixServer.StartAsync(HttpProtocols.Http1);
        using var invoker = new ConnectInvoker(server.BaseUrl + "/api");

        var frames = new List<ConnectStreamFrame>();
        await foreach (var frame in invoker.InvokeServerStreamAsync(
            Service, Method, StringValue.Descriptor, StringValue.Descriptor,
            "\"q\"", metadata: null, TestContext.Current.CancellationToken))
        {
            frames.Add(frame);
        }

        var only = Assert.Single(frames);
        Assert.Null(only.ErrorCode);
        Assert.Equal("\"ok\"", only.Json);
    }

    [Fact]
    public async Task Client_streaming_keeps_the_base_url_path_prefix()
    {
        await using var server = await PrefixServer.StartAsync(HttpProtocols.Http2);
        using var invoker = new ConnectInvoker(server.BaseUrl + "/api");

        var result = await invoker.InvokeClientStreamAsync(
            Service, Method, StringValue.Descriptor, StringValue.Descriptor,
            ["\"q1\"", "\"q2\""], metadata: null, TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal("\"ok\"", result.Response);
    }

    [Fact]
    public async Task Bidi_streaming_keeps_the_base_url_path_prefix()
    {
        await using var server = await PrefixServer.StartAsync(HttpProtocols.Http2);
        using var invoker = new ConnectInvoker(server.BaseUrl + "/api");

        var frames = new List<ConnectStreamFrame>();
        await foreach (var frame in invoker.InvokeBidiStreamAsync(
            Service, Method, StringValue.Descriptor, StringValue.Descriptor,
            ["\"q1\"", "\"q2\""], metadata: null, TestContext.Current.CancellationToken))
        {
            frames.Add(frame);
        }

        var only = Assert.Single(frames);
        Assert.Null(only.ErrorCode);
        Assert.Equal("\"ok\"", only.Json);
    }

    /// <summary>
    /// A Connect listener that answers only under <c>/api</c>: a request
    /// that lost the prefix gets a 404. Unary calls get one protobuf body,
    /// streaming calls one message frame and a clean end of stream.
    /// </summary>
    private sealed class PrefixServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private PrefixServer(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }

        public static async Task<PrefixServer> StartAsync(HttpProtocols protocols)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            // Client and bidi streaming insist on HTTP/2, which cleartext
            // Kestrel speaks only on an HTTP/2-only endpoint.
            builder.WebHost.ConfigureKestrel(o =>
                o.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = protocols));
            var app = builder.Build();

            app.MapPost("/api/{service}/{method}", async (HttpContext ctx) =>
            {
                using var drained = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(drained, ctx.RequestAborted);

                var ok = new StringValue { Value = "ok" }.ToByteArray();
                ctx.Response.StatusCode = 200;
                if (ctx.Request.ContentType == "application/proto")
                {
                    ctx.Response.ContentType = "application/proto";
                    await ctx.Response.Body.WriteAsync(ok, ctx.RequestAborted);
                    return;
                }
                ctx.Response.ContentType = "application/connect+proto";
                await ctx.Response.Body.WriteAsync(
                    ConnectInvoker.EncodeFrame(0x00, ok), ctx.RequestAborted);
                await ctx.Response.Body.WriteAsync(
                    ConnectInvoker.EncodeFrame(ConnectInvoker.EndStreamFlag, Encoding.UTF8.GetBytes("{}")),
                    ctx.RequestAborted);
            });

            await app.StartAsync();
            return new PrefixServer(app, LoopbackHost.BaseAddress(app.Services));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
