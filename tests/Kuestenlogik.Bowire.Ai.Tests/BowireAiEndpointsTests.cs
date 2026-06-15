// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Ai.Tests;

/// <summary>
/// In-process integration tests for the routes wired by
/// <see cref="BowireAiEndpoints.MapBowireAiEndpoints"/>:
/// <list type="bullet">
///   <item>route registration shape — every documented endpoint
///     answers under the supplied basePath;</item>
///   <item><c>GET /api/ai/status</c> reflects the registered runtime
///     options + hostManaged flag;</item>
///   <item><c>GET /api/ai/status?workspaceId=X</c> reflects the
///     workspace-override resolution layer;</item>
///   <item><c>POST /api/ai/config</c> validates the endpoint URL and
///     persists the pick through <see cref="BowireAiUserConfigStore"/>;</item>
///   <item><c>POST /api/ai/config</c> rejects invalid JSON and
///     non-http(s) endpoints with structured errors;</item>
///   <item><c>DELETE /api/ai/config</c> drops the per-workspace
///     override and falls back to the global resolved options;</item>
///   <item><c>POST /api/ai/chat</c> returns the documented
///     RFC 7807 ProblemDetails shape on the no-client / invalid-body /
///     missing-messages paths;</item>
///   <item><c>GET /api/ai/probe-local</c> short-circuits when
///     AutoDetectLocal is off.</item>
/// </list>
/// </summary>
[Collection("BowireUserContext")]
public sealed class BowireAiEndpointsTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _tempRoot;

    public BowireAiEndpointsTests()
    {
        _originalStore = BowireUserContext.Current;
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"bowire-ai-endpoints-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        BowireUserContext.Current = new TempUserStore(_tempRoot);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _originalStore;
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MapBowireAiEndpoints_ReturnsTheBuilder_ForChaining()
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddSingleton(new BowireAiOptions());
        services.AddSingleton<BowireAiRuntime>(sp => new BowireAiRuntime(sp.GetRequiredService<BowireAiOptions>()));

        using var sp = services.BuildServiceProvider();
        var builder = new TestEndpointRouteBuilder(sp);

        var result = builder.MapBowireAiEndpoints(basePath: "/bowire");

        Assert.Same(builder, result);
        Assert.NotEmpty(builder.DataSources);
    }

    [Fact]
    public void MapBowireAiEndpoints_RegistersDocumentedRoutes_UnderBasePath()
    {
        // Pins the public route surface: probe-local, status, config
        // (GET/POST/DELETE), chat, triage, threat-model, template-suggest,
        // template-save, fuzz-values. Surfacing a list lets a future
        // route rename break the test loudly instead of dead-routing a
        // running deployment.
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddSingleton(new BowireAiOptions());
        services.AddSingleton<BowireAiRuntime>(sp => new BowireAiRuntime(sp.GetRequiredService<BowireAiOptions>()));

        using var sp = services.BuildServiceProvider();
        var builder = new TestEndpointRouteBuilder(sp);
        builder.MapBowireAiEndpoints(basePath: "/bowire");

        var patterns = builder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("/bowire/api/ai/probe-local", patterns);
        Assert.Contains("/bowire/api/ai/status", patterns);
        Assert.Contains("/bowire/api/ai/config", patterns);
        Assert.Contains("/bowire/api/ai/chat", patterns);
        Assert.Contains("/bowire/api/ai/triage", patterns);
        Assert.Contains("/bowire/api/ai/threat-model", patterns);
        Assert.Contains("/bowire/api/ai/template-suggest", patterns);
        Assert.Contains("/bowire/api/ai/template-save", patterns);
        Assert.Contains("/bowire/api/ai/fuzz-values", patterns);
    }

    [Fact]
    public void MapBowireAiEndpoints_RespectsCustomBasePath()
    {
        // Embedded hosts mount at "/bowire" by default but operator
        // hosts can pick anything. Pin the substitution so a non-default
        // mount point really reaches the per-feature routes.
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddSingleton(new BowireAiOptions());
        services.AddSingleton<BowireAiRuntime>(sp => new BowireAiRuntime(sp.GetRequiredService<BowireAiOptions>()));

        using var sp = services.BuildServiceProvider();
        var builder = new TestEndpointRouteBuilder(sp);
        builder.MapBowireAiEndpoints(basePath: "/custom-prefix");

        var patterns = builder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .ToList();

        Assert.Contains(patterns, p => p == "/custom-prefix/api/ai/status");
        Assert.Contains(patterns, p => p == "/custom-prefix/api/ai/chat");
        Assert.DoesNotContain(patterns, p => p.StartsWith("/bowire/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_Reflects_Runtime_Options_And_HostManaged_False()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions
            {
                ProviderId = "ollama",
                Endpoint = "http://localhost:11434",
                Model = "qwen2.5:7b",
                AutoDetectLocal = true,
            });
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.GetAsync(
            new Uri("/api/ai/status", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await ReadJsonAsync(resp);
        Assert.Equal("ollama", body.GetProperty("providerId").GetString());
        Assert.Equal("qwen2.5:7b", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("autoDetectLocal").GetBoolean());
        Assert.False(body.GetProperty("hostManaged").GetBoolean());
        Assert.False(body.GetProperty("hasOverride").GetBoolean());
        Assert.True(body.GetProperty("hasClient").GetBoolean()); // ollama → MutableChatClient over a real OllamaSharp client
    }

    [Fact]
    public async Task Status_HostManaged_TrueWhenHostRegistersOwnChatClient()
    {
        // Embedded host preempts AddBowireAi's TryAdd on IChatClient,
        // so the status payload's hostManaged flag should flip.
        using var hostClient = new NoOpChatClient();
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(hostClient);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.GetAsync(
            new Uri("/api/ai/status", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);

        Assert.True(body.GetProperty("hostManaged").GetBoolean());
        Assert.True(body.GetProperty("hasClient").GetBoolean());
    }

    [Fact]
    public async Task Status_WithWorkspaceId_ReflectsWorkspaceOverride()
    {
        // Save a per-workspace override; calling /status?workspaceId=X
        // should report hasOverride=true and surface the override's
        // model rather than the global default.
        BowireAiUserConfigStore.Save(
            new BowireAiOptions
            {
                ProviderId = "ollama",
                Endpoint = "http://localhost:11434",
                Model = "ws-pick:7b",
                AutoDetectLocal = false,
            },
            workspaceId: "personal");

        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions { Model = "global-default:1b" });
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.GetAsync(
            new Uri("/api/ai/status?workspaceId=personal", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);

        Assert.Equal("ws-pick:7b", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("hasOverride").GetBoolean());
        Assert.Equal("personal", body.GetProperty("workspaceId").GetString());
    }

    [Fact]
    public async Task ProbeLocal_AutoDetectDisabled_Skipped()
    {
        // Privacy stance: when AutoDetectLocal=false, the probe must
        // not actually fire (which would touch 127.0.0.1:11434 and
        // :1234). Endpoint returns a "skipped" sentinel.
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions { AutoDetectLocal = false });
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.GetAsync(
            new Uri("/api/ai/probe-local", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);

        Assert.Equal("auto-detect disabled", body.GetProperty("skipped").GetString());
    }

    [Fact]
    public async Task PostConfig_PersistsThroughUserStore_AndUpdatesRuntime()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/config",
            new
            {
                providerId = "ollama",
                endpoint = "http://localhost:11434",
                model = "saved-pick:7b",
                autoDetectLocal = false,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.True(body.GetProperty("saved").GetBoolean());
        Assert.Equal("saved-pick:7b", body.GetProperty("model").GetString());

        // The user-config file landed in the temp store.
        Assert.True(File.Exists(Path.Combine(_tempRoot, "ai-config.json")));
        var persisted = BowireAiUserConfigStore.TryLoad();
        Assert.NotNull(persisted);
        Assert.Equal("saved-pick:7b", persisted!.Model);
    }

    [Fact]
    public async Task PostConfig_WithWorkspaceId_WritesOverrideFile()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/config?workspaceId=personal",
            new
            {
                providerId = "ollama",
                endpoint = "http://localhost:11434",
                model = "ws-pick:7b",
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Per-workspace file landed; global stayed empty.
        Assert.True(File.Exists(Path.Combine(_tempRoot, "ai-config.personal.json")));
        Assert.False(File.Exists(Path.Combine(_tempRoot, "ai-config.json")));
    }

    [Fact]
    public async Task PostConfig_RejectsNonHttpEndpoint_With400()
    {
        // file:// / javascript: / about: schemes must be rejected with a
        // clear 400 so the user sees the config mistake instead of an
        // opaque OllamaApiClient construction failure later.
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/config",
            new { endpoint = "file:///etc/passwd" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Contains("http", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostConfig_RejectsRelativeUrl_With400()
    {
        // Endpoint must be absolute — relative paths would silently
        // pin to whatever base URI OllamaSharp picks up.
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/config",
            new { endpoint = "/api/local" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostConfig_PartialBody_FillsFromCurrentRuntimeOptions()
    {
        // Settings UI saves a partial patch — fields absent from the
        // body should fall back to the runtime's current options
        // rather than reset to BowireAiOptions defaults.
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions
            {
                ProviderId = "ollama",
                Endpoint = "http://localhost:11434",
                Model = "before-patch:1b",
                AutoDetectLocal = true,
            });
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/config",
            new { model = "after-patch:7b" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("ollama", body.GetProperty("providerId").GetString());
        Assert.Equal("http://localhost:11434", body.GetProperty("endpoint").GetString());
        Assert.Equal("after-patch:7b", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("autoDetectLocal").GetBoolean());
    }

    [Fact]
    public async Task PostConfig_InvalidJsonBody_Returns400()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/config")
        {
            Content = new StringContent("{not valid json", System.Text.Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Contains("JSON", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteConfig_WithoutWorkspaceId_Returns400()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.DeleteAsync(
            new Uri("/api/ai/config", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteConfig_RemovesOverride_AndRevertsToGlobal()
    {
        // Setup: a global config + a workspace override, both saved.
        // Delete the override → status?workspaceId=X should now
        // reflect the global values.
        BowireAiUserConfigStore.Save(new BowireAiOptions
        {
            ProviderId = "ollama",
            Endpoint = "http://localhost:11434",
            Model = "global-after-revert:1b",
            AutoDetectLocal = true,
        });
        BowireAiUserConfigStore.Save(
            new BowireAiOptions { Model = "ws-pick:7b" },
            workspaceId: "personal");

        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.DeleteAsync(
            new Uri("/api/ai/config?workspaceId=personal", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await ReadJsonAsync(resp);
        Assert.True(body.GetProperty("deleted").GetBoolean());
        Assert.Equal("global-after-revert:1b", body.GetProperty("model").GetString());

        // The override file is gone.
        Assert.False(File.Exists(Path.Combine(_tempRoot, "ai-config.personal.json")));
    }

    // ----- /api/ai/chat — ProblemDetails error paths ---------------

    [Fact]
    public async Task Chat_NoClientRegistered_Returns503_ProblemDetails()
    {
        // The Settings-UI flow surfaces "no chat client" as RFC 7807
        // problem+json with a stable type URN the frontend special-cases.
        using var host = BuildHostWithoutChatClient();
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/chat",
            new { messages = new[] { new { role = "user", content = "hi" } } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await ReadJsonAsync(resp);
        Assert.Equal("urn:bowire:ai:no-chat-client", body.GetProperty("type").GetString());
        Assert.Equal(503, body.GetProperty("status").GetInt32());
        Assert.Equal("/api/ai/chat", body.GetProperty("instance").GetString());
        // The frontend uses the links array to surface a "Configure"
        // affordance pointing at the AI settings tab.
        Assert.True(body.TryGetProperty("links", out _));
    }

    [Fact]
    public async Task Chat_InvalidJson_Returns400_ProblemDetails()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(new StubChatClient("unused"));
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/chat")
        {
            Content = new StringContent("{not valid", System.Text.Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await ReadJsonAsync(resp);
        Assert.Equal("urn:bowire:invalid-input", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Chat_MissingMessagesArray_Returns400_ProblemDetails()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(new StubChatClient("unused"));
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/chat",
            new { messages = Array.Empty<object>() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("urn:bowire:invalid-input", body.GetProperty("type").GetString());
        Assert.Contains("messages", body.GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Chat_HappyPath_ReturnsContent_FromStubClient()
    {
        // Pin the happy-path response shape: { content, finishReason,
        // modelId, toolCalls? } — the frontend reads these field
        // names verbatim.
        using var stub = new StubChatClient("hello from the model");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/chat",
            new { messages = new[] { new { role = "user", content = "ping" } } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("hello from the model", body.GetProperty("content").GetString());
    }

    // ----- helpers --------------------------------------------------

    private static IHost BuildHost(Action<IServiceCollection> register)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireAiEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       register(s);
                   });
            })
            .Start();
    }

    private static IHost BuildHostWithoutChatClient()
    {
        // Skip AddBowireAi so no MutableChatClient lands as the
        // IChatClient. The chat endpoint's [FromServices] IChatClient?
        // parameter then resolves to null, exercising the 503 path.
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireAiEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       s.AddSingleton(new BowireAiOptions());
                       s.AddSingleton<BowireAiRuntime>(sp => new BowireAiRuntime(sp.GetRequiredService<BowireAiOptions>()));
                   });
            })
            .Start();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private sealed class TempUserStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }

    private sealed class StubChatClient(string responseText) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NoOpChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Minimal <see cref="IEndpointRouteBuilder"/> for the route-shape
    /// assertions — we don't need a full HTTP pipeline to verify what
    /// patterns MapBowireAiEndpoints registered.
    /// </summary>
    private sealed class TestEndpointRouteBuilder(IServiceProvider sp) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = sp;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }
}
