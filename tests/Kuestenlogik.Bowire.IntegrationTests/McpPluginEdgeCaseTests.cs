// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Edge-case coverage for the MCP plugin. The happy path lives in
/// <see cref="McpAdapterAndProtocolE2ETests"/>; this file targets the
/// error / guard / fallback branches the SDK lifts onto the
/// <c>BowireMcpProtocol</c> client and the
/// <see cref="BowireMcpAdapterServiceCollectionExtensions"/> adapter
/// handlers — non-Unary skip, mcp self-skip, malformed resource URIs,
/// unknown plugin / service / prompt, discovery-throws inside a
/// handler, and the value-kind branches in <c>ParseArguments</c> /
/// <c>BuildInputSchema</c>.
/// </summary>
[Collection(nameof(RestInvokerEndToEndFixture))]
public sealed class McpPluginEdgeCaseTests
{
    // ----- BowireMcpProtocol: trivial / guard branches ----------------

    [Fact]
    public void Initialize_With_Null_Provider_Does_Not_Throw()
    {
        // Plugin contract says Initialize is a no-op for MCP because the
        // SDK owns its own HttpClient. Just guard the seam against an
        // accidental NRE on the null path the host actually uses.
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);
    }

    [Fact]
    public async Task DiscoverAsync_With_Empty_ServerUrl_Returns_Empty()
    {
        var protocol = new BowireMcpProtocol();
        var result = await protocol.DiscoverAsync(
            "",
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task DiscoverAsync_With_Whitespace_ServerUrl_Returns_Empty()
    {
        var protocol = new BowireMcpProtocol();
        var result = await protocol.DiscoverAsync(
            "   ",
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task DiscoverAsync_With_Unreachable_ServerUrl_Returns_Empty()
    {
        // Bound port that nobody listens on — the SDK's initialize POST
        // fails, the CreateClientAsync catch swallows it, DiscoverAsync
        // returns []. Covers the catch-and-empty branch.
        var protocol = new BowireMcpProtocol();
        var result = await protocol.DiscoverAsync(
            "http://127.0.0.1:1/mcp",
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task InvokeAsync_With_Unsupported_Service_Surfaces_Error()
    {
        // Hosts a real adapter so CreateClientAsync succeeds, then asks
        // for "NoSuchService" — the switch falls through to the
        // InvalidOperationException branch, which surfaces as the
        // InvokeResult.Status (the InvokeAsync catch turns ex.Message
        // into the status field).
        await using var host = await StartAdapterHostAsync(MakeFakeProtocol());
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var result = await protocol.InvokeAsync(
            host.AdapterUrl,
            service: "NoSuchService",
            method: "whatever",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
        Assert.Contains("not supported", result.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_With_Unreachable_ServerUrl_Returns_Error_Status()
    {
        // Bound port that nobody listens on — the CreateClientAsync
        // throw lands in the outer catch and turns into the status
        // string. Exercises the "client is null, throw before
        // assignment" path.
        var protocol = new BowireMcpProtocol();
        var result = await protocol.InvokeAsync(
            "http://127.0.0.1:1/mcp",
            service: "Tools",
            method: "whatever",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task OpenChannelAsync_Returns_Null()
    {
        // MCP is request/response only — channel surface is
        // intentionally null. Documents the contract; covers the line.
        var protocol = new BowireMcpProtocol();
        var ch = await protocol.OpenChannelAsync(
            "http://localhost:1",
            service: "Tools",
            method: "x",
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);
        Assert.Null(ch);
    }

    [Fact]
    public async Task InvokeStreamAsync_Yields_Empty_Sequence()
    {
        // Streaming surface is an empty IAsyncEnumerable until the SDK
        // exposes the notification seam.
        var protocol = new BowireMcpProtocol();
        var emitted = 0;
        await foreach (var _ in protocol.InvokeStreamAsync(
            "http://localhost:1",
            service: "Tools",
            method: "x",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken))
        {
            emitted++;
        }
        Assert.Equal(0, emitted);
    }

    [Fact]
    public async Task InvokeAsync_Parses_Numeric_And_Boolean_Tool_Arguments()
    {
        // Drives the JsonValueKind.Number + True/False/Null branches in
        // ParseArguments. The FakeProtocol echoes the received JSON
        // body, so we can assert the args round-tripped through the
        // SDK's tool-call serialiser unchanged.
        var fake = MakeFakeProtocol(echo: true);
        await using var host = await StartAdapterHostAsync(fake);
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var result = await protocol.InvokeAsync(
            host.AdapterUrl,
            service: "Tools",
            method: "Greet_hello",
            jsonMessages: ["""{"count":7,"ratio":1.5,"on":true,"off":false,"x":null}"""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        // FakeProtocol with echo=true stuffs the inbound jsonBody into
        // the response, so each arg-name must show up there.
        Assert.Contains("count", result.Response!, StringComparison.Ordinal);
        Assert.Contains("ratio", result.Response!, StringComparison.Ordinal);
        Assert.Contains("on", result.Response!, StringComparison.Ordinal);
    }

    // ----- Adapter (server side): error + skip branches ---------------

    [Fact]
    public async Task Adapter_Skips_Mcp_Self_And_Throwing_Protocols_For_List_Tools()
    {
        // Two extra protocols live alongside the real "rest" one:
        //   - SelfMcpProtocol with Id="mcp" — must be skipped to avoid
        //     re-entering ourselves.
        //   - ThrowingProtocol whose DiscoverAsync throws — handler must
        //     swallow and continue (the test elsewhere confirms results
        //     still flow).
        // Plus a non-Unary method on the real protocol that must drop
        // from the tools list.
        var real = MakeFakeProtocol(includeStreaming: true);
        var registry = new BowireProtocolRegistry();
        registry.Register(new SelfMcpProtocol());
        registry.Register(new ThrowingProtocol("throws"));
        registry.Register(real);
        Endpoints.BowireEndpointHelpers.SetRegistry(registry);

        await using var host = await StartHostWithRegistryAsync();
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var services = await protocol.DiscoverAsync(
            host.AdapterUrl,
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        var tools = services.SingleOrDefault(s => s.Name == "Tools");
        Assert.NotNull(tools);
        // Only the Unary method on the real protocol survives.
        Assert.Single(tools!.Methods);
        Assert.Equal("Greet_hello", tools.Methods[0].Name);
    }

    [Fact]
    public async Task Adapter_ReadResource_With_Unsupported_Scheme_Returns_Error()
    {
        await using var host = await StartAdapterHostAsync(MakeFakeProtocol());
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var result = await protocol.InvokeAsync(
            host.AdapterUrl,
            service: "Resources",
            method: "http://example.com/not-bowire",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
    }

    [Fact]
    public async Task Adapter_ReadResource_With_Malformed_Uri_Returns_Error()
    {
        await using var host = await StartAdapterHostAsync(MakeFakeProtocol());
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        // No slash after the scheme — IndexOf returns -1 → malformed.
        var result = await protocol.InvokeAsync(
            host.AdapterUrl,
            service: "Resources",
            method: "bowire-service://justplugin",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
    }

    [Fact]
    public async Task Adapter_ReadResource_With_Unknown_Plugin_Returns_Error()
    {
        await using var host = await StartAdapterHostAsync(MakeFakeProtocol());
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var result = await protocol.InvokeAsync(
            host.AdapterUrl,
            service: "Resources",
            method: "bowire-service://nope/Greet",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
    }

    [Fact]
    public async Task Adapter_ReadResource_With_Unknown_Service_Returns_Error()
    {
        await using var host = await StartAdapterHostAsync(MakeFakeProtocol());
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var result = await protocol.InvokeAsync(
            host.AdapterUrl,
            service: "Resources",
            method: "bowire-service://rest/NoSuchService",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
    }

    [Fact]
    public async Task Adapter_ReadResource_When_Discovery_Throws_Returns_Error()
    {
        // ThrowingProtocol gets registered alone so the resource path's
        // discovery call wraps the exception in "Discovery failed for".
        var registry = new BowireProtocolRegistry();
        registry.Register(new ThrowingProtocol("throws"));
        Endpoints.BowireEndpointHelpers.SetRegistry(registry);

        await using var host = await StartHostWithRegistryAsync();
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var result = await protocol.InvokeAsync(
            host.AdapterUrl,
            service: "Resources",
            method: "bowire-service://throws/AnyService",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
    }

    [Fact]
    public async Task Adapter_GetPrompt_With_Unknown_Name_Returns_Unknown_Marker()
    {
        // The Prompts service only advertises describe-service and
        // generate-sample-request via ListPrompts, but the SDK still
        // forwards unknown names to the GetPrompt handler. The handler
        // falls into its "Unknown prompt: ..." branch which we want to
        // observe through the client. McpException carries the message
        // back through the JSON-RPC envelope.
        await using var host = await StartAdapterHostAsync(MakeFakeProtocol());
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var result = await protocol.InvokeAsync(
            host.AdapterUrl,
            service: "Prompts",
            method: "no-such-prompt",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        var surface = $"{result.Status}\n{result.Response}";
        // Either the SDK error wrapping or the placeholder text from the
        // handler must contain "Unknown".
        Assert.Contains("Unknown", surface, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Adapter_GetPrompt_Renders_Generate_Sample_Request()
    {
        // Exercises the second branch in the prompt-name switch.
        await using var host = await StartAdapterHostAsync(MakeFakeProtocol());
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var result = await protocol.InvokeAsync(
            host.AdapterUrl,
            service: "Prompts",
            method: "generate-sample-request",
            jsonMessages: ["""{"service":"demo.S","method":"do"}"""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("demo.S", result.Response!, StringComparison.Ordinal);
        Assert.Contains("do", result.Response!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adapter_ListTools_Schema_Covers_Optional_And_Repeated_And_TypeMap()
    {
        // Drives BuildInputSchema's optional / repeated branches and the
        // type-mapping switch for several non-string proto types. The
        // FakeProtocol below has fields of every type we care about.
        var fake = MakeProtocolWithRichSchema();
        await using var host = await StartAdapterHostAsync(fake);
        var protocol = new BowireMcpProtocol();
        protocol.Initialize(serviceProvider: null);

        var services = await protocol.DiscoverAsync(
            host.AdapterUrl,
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        var tools = services.SingleOrDefault(s => s.Name == "Tools");
        Assert.NotNull(tools);
        var method = tools!.Methods.Single(m => m.Name == "Greet_hello");
        // The adapter's BuildInputSchema runs the proto-type → JSON-Schema-
        // type switch (int32 → integer, double → number, bool → boolean,
        // bytes → string, message → object); the client's
        // MapToolInputSchema reads those back. This covers both the
        // adapter-side mapping and the client-side schema walker for
        // every non-trivial branch.
        var fields = method.InputType?.Fields;
        Assert.NotNull(fields);
        Assert.Contains(fields!, f => f.Name == "count" && f.Type == "integer");
        Assert.Contains(fields!, f => f.Name == "ratio" && f.Type == "number");
        Assert.Contains(fields!, f => f.Name == "on" && f.Type == "boolean");
        Assert.Contains(fields!, f => f.Name == "kind" && f.Type == "object");
        // Optional field gets surfaced (vs required) — covers the
        // adapter's required-array-skip branch.
        Assert.Contains(fields!, f => f.Name == "name" && !f.Required);
    }

    // ----- fixtures ----------------------------------------------------

    private static FakeProtocol MakeFakeProtocol(bool echo = false, bool includeStreaming = false)
    {
        var methods = new List<BowireMethodInfo>
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
        };
        if (includeStreaming)
        {
            methods.Add(new BowireMethodInfo(
                Name: "stream",
                FullName: "demo.Greet/stream",
                ClientStreaming: false,
                ServerStreaming: true,
                InputType: new BowireMessageInfo("StreamIn", "demo.StreamIn", []),
                OutputType: new BowireMessageInfo("StreamOut", "demo.StreamOut", []),
                MethodType: "ServerStreaming"));
        }

        var greet = new BowireServiceInfo("Greet", "demo", methods);
        var fake = new FakeProtocol("rest", "REST", greet);
        fake.EchoBody = echo;
        return fake;
    }

    private static FakeProtocol MakeProtocolWithRichSchema()
    {
        var fields = new List<BowireFieldInfo>
        {
            new("name", 1, "string", "optional",
                IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null),
            new("tags", 2, "string", "repeated",
                IsMap: false, IsRepeated: true, MessageType: null, EnumValues: null),
            new("count", 3, "int32", "required",
                IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null),
            new("ratio", 4, "double", "required",
                IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null),
            new("on", 5, "bool", "required",
                IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null),
            new("blob", 6, "bytes", "optional",
                IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null),
            new("kind", 7, "message", "optional",
                IsMap: false, IsRepeated: false,
                MessageType: new BowireMessageInfo("Kind", "demo.Kind", []),
                EnumValues: null),
        };
        var method = new BowireMethodInfo(
            Name: "hello",
            FullName: "demo.Greet/hello",
            ClientStreaming: false,
            ServerStreaming: false,
            InputType: new BowireMessageInfo("HelloInput", "demo.HelloInput", fields),
            OutputType: new BowireMessageInfo("HelloOutput", "demo.HelloOutput", []),
            MethodType: "Unary");
        var greet = new BowireServiceInfo("Greet", "demo", new List<BowireMethodInfo> { method });
        return new FakeProtocol("rest", "REST", greet);
    }

    private static async Task<AdapterHost> StartAdapterHostAsync(FakeProtocol fake)
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(fake);
        Endpoints.BowireEndpointHelpers.SetRegistry(registry);
        return await StartHostWithRegistryAsync();
    }

    private static async Task<AdapterHost> StartHostWithRegistryAsync()
    {
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

        string baseUrl = "http://localhost";
        foreach (var u in app.Urls)
        {
            baseUrl = u.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)
                        .Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);
            break;
        }
        return new AdapterHost(app, baseUrl + "/mcp");
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
            // Drop the fake registry we injected so later readers (e.g.
            // BowireEndpointTests' /protocols) rediscover the real set.
            Endpoints.BowireEndpointHelpers.ResetRegistry();
        }
    }

    private sealed class FakeProtocol(string id, string name, BowireServiceInfo service) : IBowireProtocol
    {
        private readonly BowireServiceInfo _service = service;

        public string Name => name;
        public string Id => id;
        public string IconSvg => "<svg/>";

        // When set, the InvokeAsync result echoes back whatever was
        // received in jsonMessages[0]. Lets us assert on the
        // ParseArguments → CallToolAsync round-trip.
        public bool EchoBody { get; set; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo> { _service });

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            var body = jsonMessages.Count > 0 ? jsonMessages[0] : "{}";
            var response = EchoBody ? body : "\"default\"";
            return Task.FromResult(new InvokeResult(
                Response: response,
                DurationMs: 0,
                Status: "OK",
                Metadata: new Dictionary<string, string>()));
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

    private sealed class SelfMcpProtocol : IBowireProtocol
    {
        public string Name => "MCP";
        public string Id => "mcp";
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>
            {
                new("ShouldBeSkipped", "mcp",
                    new List<BowireMethodInfo>
                    {
                        new(
                            Name: "selfRef",
                            FullName: "mcp.Self/selfRef",
                            ClientStreaming: false,
                            ServerStreaming: false,
                            InputType: new BowireMessageInfo("In", "mcp.In", []),
                            OutputType: new BowireMessageInfo("Out", "mcp.Out", []),
                            MethodType: "Unary"),
                    }),
            });

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new InvalidOperationException("MCP self-protocol should never be invoked.");

        public IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<string>();

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    private sealed class ThrowingProtocol(string id) : IBowireProtocol
    {
        public string Name => "Throws";
        public string Id => id;
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => throw new InvalidOperationException("discovery boom");

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new InvalidOperationException("invoke boom");

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
