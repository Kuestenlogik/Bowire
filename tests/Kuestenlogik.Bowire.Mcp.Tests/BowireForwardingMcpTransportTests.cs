// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Testing;
using System.ComponentModel;
using System.Net.Sockets;
using Kuestenlogik.Bowire.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Mcp.Tests;

/// <summary>
/// End-to-end coverage for the #286 MCP-over-MCP forwarder. Each test
/// spins up a real parent WebApplication, optionally wraps it in a
/// forwarder-child, and drives the topology with the SDK's
/// <see cref="McpClient"/> so the assertions hit every layer (transport,
/// JSON-RPC framing, handler dispatch, parent execution, response relay).
/// </summary>
public sealed class BowireForwardingMcpTransportTests
{
    // ---- helpers ----------------------------------------------------


    private sealed record ParentHandle(WebApplication App, Uri McpEndpoint, int Port) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<ParentHandle> StartParentAsync(string? token = null, CancellationToken ct = default)
    {
        ForwardingTestCountingTools.Reset();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // Not the default 5000/5001, which every other developer on the box
        // is also using. Port 0 and read back what it got, rather than a
        // number found a moment earlier that anything could have taken since.
        builder.WebHost.UseUrls(LoopbackHost.AnyPort);

        builder.Services
            .AddBowireMcp(o => { o.LoadAllowlistFromEnvironments = false; })
            .WithHttpTransport(o => o.Stateless = true)
            // Override the default tool registration with our stub set;
            // the real BowireMcpTools surface needs the protocol registry
            // assembly-scan which is too heavy + slow for tests + the
            // forwarder doesn't care what's on the other side.
            .WithTools<ForwardingTestCountingTools>();

        var app = builder.Build();

        if (!string.IsNullOrEmpty(token))
        {
            var expected = $"Bearer {token}";
            app.Use(async (HttpContext httpCtx, RequestDelegate next) =>
            {
                if (httpCtx.Request.Path.StartsWithSegments("/bowire/mcp"))
                {
                    var supplied = (string?)httpCtx.Request.Headers.Authorization;
                    if (!string.Equals(supplied, expected, StringComparison.Ordinal))
                    {
                        httpCtx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await httpCtx.Response.WriteAsync("Unauthorized", httpCtx.RequestAborted);
                        return;
                    }
                }
                await next(httpCtx);
            });
        }

        app.MapMcp("/bowire/mcp");
        await app.StartAsync(ct);
        var port = LoopbackHost.Port(app.Services);
        return new ParentHandle(app, new Uri($"http://127.0.0.1:{port}/bowire/mcp"), port);
    }

    /// <summary>
    /// A parent mounted the way `bowire mcp serve --bind http` mounts it:
    /// through <see cref="BowireMcpEndpointRouteBuilderExtensions.MapBowireMcp"/>
    /// at <c>/mcp</c>, because the standalone CLI puts the workbench at "/"
    /// and its MCP endpoint carries no /bowire/ prefix. The other helper
    /// mounts an embedded-shaped parent at /bowire/mcp with the SDK's own
    /// MapMcp, which is a different route and a different code path.
    /// </summary>
    private static async Task<ParentHandle> StartCliShapedParentAsync(CancellationToken ct = default)
    {
        ForwardingTestCountingTools.Reset();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(LoopbackHost.AnyPort);

        builder.Services
            .AddBowireMcp(o => { o.LoadAllowlistFromEnvironments = false; })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<ForwardingTestCountingTools>();

        var app = builder.Build();
        app.MapBowireMcp("/mcp");
        await app.StartAsync(ct);
        var port = LoopbackHost.Port(app.Services);
        return new ParentHandle(app, new Uri($"http://127.0.0.1:{port}/mcp"), port);
    }

    private sealed record ChildHandle(WebApplication App, Uri McpEndpoint) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<ChildHandle> StartForwarderChildAsync(
        Uri parentEndpoint, string? attachToken = null, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(LoopbackHost.AnyPort);

        builder.Services
            .AddBowireMcpForwarder(parentEndpoint, attachToken)
            .WithHttpTransport(o => o.Stateless = true);

        var app = builder.Build();
        app.MapMcp("/bowire/mcp");
        await app.StartAsync(ct);
        return new ChildHandle(
            app, new Uri($"{LoopbackHost.BaseAddress(app.Services)}/bowire/mcp"));
    }

    /// <summary>
    /// A forwarder child wired exactly as <c>bowire mcp serve --bind http
    /// --attach …</c> wires it: <see cref="BowireMcpServiceCollectionExtensions.AddBowireMcpForwarder"/>
    /// plus <c>MapBowireMcp("/mcp")</c>. The other child helper mounts with
    /// the SDK's <c>MapMcp</c>, which asks the container for nothing — which
    /// is why it could not see #731.
    /// </summary>
    private static async Task<ChildHandle> StartCliShapedForwarderChildAsync(
        Uri parentEndpoint, string? attachToken = null, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(LoopbackHost.AnyPort);

        builder.Services
            .AddBowireMcpForwarder(parentEndpoint, attachToken)
            .WithHttpTransport(o => o.Stateless = true);

        var app = builder.Build();
        app.MapBowireMcp("/mcp");
        await app.StartAsync(ct);
        return new ChildHandle(app, new Uri($"{LoopbackHost.BaseAddress(app.Services)}/mcp"));
    }

    private static async Task<McpClient> ConnectClientAsync(Uri endpoint, CancellationToken ct)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.AutoDetect,
        };
#pragma warning disable CA2000
        var transport = new HttpClientTransport(options);
#pragma warning restore CA2000
        try
        {
            return await McpClient.CreateAsync(transport, cancellationToken: ct);
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    // ---- unit tests on the transport itself --------------------------

    [Fact]
    public void Ctor_Rejects_NonHttp_Endpoint()
    {
        Assert.Throws<ArgumentException>(() =>
            new BowireForwardingMcpTransport(new Uri("ftp://example.com/")));
    }

    [Fact]
    public void Ctor_Rejects_Null_Endpoint() =>
        Assert.Throws<ArgumentNullException>(() => new BowireForwardingMcpTransport(null!));

    [Fact]
    public async Task ParentEndpoint_Roundtrips()
    {
        await using var t = new BowireForwardingMcpTransport(
            new Uri("http://localhost:1234/bowire/mcp"), "secret");
        Assert.Equal("http://localhost:1234/bowire/mcp", t.ParentEndpoint.ToString());
        Assert.True(t.HasBearerToken);
    }

    [Fact]
    public async Task GetClientAsync_Throws_When_Parent_Unreachable()
    {
        // Use a port we *know* nothing is listening on.
        var deadPort = LoopbackHost.ClosedPort();
        await using var t = new BowireForwardingMcpTransport(
            new Uri($"http://localhost:{deadPort}/bowire/mcp"));

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await t.GetClientAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var t = new BowireForwardingMcpTransport(new Uri("http://localhost:1/"));
        await t.DisposeAsync();
        await t.DisposeAsync(); // second call must not throw.
    }

    // ---- end-to-end: parent + child + stub LLM client ----------------

    [Fact]
    public async Task EndToEnd_ToolCall_From_Child_Reaches_Parent_And_Returns_Result()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var parent = await StartParentAsync(ct: ct);
        await using var child = await StartForwarderChildAsync(parent.McpEndpoint, ct: ct);

        // The "LLM client" — points at the child, not the parent.
        await using var client = await ConnectClientAsync(child.McpEndpoint, ct);

        // ListTools should forward + return the parent's tool surface.
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        Assert.Contains(tools, t => t.Name == "test.echo");

        // CallTool should forward + return the parent's result.
        var result = await client.CallToolAsync(
            "test.echo",
            new Dictionary<string, object?> { ["message"] = "hello" },
            cancellationToken: ct);

        // IsError is bool? — null / false both mean success. We assert
        // the positive shape (non-error content) rather than pinning the
        // exact null-vs-false convention the SDK happens to use.
        Assert.NotEqual(true, result.IsError);
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.Equal("echo:hello", text);
        Assert.Equal(1, ForwardingTestCountingTools.EchoCalls);
        Assert.Equal("hello", ForwardingTestCountingTools.LastEchoMessage);
    }

    [Fact]
    public async Task EndToEnd_Parent_Unreachable_Surfaces_Clean_McpError_To_Child_Caller()
    {
        var ct = TestContext.Current.CancellationToken;
        // Aim the child at a port no parent is listening on.
        var deadPort = LoopbackHost.ClosedPort();
        var phantomParent = new Uri($"http://localhost:{deadPort}/bowire/mcp");
        await using var child = await StartForwarderChildAsync(phantomParent, ct: ct);

        await using var client = await ConnectClientAsync(child.McpEndpoint, ct);

        // Either the connect to the parent fails, or the tools/list call
        // does — either way the child must surface an MCP-level error
        // rather than leaking a raw transport exception.
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.ListToolsAsync(cancellationToken: ct));

        // Must be either an McpException or a JSON-RPC error wrapped into
        // an exception — the key contract is "no leak of the raw HTTP /
        // socket error to the LLM caller".
        Assert.True(
            ex is McpException
            || ex.GetType().FullName?.Contains("Mcp", StringComparison.OrdinalIgnoreCase) == true
            || ex.Message.Contains("forward", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("parent", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("connect", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("refused", StringComparison.OrdinalIgnoreCase),
            $"Unexpected exception type/message: {ex.GetType().FullName} — {ex.Message}");
    }

    [Fact]
    public async Task EndToEnd_Bearer_Token_Mismatch_Bubbles_Up_To_Child_Caller()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var parent = await StartParentAsync(token: "correct-secret", ct: ct);
        await using var child = await StartForwarderChildAsync(
            parent.McpEndpoint, attachToken: "WRONG-SECRET", ct: ct);

        await using var client = await ConnectClientAsync(child.McpEndpoint, ct);

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.ListToolsAsync(cancellationToken: ct));

        // The HTTP 401 comes back through the forwarder; the LLM caller
        // sees *some* failure. We don't pin the exact error class —
        // SDK versions differ — but the error must trace back to the
        // parent's auth rejection.
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task EndToEnd_Correct_Bearer_Token_Passes_Through()
    {
        var ct = TestContext.Current.CancellationToken;
        const string secret = "shared-bearer-deadbeef";
        await using var parent = await StartParentAsync(token: secret, ct: ct);
        await using var child = await StartForwarderChildAsync(
            parent.McpEndpoint, attachToken: secret, ct: ct);

        await using var client = await ConnectClientAsync(child.McpEndpoint, ct);

        var tools = await client.ListToolsAsync(cancellationToken: ct);
        Assert.Contains(tools, t => t.Name == "test.echo");

        var result = await client.CallToolAsync(
            "test.echo",
            new Dictionary<string, object?> { ["message"] = "authed" },
            cancellationToken: ct);
        Assert.Equal("echo:authed", result.Content.OfType<TextContentBlock>().First().Text);
    }

    [Fact]
    public async Task EndToEnd_Child_Shutdown_Closes_Parent_Connection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var parent = await StartParentAsync(ct: ct);

        // Resolve the forwarder transport directly so we can check
        // ParentEndpoint round-trip + observe disposal.
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(LoopbackHost.AnyPort);
        builder.Services
            .AddBowireMcpForwarder(parent.McpEndpoint)
            .WithHttpTransport(o => o.Stateless = true);
        var app = builder.Build();
        app.MapMcp("/bowire/mcp");
        await app.StartAsync(ct);
        var port = LoopbackHost.Port(app.Services);

        var forwarder = app.Services.GetRequiredService<BowireForwardingMcpTransport>();
        Assert.Equal(parent.McpEndpoint, forwarder.ParentEndpoint);

        // Drive at least one request through so the lazy parent
        // connection is actually established.
        await using (var client = await ConnectClientAsync(new Uri($"http://127.0.0.1:{port}/bowire/mcp"), ct))
        {
            _ = await client.ListToolsAsync(cancellationToken: ct);
        }

        // Stop the child + dispose the host; the forwarder singleton's
        // DisposeAsync must close the parent client without throwing.
        await app.StopAsync(ct);
        await app.DisposeAsync();

        // Second dispose on the transport must be a no-op — proves the
        // close is idempotent regardless of host shutdown ordering.
        await forwarder.DisposeAsync();
    }

    // ---- the combination the CLI actually runs (#730, #731) ----------
    //
    // Both flags were covered on their own and both worked on their own:
    // stdio + --attach never maps an endpoint, and --bind http without
    // --attach goes through AddBowireMcp, which registers the endpoint
    // registry. Together they did not start at all. The helpers above
    // mount with the SDK's MapMcp, which asks the container for nothing,
    // so no test in this file could have seen it.

    [Fact]
    public async Task Forwarder_Over_Http_Starts_When_Mounted_The_Way_The_Cli_Mounts_It()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var parent = await StartCliShapedParentAsync(ct);

        // #731: this line threw "No service for type
        // 'Kuestenlogik.Bowire.Mcp.BowireMcpEndpointRegistry' has been
        // registered" before AddBowireMcpForwarder registered it, so
        // `bowire mcp serve --bind http --attach …` died on startup.
        await using var child = await StartCliShapedForwarderChildAsync(parent.McpEndpoint, ct: ct);

        await using var client = await ConnectClientAsync(child.McpEndpoint, ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        Assert.Contains(tools, t => t.Name == "test.echo");

        var result = await client.CallToolAsync(
            "test.echo",
            new Dictionary<string, object?> { ["message"] = "combined" },
            cancellationToken: ct);
        Assert.Equal("echo:combined", result.Content.OfType<TextContentBlock>().First().Text);
        // The call ran on the parent, not on the child: the child registers
        // no tools of its own.
        Assert.Equal(1, ForwardingTestCountingTools.EchoCalls);
    }

    [Fact]
    public async Task Forwarder_Over_Http_Also_Serves_The_Mcp_Manifest()
    {
        // MapBowireMcp mounts the manifest alongside the server, and the
        // registry is what records the entry. A forwarder that registered
        // the type but never reached Register would still answer tools;
        // the manifest is where the mount is visible.
        var ct = TestContext.Current.CancellationToken;
        await using var parent = await StartCliShapedParentAsync(ct);
        await using var child = await StartCliShapedForwarderChildAsync(parent.McpEndpoint, ct: ct);

        using var http = new HttpClient();
        var manifest = await http.GetStringAsync(
            new Uri($"{LoopbackHost.BaseAddress(child.App.Services)}{BowireMcpEndpointRouteBuilderExtensions.ManifestPath}"),
            ct);

        Assert.Contains("\"/mcp\"", manifest, StringComparison.Ordinal);
        Assert.Contains("server", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_Attach_Shorthand_Reaches_A_Parent_Started_By_The_Cli()
    {
        // #730: the documented shorthand `--attach host:port` expanded to
        // /bowire/mcp while the CLI's own HTTP bind listens on /mcp, so the
        // advertised form pointed at a 404. Asserted against a running
        // parent rather than against the expansion string, because the
        // InlineData test passed just as happily with the wrong path.
        var ct = TestContext.Current.CancellationToken;
        await using var parent = await StartCliShapedParentAsync(ct);

        var ok = BowireForwardingMcpTransport.TryParseAttachEndpoint(
            $"127.0.0.1:{parent.Port}", out var endpoint, out var error);
        Assert.True(ok, error);

        await using var child = await StartCliShapedForwarderChildAsync(endpoint!, ct: ct);
        await using var client = await ConnectClientAsync(child.McpEndpoint, ct);

        var tools = await client.ListToolsAsync(cancellationToken: ct);
        Assert.Contains(tools, t => t.Name == "test.echo");
    }

    // ---- attach-endpoint parsing -------------------------------------
    // (Exercises the CLI helper through the public surface.)

    // The shorthand expands to /mcp, which is where `bowire mcp serve --bind
    // http` listens — the standalone CLI mounts the workbench at "/", so its
    // MCP endpoint carries no /bowire/ prefix. It used to expand to
    // /bowire/mcp, and this test pinned that: measured against one running
    // parent, /mcp answered 200 and /bowire/mcp answered 404, so the only
    // advertised way to attach was the broken one.
    //
    // The absolute forms below are how an embedded parent is reached; those
    // do mount under /bowire/ and must keep passing through untouched.
    [Theory]
    [InlineData("localhost:5198", "http://localhost:5198/mcp")]
    [InlineData("127.0.0.1:5081", "http://127.0.0.1:5081/mcp")]
    [InlineData("http://parent.local:6000/bowire/mcp", "http://parent.local:6000/bowire/mcp")]
    [InlineData("https://parent.example.com/bowire/mcp", "https://parent.example.com/bowire/mcp")]
    [InlineData("http://parent.local:6000/mcp", "http://parent.local:6000/mcp")]
    public void TryParseAttachEndpoint_Accepts_Documented_Forms(string raw, string expected)
    {
        var ok = BowireForwardingMcpTransport.TryParseAttachEndpoint(raw, out var uri, out _);
        Assert.True(ok);
        Assert.Equal(expected, uri!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://parent/")]
    [InlineData("not-a-host")]
    public void TryParseAttachEndpoint_Rejects_Malformed(string raw)
    {
        var ok = BowireForwardingMcpTransport.TryParseAttachEndpoint(raw, out var uri, out var err);
        Assert.False(ok);
        Assert.Null(uri);
        Assert.NotEmpty(err);
    }
}

/// <summary>
/// Stub MCP tool surface bound to the parent test host. Tracks every
/// call so the forwarder tests can prove the request landed here (not
/// on the child). Lives at file scope so the SDK's
/// <c>WithTools&lt;T&gt;()</c> can resolve it via
/// <c>typeof(ForwardingTestCountingTools)</c> the same way it resolves
/// <see cref="BowireMcpTools"/>.
/// </summary>
[McpServerToolType]
internal sealed class ForwardingTestCountingTools
{
    private static int s_echoCalls;
    private static string? s_lastEchoMessage;

    public static int EchoCalls => s_echoCalls;
    public static string? LastEchoMessage => s_lastEchoMessage;

    public static void Reset()
    {
        Interlocked.Exchange(ref s_echoCalls, 0);
        s_lastEchoMessage = null;
    }

    [McpServerTool(Name = "test.echo")]
    [Description("Echoes the supplied message verbatim. Used by the forwarder tests to prove the call reached the parent.")]
    public static string Echo(
        [Description("Message to echo back.")] string message)
    {
        Interlocked.Increment(ref s_echoCalls);
        s_lastEchoMessage = message;
        return $"echo:{message}";
    }
}
