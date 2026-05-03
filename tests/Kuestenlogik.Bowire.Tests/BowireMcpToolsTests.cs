// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Mcp;
using Kuestenlogik.Bowire.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for <see cref="BowireMcpTools"/>. Each test injects a hand-rolled
/// <see cref="BowireProtocolRegistry"/> with one or zero stub plugins so the
/// allowlist + protocol-resolution branches can be exercised without a live
/// MCP transport or HTTP server.
/// </summary>
public class BowireMcpToolsTests : IAsyncDisposable
{
    private readonly List<BowireMockHandleRegistry> _registries = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _registries)
        {
            await r.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }

    private BowireMcpTools BuildTools(
        BowireProtocolRegistry? registry = null,
        BowireMcpOptions? options = null,
        BowireMockHandleRegistry? mockHandles = null)
    {
        registry ??= new BowireProtocolRegistry();
        // LoadAllowlistFromEnvironments must default to false in tests so the
        // ctor doesn't read the user's actual ~/.bowire/environments.json.
        options ??= new BowireMcpOptions { LoadAllowlistFromEnvironments = false };
        if (mockHandles is null)
        {
            mockHandles = new BowireMockHandleRegistry();
            _registries.Add(mockHandles);
        }

        return new BowireMcpTools(
            registry,
            mockHandles,
            Options.Create(options),
            NullLogger<BowireMcpTools>.Instance);
    }

    [Fact]
    public void AllowlistShow_Returns_JSON_With_Current_Configuration()
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            AllowArbitraryUrls = true,
        };
        options.AllowedServerUrls.Add("https://api.example.com");

        var tools = BuildTools(options: options);

        var json = tools.AllowlistShow();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("allowArbitraryUrls").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("loadFromEnvironments").GetBoolean());
        Assert.Equal("https://api.example.com",
            doc.RootElement.GetProperty("urls")[0].GetString());
    }

    [Fact]
    public void MockList_Empty_Registry_Returns_Zero_Count()
    {
        var tools = BuildTools();

        var json = tools.MockList();
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("mocks").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("mocks").GetArrayLength());
    }

    [Fact]
    public async Task MockStop_Unknown_Handle_Returns_Stopped_False()
    {
        var tools = BuildTools();

        var json = await tools.MockStop("not-a-real-handle");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("not-a-real-handle", doc.RootElement.GetProperty("handle").GetString());
        Assert.False(doc.RootElement.GetProperty("stopped").GetBoolean());
    }

    [Fact]
    public async Task MockStart_No_Sources_Returns_Validation_Error()
    {
        var tools = BuildTools();

        var result = await tools.MockStart(
            recording: null, schema: null, grpcSchema: null, graphqlSchema: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Contains("provide exactly one of", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MockStart_Multiple_Sources_Returns_Mutually_Exclusive_Error()
    {
        var tools = BuildTools();

        var result = await tools.MockStart(
            recording: "rec.json", schema: "api.yaml",
            ct: TestContext.Current.CancellationToken);

        Assert.Contains("mutually exclusive", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_Url_Not_On_Allowlist_Is_Denied()
    {
        var tools = BuildTools(); // empty allowlist, AllowArbitraryUrls=false

        var result = await tools.Discover(
            "https://malicious.example.com",
            protocol: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Contains("not on the allowlist", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_With_AllowArbitraryUrls_And_No_Plugins_Reports_No_Match()
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            AllowArbitraryUrls = true,
        };
        var tools = BuildTools(options: options);

        var result = await tools.Discover(
            "http://localhost:5000",
            protocol: "nope",
            ct: TestContext.Current.CancellationToken);

        Assert.Contains("No matching protocol plugin", result, StringComparison.Ordinal);
        Assert.Contains("nope", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_With_Stub_Plugin_Returns_Service_List()
    {
        var registry = new BowireProtocolRegistry();
        var stub = new StubProtocol("stub", "Stub");
        registry.Register(stub);

        var options = new BowireMcpOptions { LoadAllowlistFromEnvironments = false };
        options.AllowedServerUrls.Add("http://localhost:5000");

        var tools = BuildTools(registry: registry, options: options);

        var result = await tools.Discover(
            "http://localhost:5000",
            protocol: "stub",
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("http://localhost:5000",
            doc.RootElement.GetProperty("url").GetString());
        var services = doc.RootElement.GetProperty("services");
        Assert.Equal(1, services.GetArrayLength());
        Assert.Equal("stub", services[0].GetProperty("protocol").GetString());
        Assert.Equal("StubService", services[0].GetProperty("service").GetString());
    }

    [Fact]
    public async Task Discover_Allowlist_PathPrefix_Match_Is_Allowed()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("stub", "Stub"));

        var options = new BowireMcpOptions { LoadAllowlistFromEnvironments = false };
        options.AllowedServerUrls.Add("https://api.example.com/v1");

        var tools = BuildTools(registry: registry, options: options);

        // "https://api.example.com/v1/users" starts with the allowlisted base
        var result = await tools.Discover(
            "https://api.example.com/v1/users",
            ct: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("not on the allowlist", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_Url_Not_On_Allowlist_Is_Denied()
    {
        var tools = BuildTools();

        var result = await tools.Invoke(
            "https://blocked.example.com",
            "Service",
            "Method",
            ct: TestContext.Current.CancellationToken);

        Assert.Contains("not on the allowlist", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_No_Plugin_Loaded_Reports_No_Match()
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            AllowArbitraryUrls = true,
        };
        var tools = BuildTools(options: options);

        var result = await tools.Invoke(
            "http://localhost:5000",
            "Service",
            "Method",
            protocol: "missing",
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("No protocol plugin matched.", result);
    }

    [Fact]
    public async Task Invoke_With_Stub_Plugin_Returns_Echoed_Response()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("stub", "Stub"));

        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            AllowArbitraryUrls = true,
        };
        var tools = BuildTools(registry: registry, options: options);

        var result = await tools.Invoke(
            "http://localhost:5000",
            "StubService",
            "Echo",
            body: "{\"hello\":\"world\"}",
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("stub", doc.RootElement.GetProperty("protocol").GetString());
        Assert.Equal("StubService", doc.RootElement.GetProperty("service").GetString());
        Assert.Equal("Echo", doc.RootElement.GetProperty("method").GetString());
        Assert.Equal("OK", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Subscribe_Url_Not_On_Allowlist_Is_Denied()
    {
        var tools = BuildTools();

        var result = await tools.Subscribe(
            "https://blocked.example.com",
            "Service",
            "Method",
            ct: TestContext.Current.CancellationToken);

        Assert.Contains("not on the allowlist", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subscribe_No_Plugin_Loaded_Reports_No_Match()
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            AllowArbitraryUrls = true,
        };
        var tools = BuildTools(options: options);

        var result = await tools.Subscribe(
            "http://localhost:5000",
            "Service",
            "Method",
            protocol: "missing",
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("No protocol plugin matched.", result);
    }

    [Fact]
    public async Task Subscribe_With_Stub_Plugin_Collects_Frames_Within_Window()
    {
        var registry = new BowireProtocolRegistry();
        var stub = new StubProtocol("stub", "Stub")
        {
            StreamFrames = ["frame-1", "frame-2", "frame-3"],
        };
        registry.Register(stub);

        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            AllowArbitraryUrls = true,
            DefaultSubscribeMs = 1_000,
            MaxSubscribeMs = 2_000,
            MaxSubscribeFrames = 200,
        };
        var tools = BuildTools(registry: registry, options: options);

        var result = await tools.Subscribe(
            "http://localhost:5000",
            "StubService",
            "Stream",
            durationMs: 500,
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(3, doc.RootElement.GetProperty("frameCount").GetInt32());
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(500, doc.RootElement.GetProperty("durationMs").GetInt32());
    }

    [Fact]
    public async Task Subscribe_Truncates_When_Frames_Exceed_Cap()
    {
        var registry = new BowireProtocolRegistry();
        var stub = new StubProtocol("stub", "Stub")
        {
            StreamFrames = ["a", "b", "c", "d", "e"],
        };
        registry.Register(stub);

        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            AllowArbitraryUrls = true,
            DefaultSubscribeMs = 1_000,
            MaxSubscribeMs = 2_000,
            MaxSubscribeFrames = 2,
        };
        var tools = BuildTools(registry: registry, options: options);

        var result = await tools.Subscribe(
            "http://localhost:5000",
            "StubService",
            "Stream",
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(2, doc.RootElement.GetProperty("frameCount").GetInt32());
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Subscribe_DurationMs_Clamped_To_MaxSubscribeMs()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("stub", "Stub"));

        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            AllowArbitraryUrls = true,
            MaxSubscribeMs = 250,
        };
        var tools = BuildTools(registry: registry, options: options);

        var result = await tools.Subscribe(
            "http://localhost:5000",
            "StubService",
            "Stream",
            durationMs: 10_000, // way above cap
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(250, doc.RootElement.GetProperty("durationMs").GetInt32());
    }

    [Fact]
    public void RecordStart_Returns_Not_Implemented_Sentinel()
    {
        var result = BowireMcpTools.RecordStart("my-recording");
        Assert.Contains("not yet implemented", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordStop_Returns_Not_Implemented_Sentinel()
    {
        var result = BowireMcpTools.RecordStop();
        Assert.Contains("not yet implemented", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordReplay_Returns_Not_Implemented_Sentinel()
    {
        var result = BowireMcpTools.RecordReplay("rec_42");
        Assert.Contains("not yet implemented", result, StringComparison.Ordinal);
        Assert.Contains("rec_42", result, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvList_Returns_Path_Even_When_File_Missing()
    {
        // The static EnvList reads from ~/.bowire/environments.json. We can't
        // assume the user's box has it or doesn't — just verify the response
        // is parseable JSON with the expected shape.
        var result = BowireMcpTools.EnvList();

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("path", out _));
        Assert.True(doc.RootElement.TryGetProperty("environments", out _));
    }

    [Fact]
    public void RecordList_Returns_Path_Even_When_File_Missing()
    {
        var result = BowireMcpTools.RecordList();

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("path", out _));
        Assert.True(doc.RootElement.TryGetProperty("recordings", out _));
    }

    // -------------------- Test stub --------------------

    /// <summary>
    /// In-memory <see cref="IBowireProtocol"/> for the allowlist + plugin-
    /// dispatch tests. Returns a single canned service / method on
    /// discovery, echoes a hard-coded "OK" on invoke, and yields a fixed
    /// list of stream frames on InvokeStreamAsync.
    /// </summary>
    private sealed class StubProtocol : IBowireProtocol
    {
        public StubProtocol(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }
        public string IconSvg => "<svg/>";

        public List<string> StreamFrames { get; init; } = [];

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
        {
            var msg = new BowireMessageInfo("Empty", "stub.Empty", []);
            var method = new BowireMethodInfo(
                Name: "Echo",
                FullName: "stub.StubService/Echo",
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: msg,
                OutputType: msg,
                MethodType: "Unary");
            var svc = new BowireServiceInfo("StubService", "stub", [method]);
            return Task.FromResult(new List<BowireServiceInfo> { svc });
        }

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            return Task.FromResult(new InvokeResult(
                Response: "{\"ok\":true}",
                DurationMs: 1,
                Status: "OK",
                Metadata: new Dictionary<string, string>()));
        }

        public async IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var frame in StreamFrames)
            {
                ct.ThrowIfCancellationRequested();
                yield return frame;
                await Task.Yield();
            }
        }

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }
}
