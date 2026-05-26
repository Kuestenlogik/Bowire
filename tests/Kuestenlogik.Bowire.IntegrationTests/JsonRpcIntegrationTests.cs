// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.JsonRpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end coverage for <see cref="BowireJsonRpcProtocol"/>. Hosts
/// a hand-rolled JSON-RPC 2.0 stub that mirrors the bits of the spec
/// the plugin actually consumes (request envelope decoding,
/// success / error envelope shapes, OpenRPC's
/// <c>rpc.discover</c> convention).
/// </summary>
[Collection(nameof(RestInvokerEndToEndFixture))]
public sealed class JsonRpcIntegrationTests
{
    [Fact]
    public async Task DiscoverAsync_Picks_Up_OpenRpc_Methods()
    {
        await using var host = await StartStubAsync(supportsDiscover: true);
        var p = new BowireJsonRpcProtocol();
        p.Initialize(null);

        var services = await p.DiscoverAsync(
            host.BaseUrl, showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        Assert.Equal("Methods", svc.Name);
        var methodNames = svc.Methods.Select(m => m.Name).ToHashSet();
        Assert.Contains("echo", methodNames);
        Assert.Contains("add", methodNames);
    }

    [Fact]
    public async Task DiscoverAsync_Server_Without_OpenRpc_Returns_Empty_Tree_But_Claims_URL()
    {
        // A reachable JSON-RPC server that doesn't implement rpc.discover
        // still belongs to this plugin — Bowire returns a placeholder
        // Methods service so the user can invoke by name.
        await using var host = await StartStubAsync(supportsDiscover: false);
        var p = new BowireJsonRpcProtocol();
        p.Initialize(null);

        var services = await p.DiscoverAsync(
            host.BaseUrl, showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        Assert.Empty(svc.Methods);
        Assert.Contains("OpenRPC", svc.Description!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_RoundTrips_Named_Params_And_Result()
    {
        await using var host = await StartStubAsync(supportsDiscover: true);
        var p = new BowireJsonRpcProtocol();
        p.Initialize(null);

        var result = await p.InvokeAsync(
            host.BaseUrl, "Methods", "echo",
            jsonMessages: ["""{"text":"hi"}"""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("hi", result.Response!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_RoundTrips_Positional_Params()
    {
        await using var host = await StartStubAsync(supportsDiscover: true);
        var p = new BowireJsonRpcProtocol();
        p.Initialize(null);

        var result = await p.InvokeAsync(
            host.BaseUrl, "Methods", "add",
            jsonMessages: ["""[2, 3]"""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal("5", result.Response);
    }

    [Fact]
    public async Task InvokeAsync_Server_Error_Envelope_Surfaces_jsonrpc_Prefix()
    {
        await using var host = await StartStubAsync(supportsDiscover: true);
        var p = new BowireJsonRpcProtocol();
        p.Initialize(null);

        var result = await p.InvokeAsync(
            host.BaseUrl, "Methods", "noSuchMethod",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.StartsWith("jsonrpc:", result.Status, StringComparison.Ordinal);
        Assert.Contains("-32601", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Forwards_Metadata_As_Headers()
    {
        await using var host = await StartStubAsync(supportsDiscover: true);
        var p = new BowireJsonRpcProtocol();
        p.Initialize(null);

        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-trace"] = "abc-123",
        };

        // The stub echoes received x-* headers back via a special
        // 'echoHeaders' method; the result payload contains them as a
        // JSON object.
        var result = await p.InvokeAsync(
            host.BaseUrl, "Methods", "echoHeaders",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: meta,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("abc-123", result.Response!, StringComparison.Ordinal);
    }

    // -------- stub host -------------------------------------------------

    private static async Task<JsonRpcStubHost> StartStubAsync(bool supportsDiscover)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = HttpProtocols.Http1);
        });
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapPost("/", async (HttpContext ctx) => await HandleJsonRpcAsync(ctx, supportsDiscover));

        await app.StartAsync(TestContext.Current.CancellationToken);
        return new JsonRpcStubHost(app, ResolveBoundUrl(app));
    }

    private static async Task HandleJsonRpcAsync(HttpContext ctx, bool supportsDiscover)
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        var root = doc.RootElement;
        var method = root.GetProperty("method").GetString() ?? "";
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0;

        async Task WriteAsync(object envelope)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            var json = JsonSerializer.Serialize(envelope);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentLength = bytes.Length;
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        }

        if (method == "rpc.discover")
        {
            if (!supportsDiscover)
            {
                await WriteAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32601, message = "method not found" },
                });
                return;
            }
            await WriteAsync(new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    openrpc = "1.2.6",
                    info = new { title = "stub", version = "1.0" },
                    methods = new object[]
                    {
                        new
                        {
                            name = "echo",
                            @params = new object[]
                            {
                                new { name = "text", required = true, schema = new { type = "string" } },
                            },
                        },
                        new
                        {
                            name = "add",
                            @params = new object[]
                            {
                                new { name = "a", required = true, schema = new { type = "integer" } },
                                new { name = "b", required = true, schema = new { type = "integer" } },
                            },
                        },
                    },
                },
            });
            return;
        }

        if (method == "echo")
        {
            var text = root.TryGetProperty("params", out var p)
                && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : "";
            await WriteAsync(new { jsonrpc = "2.0", id, result = text });
            return;
        }

        if (method == "add")
        {
            if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Array
                && p.GetArrayLength() == 2)
            {
                var sum = p[0].GetInt32() + p[1].GetInt32();
                await WriteAsync(new { jsonrpc = "2.0", id, result = sum });
                return;
            }
            await WriteAsync(new
            {
                jsonrpc = "2.0",
                id,
                error = new { code = -32602, message = "invalid params" },
            });
            return;
        }

        if (method == "echoHeaders")
        {
            var headers = new Dictionary<string, string>();
            foreach (var (name, values) in ctx.Request.Headers)
            {
                if (!name.StartsWith("x-", StringComparison.OrdinalIgnoreCase)) continue;
                headers[name] = values.ToString();
            }
            await WriteAsync(new { jsonrpc = "2.0", id, result = headers });
            return;
        }

        await WriteAsync(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code = -32601, message = $"unknown method '{method}'" },
        });
    }

    private static string ResolveBoundUrl(WebApplication app)
    {
        foreach (var u in app.Urls)
        {
            return u.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)
                    .Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);
        }
        throw new InvalidOperationException("Kestrel didn't publish any bound URL.");
    }

    private sealed class JsonRpcStubHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string BaseUrl { get; }

        public JsonRpcStubHost(WebApplication app, string baseUrl)
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
