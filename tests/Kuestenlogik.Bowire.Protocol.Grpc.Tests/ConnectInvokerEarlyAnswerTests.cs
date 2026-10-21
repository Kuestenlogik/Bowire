// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Kuestenlogik.Bowire.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// A Connect server may end a bidi stream before it has read the request,
/// for instance to refuse the caller. The invoker has to finish then, not
/// wait for a request body nobody reads any more (#898).
/// </summary>
public sealed class ConnectInvokerEarlyAnswerTests
{
    [Fact(Timeout = 60_000)]
    public async Task Bidi_finishes_when_the_server_ends_the_stream_before_reading_the_request()
    {
        await using var server = await EarlyAnswerServer.StartAsync();
        using var invoker = new ConnectInvoker(server.BaseUrl);

        // Far more than the server's HTTP/2 receive window and the request
        // pipe together hold, so the producer is still writing when the
        // server has already answered.
        var big = "\"" + new string('x', 64 * 1024) + "\"";
        var requests = Enumerable.Repeat(big, 64).ToList();

        var frames = new List<ConnectStreamFrame>();
        await foreach (var frame in invoker.InvokeBidiStreamAsync(
            "test.EarlyService", "Chat", StringValue.Descriptor, StringValue.Descriptor,
            requests, metadata: null, TestContext.Current.CancellationToken))
        {
            frames.Add(frame);
        }

        var only = Assert.Single(frames);
        Assert.Equal("connect:unauthenticated", only.ErrorCode);
    }

    /// <summary>
    /// An HTTP/2 Connect listener that answers every call at once with an
    /// end-of-stream error and never reads the request body.
    /// </summary>
    private sealed class EarlyAnswerServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private EarlyAnswerServer(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }

        public static async Task<EarlyAnswerServer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
                o.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = HttpProtocols.Http2));
            var app = builder.Build();

            app.MapPost("/{service}/{method}", async (HttpContext ctx) =>
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/connect+proto";
                var end = Encoding.UTF8.GetBytes("""{"error":{"code":"unauthenticated","message":"no"}}""");
                await ctx.Response.Body.WriteAsync(
                    ConnectInvoker.EncodeFrame(ConnectInvoker.EndStreamFlag, end), ctx.RequestAborted);
            });

            await app.StartAsync();
            return new EarlyAnswerServer(app, LoopbackHost.BaseAddress(app.Services));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
