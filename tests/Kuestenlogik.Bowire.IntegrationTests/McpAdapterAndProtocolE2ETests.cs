// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end coverage for both halves of the MCP plugin SDK
/// migration: the adapter
/// (<see cref="BowireMcpAdapterServiceCollectionExtensions.AddBowireMcpAdapter"/>
/// + <see cref="McpAdapterEndpoints.MapBowireMcpAdapter"/>) hosting
/// the official SDK server, and the client
/// (<see cref="BowireMcpProtocol"/> built on
/// <c>ModelContextProtocol.Client.McpClient</c>) talking back to it.
/// </summary>
/// <remarks>
/// <para>
/// Each test spins up a Kestrel host with <c>AddBowireMcpAdapter</c>
/// + a hand-rolled <see cref="FakeProtocol"/> in the
/// <see cref="BowireProtocolRegistry"/>, then drives
/// <see cref="BowireMcpProtocol"/> as a client against the resulting
/// MCP endpoint. The SDK handles initialise + JSON-RPC envelope
/// + Streamable-HTTP transport on both sides; we only test the
/// dynamic-handler glue.
/// </para>
/// </remarks>
[Collection(nameof(RestInvokerEndToEndFixture))]
public sealed class McpAdapterAndProtocolE2ETests
{
    [Fact]
    public async Task DiscoverAsync_Picks_Up_Tools_Resources_And_Prompts()
    {
        await using var host = await StartAdapterHostAsync(SimpleService());
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var services = await protocol.DiscoverAsync(
            host.AdapterUrl,
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        // Three sub-services: Tools, Resources, Prompts.
        Assert.Equal(3, services.Count);
        Assert.Contains(services, s => s.Name == "Tools");
        Assert.Contains(services, s => s.Name == "Resources");
        Assert.Contains(services, s => s.Name == "Prompts");

        var tools = services.Single(s => s.Name == "Tools");
        Assert.Contains(tools.Methods, m => m.Name == "Greet_hello");

        var resources = services.Single(s => s.Name == "Resources");
        Assert.Contains(resources.Methods, m => m.Name == "bowire-service://rest/Greet");

        var prompts = services.Single(s => s.Name == "Prompts");
        Assert.Contains(prompts.Methods, m => m.Name == "describe-service");
        Assert.Contains(prompts.Methods, m => m.Name == "generate-sample-request");
    }

    [Fact]
    public async Task InvokeAsync_Calls_Tool_And_Returns_Response()
    {
        await using var host = await StartAdapterHostAsync(SimpleService("hello back!"));
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var result = await protocol.InvokeAsync(
            host.AdapterUrl,
            service: "Tools",
            method: "Greet_hello",
            jsonMessages: ["""{"name":"world"}"""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("hello back!", result.Response!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Tool_That_Throws_Surfaces_IsError_Result()
    {
        // FakeProtocol with InvokeException set throws inside the
        // adapter's CallTool handler; the SDK wraps that as an
        // IsError result (visible on the client as a non-OK status).
        var fake = SimpleService();
        fake.InvokeException = new InvalidOperationException("plugin boom");
        await using var host = await StartAdapterHostAsync(fake);
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var result = await protocol.InvokeAsync(
            host.AdapterUrl,
            service: "Tools",
            method: "Greet_hello",
            jsonMessages: ["""{"name":"x"}"""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status); // SDK still wraps an error as a 200 result with IsError=true
        Assert.NotNull(result.Response);
        Assert.Contains("plugin boom", result.Response!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Unknown_Tool_Surfaces_Tool_Not_Found_Message()
    {
        // The handler throws McpException("Tool not found: ...") which
        // the SDK wraps as a JSON-RPC error envelope; the client
        // surfaces that on the InvokeResult — either via the Status
        // (when McpClient throws) or via the Response payload (when
        // it returns a CallToolResult with IsError=true). Either way
        // the "Tool not found" string must reach the caller.
        await using var host = await StartAdapterHostAsync(SimpleService());
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var result = await protocol.InvokeAsync(
            host.AdapterUrl,
            service: "Tools",
            method: "DoesNotExist_atAll",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        var surface = $"{result.Status}\n{result.Response}";
        Assert.Contains("Tool not found", surface, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_Reads_Resource_Schema_Dump()
    {
        await using var host = await StartAdapterHostAsync(SimpleService());
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var result = await protocol.InvokeAsync(
            host.AdapterUrl,
            service: "Resources",
            method: "bowire-service://rest/Greet",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("Greet", result.Response!, StringComparison.Ordinal);
        Assert.Contains("hello", result.Response!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Gets_Prompt_With_Arguments_Substituted()
    {
        await using var host = await StartAdapterHostAsync(SimpleService());
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var result = await protocol.InvokeAsync(
            host.AdapterUrl,
            service: "Prompts",
            method: "describe-service",
            jsonMessages: ["""{"service":"weather.WeatherService"}"""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("weather.WeatherService", result.Response!, StringComparison.Ordinal);
    }

    // ----- fixtures ----------------------------------------------------

    private static FakeProtocol SimpleService(string? response = null)
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
                    InputType: new BowireMessageInfo("HelloInput", "demo.HelloInput", new List<BowireFieldInfo>
                    {
                        new("name", 1, "string", "required",
                            IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null),
                    }),
                    OutputType: new BowireMessageInfo("HelloOutput", "demo.HelloOutput", []),
                    MethodType: "Unary"),
            });

        return new FakeProtocol("rest", "REST", greet)
        {
            InvokeResult = new InvokeResult(
                Response: response ?? "\"default\"",
                DurationMs: 0,
                Status: "OK",
                Metadata: new Dictionary<string, string>()),
        };
    }

    private static async Task<AdapterHost> StartAdapterHostAsync(FakeProtocol fake)
    {
        // The adapter's With*Handlers read the protocol registry via
        // BowireEndpointHelpers.GetRegistry(). To inject our fake we
        // also set it on that static seam — the integration test
        // suite is single-threaded inside this collection so the
        // global doesn't race.
        var registry = new BowireProtocolRegistry();
        registry.Register(fake);
        Endpoints.BowireEndpointHelpers.SetRegistry(registry);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = HttpProtocols.Http1AndHttp2);
        });
        builder.Logging.ClearProviders();
        builder.Services.AddBowireMcpAdapter("http://localhost");

        var app = builder.Build();
        app.MapBowireMcpAdapter(prefix: string.Empty);

        await app.StartAsync(TestContext.Current.CancellationToken);

        var baseUrl = ResolveBoundUrl(app);
        return new AdapterHost(app, baseUrl + "/mcp");
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

    private sealed class AdapterHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string AdapterUrl { get; }

        public AdapterHost(WebApplication app, string adapterUrl)
        {
            _app = app;
            AdapterUrl = adapterUrl;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class FakeProtocol(string id, string name, BowireServiceInfo service) : IBowireProtocol
    {
        private readonly BowireServiceInfo _service = service;

        public string Name => name;
        public string Id => id;
        public string IconSvg => "<svg/>";

        public InvokeResult? InvokeResult { get; init; }
        public Exception? InvokeException { get; set; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo> { _service });

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            if (InvokeException is not null) throw InvokeException;
            return Task.FromResult(
                InvokeResult ?? new InvokeResult(null, 0, "OK", new Dictionary<string, string>()));
        }

        public IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<string>();

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }
}
