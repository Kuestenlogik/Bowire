// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end coverage for <see cref="ConnectInvoker"/>. Skips the
/// plugin-level discovery dance — the Connect Phase 1 contract is
/// "valid descriptor in, Connect POST out, Connect response in,
/// InvokeResult out". Discovery routes still need a native HTTP/2
/// channel (gRPC Reflection requires HTTP/2 trailers), which the
/// Roadmap calls out as a Phase-1 limitation; these tests cover the
/// wire envelope by handing the invoker a pre-resolved descriptor
/// directly.
/// </summary>
/// <remarks>
/// Hosts a minimal hand-rolled Connect endpoint on HTTP/1.1 — no
/// ASP.NET gRPC stack, no reflection, just a plain
/// <c>MapPost</c> that mirrors the bits of the Connect wire envelope
/// the invoker actually consumes.
/// </remarks>
[Collection(nameof(RestInvokerEndToEndFixture))]
public sealed class ConnectInvokerIntegrationTests
{
    private const string ConnectVersionHeader = "Connect-Protocol-Version";
    private const string ProtobufContentType = "application/proto";

    [Fact]
    public async Task InvokeUnaryAsync_RoundTrips_HelloReply_Payload()
    {
        await using var host = await StartStubAsync(replyMessage: "Hello connect!");
        using var invoker = new ConnectInvoker(host.BaseUrl);

        var result = await invoker.InvokeUnaryAsync(
            serviceName: "test.Greeter",
            methodName: "SayHello",
            inputType: HelloRequest.Descriptor,
            outputType: HelloReply.Descriptor,
            requestJson: """{"name":"connect"}""",
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("Hello connect!", result.Response!, StringComparison.Ordinal);
        Assert.NotNull(result.ResponseBinary);
    }

    [Fact]
    public async Task InvokeUnaryAsync_Decodes_Connect_Error_With_Prefix()
    {
        await using var host = await StartStubAsync(
            forceError: ("invalid_argument", "name is required"));
        using var invoker = new ConnectInvoker(host.BaseUrl);

        var result = await invoker.InvokeUnaryAsync(
            serviceName: "test.Greeter",
            methodName: "SayHello",
            inputType: HelloRequest.Descriptor,
            outputType: HelloReply.Descriptor,
            requestJson: """{"name":""}""",
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.StartsWith("connect:", result.Status, StringComparison.Ordinal);
        Assert.Contains("invalid_argument", result.Status, StringComparison.Ordinal);
        Assert.NotNull(result.Response);
        Assert.Contains("name is required", result.Response!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeUnaryAsync_NonJsonErrorBody_FallsBackToHttpStatus()
    {
        // Server returns 503 with a plain-text body that isn't a valid
        // Connect error JSON — the invoker should still surface a usable
        // status + message rather than throwing.
        await using var host = await StartStubAsync(forceHtmlError: true);
        using var invoker = new ConnectInvoker(host.BaseUrl);

        var result = await invoker.InvokeUnaryAsync(
            serviceName: "test.Greeter",
            methodName: "SayHello",
            inputType: HelloRequest.Descriptor,
            outputType: HelloReply.Descriptor,
            requestJson: """{"name":"x"}""",
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.StartsWith("http:", result.Status, StringComparison.Ordinal);
        Assert.Contains("503", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeUnaryAsync_Forwards_User_Metadata_As_Headers()
    {
        // Stub captures headers and echoes them in a response so we can
        // assert the round-trip.
        await using var host = await StartStubAsync(replyMessage: "echo", captureHeaders: true);
        using var invoker = new ConnectInvoker(host.BaseUrl);

        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-custom"] = "bowire-test",
            ["x-trace"] = "abc-123",
        };

        var result = await invoker.InvokeUnaryAsync(
            serviceName: "test.Greeter",
            methodName: "SayHello",
            inputType: HelloRequest.Descriptor,
            outputType: HelloReply.Descriptor,
            requestJson: """{"name":"meta"}""",
            metadata: meta,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        // The stub mirrors received headers back as response headers
        // prefixed with "received-".
        Assert.Contains(result.Metadata, kv =>
            kv.Key.Equals("received-x-custom", StringComparison.OrdinalIgnoreCase)
            && kv.Value == "bowire-test");
        Assert.Contains(result.Metadata, kv =>
            kv.Key.Equals("received-x-trace", StringComparison.OrdinalIgnoreCase)
            && kv.Value == "abc-123");
    }

    [Fact]
    public async Task InvokeUnaryAsync_Sets_Connect_Protocol_Version_Header()
    {
        // Stub rejects calls missing Connect-Protocol-Version: 1 with a
        // 400 + connect-error JSON. A successful round-trip implies the
        // invoker set the header.
        await using var host = await StartStubAsync(replyMessage: "version-ok");
        using var invoker = new ConnectInvoker(host.BaseUrl);

        var result = await invoker.InvokeUnaryAsync(
            serviceName: "test.Greeter",
            methodName: "SayHello",
            inputType: HelloRequest.Descriptor,
            outputType: HelloReply.Descriptor,
            requestJson: """{"name":"v"}""",
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
    }

    // ----- stub host ----------------------------------------------------

    private static async Task<ConnectStubHost> StartStubAsync(
        string? replyMessage = null,
        (string Code, string Message)? forceError = null,
        bool forceHtmlError = false,
        bool captureHeaders = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.Listen(IPAddress.Loopback, 0, lo =>
                lo.Protocols = HttpProtocols.Http1AndHttp2);
        });
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapPost("/test.Greeter/SayHello", async (HttpContext ctx) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);

            var version = ctx.Request.Headers[ConnectVersionHeader].ToString();
            if (string.IsNullOrEmpty(version))
            {
                await WriteConnectErrorAsync(ctx, 400,
                    "invalid_argument", $"missing {ConnectVersionHeader} header");
                return;
            }

            if (forceHtmlError)
            {
                ctx.Response.StatusCode = 503;
                ctx.Response.ContentType = "text/html";
                await ctx.Response.WriteAsync("<html><body>Bad Gateway</body></html>");
                return;
            }
            if (forceError is { } err)
            {
                await WriteConnectErrorAsync(ctx, 400, err.Code, err.Message);
                return;
            }

            if (captureHeaders)
            {
                foreach (var (name, values) in ctx.Request.Headers)
                {
                    if (!name.StartsWith("x-", StringComparison.OrdinalIgnoreCase)) continue;
                    ctx.Response.Headers["received-" + name] = values;
                }
            }

            var reply = new HelloReply { Message = replyMessage ?? "" };
            var bytes = reply.ToByteArray();
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = ProtobufContentType;
            ctx.Response.ContentLength = bytes.Length;
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        });

        await app.StartAsync(TestContext.Current.CancellationToken);
        return new ConnectStubHost(app, ResolveBoundUrl(app));
    }

    private static async Task WriteConnectErrorAsync(
        HttpContext ctx, int httpStatus, string code, string message)
    {
        ctx.Response.StatusCode = httpStatus;
        ctx.Response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(new { code, message });
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
    }

    private static string ResolveBoundUrl(WebApplication app)
    {
        foreach (var u in app.Urls)
        {
            return u.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)
                    .Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);
        }
        throw new InvalidOperationException("Kestrel didn't publish any bound URL after StartAsync.");
    }

    private sealed class ConnectStubHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string BaseUrl { get; }

        public ConnectStubHost(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
