// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Mcp;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Coverage for the resources/* + prompts/* handlers that landed on
/// <see cref="McpAdapterServer"/> as part of the "full MCP spec"
/// roadmap item. Mirrors the existing tools/* coverage style: register
/// hand-rolled protocols against the registry, hand the server a
/// JSON-RPC envelope, inspect the response.
/// </summary>
public sealed class McpAdapterServerResourcesPromptsTests
{
    [Fact]
    public async Task Initialize_DeclaresResources_And_Prompts_Capabilities()
    {
        var registry = new BowireProtocolRegistry();
        var server = new McpAdapterServer(registry, "http://localhost");

        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
        });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        var capabilities = response.GetProperty("result").GetProperty("capabilities");

        Assert.True(capabilities.TryGetProperty("tools", out _));
        Assert.True(capabilities.TryGetProperty("resources", out var resources));
        Assert.True(capabilities.TryGetProperty("prompts", out var prompts));
        Assert.False(resources.GetProperty("subscribe").GetBoolean());
        Assert.False(prompts.GetProperty("listChanged").GetBoolean());
    }

    [Fact]
    public async Task ResourcesList_ReturnsOneEntry_PerDiscoveredService()
    {
        var greet = new BowireServiceInfo(
            "Greet",
            "demo",
            new List<BowireMethodInfo>
            {
                new("hello", "Greet/hello",
                    ClientStreaming: false, ServerStreaming: false,
                    InputType: new BowireMessageInfo("HelloIn", "demo.HelloIn", []),
                    OutputType: new BowireMessageInfo("HelloOut", "demo.HelloOut", []),
                    MethodType: "Unary"),
            });
        var weather = new BowireServiceInfo(
            "weather.Weather",
            "weather",
            new List<BowireMethodInfo>
            {
                new("now", "weather.Weather/now",
                    ClientStreaming: false, ServerStreaming: false,
                    InputType: new BowireMessageInfo("In", "weather.In", []),
                    OutputType: new BowireMessageInfo("Out", "weather.Out", []),
                    MethodType: "Unary"),
            });

        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("grpc", "gRPC", greet));
        registry.Register(new StubProtocol("rest", "REST", weather));
        registry.Register(new ThrowingProbe("graphql", "GraphQL"));
        registry.Register(new McpSelfProtocol());

        var server = new McpAdapterServer(registry, "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "resources/list",
        });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        var resources = response.GetProperty("result").GetProperty("resources");

        Assert.Equal(2, resources.GetArrayLength());
        var uris = resources.EnumerateArray()
            .Select(r => r.GetProperty("uri").GetString())
            .ToArray();
        Assert.Contains("bowire-service://grpc/Greet", uris);
        Assert.Contains("bowire-service://rest/weather.Weather", uris);
    }

    [Fact]
    public async Task ResourcesRead_ReturnsSchemaDump_ForKnownService()
    {
        var service = new BowireServiceInfo(
            "Greet",
            "demo",
            new List<BowireMethodInfo>
            {
                new("hello", "Greet/hello",
                    ClientStreaming: false, ServerStreaming: false,
                    InputType: new BowireMessageInfo("HelloIn", "demo.HelloIn", new List<BowireFieldInfo>
                    {
                        new("name", 1, "string", "required", IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null),
                    }),
                    OutputType: new BowireMessageInfo("HelloOut", "demo.HelloOut", new List<BowireFieldInfo>
                    {
                        new("greeting", 1, "string", "required", IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null),
                    }),
                    MethodType: "Unary"),
            });

        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("grpc", "gRPC", service));

        var server = new McpAdapterServer(registry, "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "resources/read",
            @params = new { uri = "bowire-service://grpc/Greet" },
        });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        var contents = response.GetProperty("result").GetProperty("contents");
        Assert.Equal(1, contents.GetArrayLength());

        var text = contents[0].GetProperty("text").GetString();
        Assert.NotNull(text);
        using var payload = JsonDocument.Parse(text!);

        Assert.Equal("grpc", payload.RootElement.GetProperty("plugin").GetString());
        Assert.Equal("Greet", payload.RootElement.GetProperty("service").GetString());
        var methods = payload.RootElement.GetProperty("methods");
        Assert.Equal(1, methods.GetArrayLength());
        Assert.Equal("hello", methods[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("not-a-bowire-service-uri", -32602)]
    [InlineData("bowire-service://", -32602)]
    [InlineData("bowire-service://grpc/UnknownService", -32602)]
    [InlineData("bowire-service://unknownPlugin/Greet", -32602)]
    public async Task ResourcesRead_Errors_On_Malformed_Or_UnknownUri(
        string uri, int expectedCode)
    {
        var service = new BowireServiceInfo(
            "Greet",
            "demo",
            new List<BowireMethodInfo>
            {
                new("hello", "Greet/hello",
                    ClientStreaming: false, ServerStreaming: false,
                    InputType: new BowireMessageInfo("In", "demo.In", []),
                    OutputType: new BowireMessageInfo("Out", "demo.Out", []),
                    MethodType: "Unary"),
            });
        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("grpc", "gRPC", service));

        var server = new McpAdapterServer(registry, "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "resources/read",
            @params = new { uri },
        });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        Assert.True(response.GetProperty("result").TryGetProperty("error", out var err));
        Assert.Equal(expectedCode, err.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ResourcesRead_PropagatesDiscoveryFailure_As_InternalError()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new ThrowingProbe("rest", "REST"));

        var server = new McpAdapterServer(registry, "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 5,
            method = "resources/read",
            @params = new { uri = "bowire-service://rest/whatever" },
        });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        Assert.True(response.GetProperty("result").TryGetProperty("error", out var err));
        Assert.Equal(-32603, err.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task PromptsList_Returns_TwoBuiltInPrompts()
    {
        var registry = new BowireProtocolRegistry();
        var server = new McpAdapterServer(registry, "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 6,
            method = "prompts/list",
        });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        var prompts = response.GetProperty("result").GetProperty("prompts");

        var names = prompts.EnumerateArray()
            .Select(p => p.GetProperty("name").GetString())
            .ToArray();
        Assert.Contains("describe-service", names);
        Assert.Contains("generate-sample-request", names);
    }

    [Fact]
    public async Task PromptsGet_DescribeService_RendersUserMessage_WithService()
    {
        var registry = new BowireProtocolRegistry();
        var server = new McpAdapterServer(registry, "http://localhost");

        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 7,
            method = "prompts/get",
            @params = new
            {
                name = "describe-service",
                arguments = new { service = "weather.WeatherService" },
            },
        });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        var messages = response.GetProperty("result").GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        var text = messages[0].GetProperty("content").GetProperty("text").GetString();
        Assert.Contains("weather.WeatherService", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptsGet_GenerateSampleRequest_RendersMethodInPrompt()
    {
        var registry = new BowireProtocolRegistry();
        var server = new McpAdapterServer(registry, "http://localhost");

        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 8,
            method = "prompts/get",
            @params = new
            {
                name = "generate-sample-request",
                arguments = new { service = "weather.Weather", method = "now" },
            },
        });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        var text = response.GetProperty("result")
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetProperty("text")
            .GetString();
        Assert.Contains("weather.Weather/now", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptsGet_UnknownPromptName_Surfaces_Fallback_Text()
    {
        var registry = new BowireProtocolRegistry();
        var server = new McpAdapterServer(registry, "http://localhost");

        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 9,
            method = "prompts/get",
            @params = new { name = "not-a-real-prompt" },
        });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        var text = response.GetProperty("result")
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetProperty("text")
            .GetString();
        Assert.Contains("not-a-real-prompt", text, StringComparison.Ordinal);
    }

    // -------- helpers --------

    private sealed class StubProtocol(string id, string displayName, BowireServiceInfo service) : IBowireProtocol
    {
        public string Id => id;
        public string Name => displayName;
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo> { service });

        public Task<InvokeResult> InvokeAsync(string serverUrl, string s, string m, List<string> j, bool si,
            Dictionary<string, string>? md = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", new Dictionary<string, string>()));

        public IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string s, string m, List<string> j, bool si,
            Dictionary<string, string>? md = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<string>();

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string s, string m, bool si,
            Dictionary<string, string>? md = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    private sealed class ThrowingProbe(string id, string displayName) : IBowireProtocol
    {
        public string Id => id;
        public string Name => displayName;
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => throw new InvalidOperationException("discover failed: " + id + " " + CultureInfo.InvariantCulture.Name);

        public Task<InvokeResult> InvokeAsync(string serverUrl, string s, string m, List<string> j, bool si,
            Dictionary<string, string>? md = null, CancellationToken ct = default)
            => throw new InvalidOperationException("never called");

        public IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string s, string m, List<string> j, bool si,
            Dictionary<string, string>? md = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<string>();

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string s, string m, bool si,
            Dictionary<string, string>? md = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    private sealed class McpSelfProtocol : IBowireProtocol
    {
        public string Id => "mcp";
        public string Name => "MCP";
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => throw new InvalidOperationException("MCP plugin should never be asked to discover by the adapter.");

        public Task<InvokeResult> InvokeAsync(string serverUrl, string s, string m, List<string> j, bool si,
            Dictionary<string, string>? md = null, CancellationToken ct = default)
            => throw new InvalidOperationException("never called");

        public IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string s, string m, List<string> j, bool si,
            Dictionary<string, string>? md = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<string>();

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string s, string m, bool si,
            Dictionary<string, string>? md = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }
}
