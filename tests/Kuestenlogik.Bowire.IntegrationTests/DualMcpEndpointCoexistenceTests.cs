// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Net;
using Kuestenlogik.Bowire.Mcp;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end coverage for the dual-mount MCP topology shipped by
/// issue #287: <c>MapBowireMcp</c> (full server, exposes the workbench
/// tools) and <c>MapBowireMcpAdapter</c> (forwards discovered upstream
/// services as MCP tools) hosted side-by-side on a single
/// <see cref="WebApplication"/>.
/// </summary>
/// <remarks>
/// <para>
/// The fixture mounts both endpoints on the same in-process Kestrel
/// host and drives a <see cref="BowireMcpProtocol"/> MCP client
/// against each URL. The key assertion is independence — the
/// server endpoint's <c>tools/list</c> returns the static
/// <c>TestServerTools.ping</c> tool registered via
/// <c>WithTools&lt;TestServerTools&gt;</c>, the adapter endpoint
/// returns the dynamic tool list synthesised from the upstream
/// <see cref="FakeAdapterProtocol"/>, and tools invoked on one URL
/// never reach the other side's handler chain.
/// </para>
/// <para>
/// Lives in <c>IntegrationTests</c> rather than
/// <c>Kuestenlogik.Bowire.Mcp.Tests</c> because it needs a Kestrel
/// host bound to a loopback port (the streamable-HTTP transport
/// cannot run on <c>TestServer</c> without sockets).
/// </para>
/// </remarks>
[Collection(nameof(RestInvokerEndToEndFixture))]
public sealed class DualMcpEndpointCoexistenceTests
{
    [Fact]
    public async Task Both_Endpoints_Listed_In_Manifest_With_Correct_Modes()
    {
        await using var host = await StartDualHostAsync();
        using var http = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var manifest = await http.GetFromJsonAsync<ManifestResponse>(
            BowireMcpEndpointRouteBuilderExtensions.ManifestPath,
            TestContext.Current.CancellationToken);

        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.Endpoints);
        Assert.Equal(2, manifest.Endpoints!.Length);

        // Stable ordinal-by-path sort means the server entry comes
        // first ("/bowire/mcp" < "/bowire/mcp/adapter" ordinally).
        Assert.Equal("/bowire/mcp", manifest.Endpoints[0].Path);
        Assert.Equal("server", manifest.Endpoints[0].Mode);
        Assert.Equal("/bowire/mcp/adapter", manifest.Endpoints[1].Path);
        Assert.Equal("adapter", manifest.Endpoints[1].Mode);
    }

    [Fact]
    public async Task Server_Endpoint_Lists_Only_Static_Tools_Not_Adapter_Tools()
    {
        await using var host = await StartDualHostAsync();

        var serverClient = new BowireMcpProtocol();
        serverClient.Initialize(serviceProvider: null);
        var services = await serverClient.DiscoverAsync(
            host.ServerUrl,
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        // BowireMcpProtocol groups the SDK's tools list under a single
        // "Tools" pseudo-service. The full-server endpoint must only
        // expose the static tool we registered via WithTools<T>; the
        // adapter's dynamic-tool synth (Greet_hello) must NOT leak
        // here, otherwise the dual-mount routing is broken.
        var tools = services.SingleOrDefault(s => s.Name == "Tools");
        Assert.NotNull(tools);
        var names = tools!.Methods.Select(m => m.Name).ToList();
        Assert.Contains("ping", names);
        Assert.DoesNotContain("Greet_hello", names);
    }

    [Fact]
    public async Task Adapter_Endpoint_Lists_Only_Adapter_Tools_Not_Static_Tools()
    {
        await using var host = await StartDualHostAsync();

        var adapterClient = new BowireMcpProtocol();
        adapterClient.Initialize(serviceProvider: null);
        var services = await adapterClient.DiscoverAsync(
            host.AdapterUrl,
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        var tools = services.SingleOrDefault(s => s.Name == "Tools");
        Assert.NotNull(tools);
        var names = tools!.Methods.Select(m => m.Name).ToList();
        // Adapter synthesises one tool per (service, unary method) of
        // the upstream registry — our FakeAdapterProtocol exposes
        // demo.Greet/hello, which serialises to "Greet_hello".
        Assert.Contains("Greet_hello", names);
        // The static-tool path must NOT bleed into the adapter list.
        Assert.DoesNotContain("ping", names);
    }

    [Fact]
    public async Task Server_Tool_Call_Reaches_Static_Handler_Not_Adapter()
    {
        await using var host = await StartDualHostAsync();
        var client = new BowireMcpProtocol();
        client.Initialize(serviceProvider: null);

        var result = await client.InvokeAsync(
            host.ServerUrl,
            service: "Tools",
            method: "ping",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        // The static tool stamps a known sentinel string into its
        // response — proving the server's tool/call traveled the
        // static path, not the adapter's dynamic-tool synth.
        Assert.Contains("pong-from-static-tool", result.Response!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adapter_Tool_Call_Reaches_Adapter_Handler_Not_Static()
    {
        await using var host = await StartDualHostAsync();
        var client = new BowireMcpProtocol();
        client.Initialize(serviceProvider: null);

        var result = await client.InvokeAsync(
            host.AdapterUrl,
            service: "Tools",
            method: "Greet_hello",
            jsonMessages: ["""{"name":"world"}"""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        // FakeAdapterProtocol's InvokeAsync returns a sentinel that
        // confirms the call traveled the adapter's dynamic handler
        // (which calls IBowireProtocol.InvokeAsync on the upstream).
        Assert.Contains("hello-from-adapter-fake", result.Response!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlapping_Prefixes_Throw_At_Startup()
    {
        // Mount the server at /bowire/mcp and try to mount the adapter
        // at the same prefix. The registry's overlap check should
        // throw an InvalidOperationException with both routes named.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.Listen(IPAddress.Loopback, 0);
        });
        builder.Logging.ClearProviders();
        builder.Services.AddBowireMcp().WithHttpTransport(o => o.Stateless = true);
        builder.Services.AddBowireMcpAdapter(opts => opts.UpstreamServerUrl = "http://localhost");

        var app = builder.Build();
        app.MapBowireMcp("/bowire/mcp");

        var ex = Assert.Throws<InvalidOperationException>(
            () => app.MapBowireMcpAdapter("/bowire/mcp"));
        Assert.Contains("/bowire/mcp", ex.Message, StringComparison.Ordinal);
        await app.DisposeAsync();
    }

    [Fact]
    public void Adapter_Options_Are_Independent_Of_Server_Options()
    {
        // The adapter and the server each own their own IOptions<T>
        // block; tweaking one must not affect the other. Verifies the
        // DI shape — independent option-block resolution is part of
        // the #287 acceptance criteria.
        var services = new ServiceCollection();
        services.AddBowireMcp(opts => opts.MaxSubscribeMs = 12345);
        services.AddBowireMcpAdapter(opts =>
        {
            opts.UpstreamServerUrl = "http://adapter.example";
            opts.RequestTimeout = TimeSpan.FromSeconds(9);
        });
        using var sp = services.BuildServiceProvider();

        var serverOpts = sp.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<BowireMcpOptions>>().Value;
        var adapterOpts = sp.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<BowireMcpAdapterOptions>>().Value;

        Assert.Equal(12345, serverOpts.MaxSubscribeMs);
        // Server option block doesn't carry adapter-only knobs.
        Assert.False(serverOpts.AllowArbitraryUrls);

        Assert.Equal("http://adapter.example", adapterOpts.UpstreamServerUrl);
        Assert.Equal(TimeSpan.FromSeconds(9), adapterOpts.RequestTimeout);

        // Different IOptions<> entries — the types are completely
        // distinct so changing MaxSubscribeMs leaves UpstreamServerUrl
        // untouched and vice versa.
        Assert.NotSame((object)serverOpts, adapterOpts);
    }

    // ----- fixture -----------------------------------------------------

    private static async Task<DualHost> StartDualHostAsync()
    {
        // The adapter's dynamic-tool path reads the BowireProtocolRegistry
        // via BowireEndpointHelpers.GetRegistry() — same static seam the
        // existing McpAdapterAndProtocolE2ETests fixture uses. Set up
        // exactly one FakeAdapterProtocol so the adapter's tools/list
        // returns Greet_hello.
        var registry = new BowireProtocolRegistry();
        registry.Register(new FakeAdapterProtocol());
        Kuestenlogik.Bowire.Endpoints.BowireEndpointHelpers.SetRegistry(registry);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.Listen(IPAddress.Loopback, 0, lo =>
                lo.Protocols = HttpProtocols.Http1AndHttp2);
        });
        builder.Logging.ClearProviders();

        // Full server: tools registered via WithTools<T>.
        builder.Services
            .AddBowireMcp()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<TestServerTools>();

        // Adapter: dynamic handlers + the FakeAdapterProtocol upstream.
        builder.Services.AddBowireMcpAdapter(opts =>
        {
            opts.UpstreamServerUrl = "http://localhost";
        });

        var app = builder.Build();
        // Defaults pin the server at /bowire/mcp and the adapter at
        // /bowire/mcp/adapter — the #287 convention.
        app.MapBowireMcp();
        app.MapBowireMcpAdapter();

        await app.StartAsync(TestContext.Current.CancellationToken);
        var baseUrl = ResolveBoundUrl(app);

        return new DualHost(app, baseUrl);
    }

    private static string ResolveBoundUrl(WebApplication app)
    {
        foreach (var u in app.Urls)
        {
            return u
                .Replace("[::]", "127.0.0.1", StringComparison.Ordinal)
                .Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);
        }
        throw new InvalidOperationException("Kestrel didn't publish any bound URL.");
    }

    private sealed class DualHost(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl { get; } = baseUrl;
        public string ServerUrl { get; } = baseUrl + "/bowire/mcp";
        public string AdapterUrl { get; } = baseUrl + "/bowire/mcp/adapter";

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            // Reset the static registry seam so neighbouring tests in
            // the collection (BowireEndpointTests, &c) rediscover the
            // real protocol set.
            Kuestenlogik.Bowire.Endpoints.BowireEndpointHelpers.ResetRegistry();
        }
    }

    // ----- test-only tool surface (server side) ------------------------

    /// <summary>
    /// Minimal MCP tool surface registered via <c>WithTools&lt;T&gt;</c>
    /// to verify the static-tool path. Returns a sentinel string the
    /// assertions key off so we know the call actually reached the
    /// statically-registered handler (and not the adapter delegate
    /// that would have synthesised an empty response).
    /// </summary>
    [McpServerToolType]
    internal sealed class TestServerTools
    {
        [McpServerTool(Name = "ping")]
        [Description("Returns a sentinel string to prove the static-tool path was hit.")]
        public static string Ping() => "pong-from-static-tool";
    }

    // ----- test-only upstream protocol (adapter side) ------------------

    /// <summary>
    /// Stub <see cref="IBowireProtocol"/> that the dual-mount adapter
    /// discovers + invokes. Stamps a sentinel into the
    /// <see cref="InvokeResult.Response"/> so the assertions can
    /// distinguish "call reached the adapter" from "call reached
    /// the static-tool path that happens to share the name".
    /// </summary>
    private sealed class FakeAdapterProtocol : IBowireProtocol
    {
        public string Name => "REST";
        public string Id => "rest";
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
        {
            var greet = new BowireServiceInfo(
                "Greet", "demo",
                new List<BowireMethodInfo>
                {
                    new(
                        Name: "hello",
                        FullName: "demo.Greet/hello",
                        ClientStreaming: false,
                        ServerStreaming: false,
                        InputType: new BowireMessageInfo("HelloInput", "demo.HelloInput",
                            new List<BowireFieldInfo>
                            {
                                new("name", 1, "string", "required",
                                    IsMap: false, IsRepeated: false,
                                    MessageType: null, EnumValues: null),
                            }),
                        OutputType: new BowireMessageInfo("HelloOutput", "demo.HelloOutput", []),
                        MethodType: "Unary"),
                });
            return Task.FromResult(new List<BowireServiceInfo> { greet });
        }

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(
                Response: "\"hello-from-adapter-fake\"",
                DurationMs: 0,
                Status: "OK",
                Metadata: new Dictionary<string, string>()));

        public IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<string>();

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    // ----- manifest DTO ------------------------------------------------

    private sealed class ManifestResponse
    {
        public ManifestEntry[]? Endpoints { get; set; }
    }

    private sealed class ManifestEntry
    {
        public string Path { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
    }
}
