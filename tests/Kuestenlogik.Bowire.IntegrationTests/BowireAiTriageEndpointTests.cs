// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using Kuestenlogik.Bowire.Ai;
using Kuestenlogik.Bowire.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// In-process tests for <c>POST /api/ai/triage</c> (#61). Drives the
/// endpoint via a TestServer with a stub <see cref="IChatClient"/> that
/// returns canned JSON, so we cover the verdict parser + happy path +
/// no-client-503 path + cap-on-evidence prompt without standing up a
/// real model.
/// </summary>
public sealed class BowireAiTriageEndpointTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _tempRoot;

    public BowireAiTriageEndpointTests()
    {
        _originalStore = BowireUserContext.Current;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"bowire-triage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        BowireUserContext.Current = new TempStore(_tempRoot);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _originalStore;
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Triage_Returns_503_When_No_IChatClient_Registered()
    {
        // Standalone-style host: AddBowireAi registers MutableChatClient
        // but the runtime's inner client is null for unknown providers,
        // so MutableChatClient.GetResponseAsync throws. Easier path:
        // register a null IChatClient explicitly so the endpoint takes
        // its short-circuit.
        using var host = BuildHostWithoutClient();
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/triage",
            new { title = "test finding" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task Triage_Returns_400_For_Missing_Title()
    {
        using var host = BuildHostWithStub(("""{"realScore":80,"reasoning":"x","fix":"y"}"""));
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/triage",
            new { category = "auth-bypass" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Triage_Parses_Clean_Json_Verdict()
    {
        using var host = BuildHostWithStub((
            """{"realScore":85,"reasoning":"Response leaks the admin flag","fix":"Add ABAC check before serializing"}"""));
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/triage",
            new
            {
                title = "auth-bypass on /api/orders/{id}",
                category = "auth-bypass",
                evidence = "{\"admin\":true}",
                endpoint = "GET /api/orders/123",
                protocol = "rest",
            },
            TestContext.Current.CancellationToken);

        var bodyText = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, "expected OK, got " + resp.StatusCode + ": " + bodyText);
        var body = await resp.Content.ReadFromJsonAsync<VerdictResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(85, body!.RealScore);
        Assert.Contains("admin", body.Reasoning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ABAC", body.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Triage_Recovers_When_Model_Wraps_Json_In_Prose()
    {
        // Local models often wrap the JSON in markdown fences or a
        // chatty preamble. The parser extracts the first {...} block.
        const string Wrapped = "Sure thing! Here's my verdict:\n```json\n{\"realScore\":40,\"reasoning\":\"limited evidence\",\"fix\":\"capture full response\"}\n```\nHope this helps!";
        using var host = BuildHostWithStub((Wrapped));
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/triage",
            new { title = "idor probe", evidence = "id=2 returned id=3 row" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<VerdictResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(40, body!.RealScore);
        Assert.Contains("evidence", body.Reasoning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Triage_Falls_Back_To_Score_50_When_Model_Returns_Garbage()
    {
        // A model that ignores the system prompt and returns prose
        // shouldn't crash the endpoint; we surface a "couldn't parse"
        // fallback so the UI shows something useful.
        using var host = BuildHostWithStub(("I have no idea what this means."));
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/triage",
            new { title = "unknown probe" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<VerdictResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(50, body!.RealScore);
    }

    [Fact]
    public async Task Triage_Clamps_Score_To_0_100_Range()
    {
        using var host = BuildHostWithStub((
            """{"realScore":500,"reasoning":"out of range","fix":"clamp"}"""));
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/triage",
            new { title = "test" },
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<VerdictResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(100, body!.RealScore);
    }

    [Fact]
    public async Task Triage_Caps_Long_Evidence_In_Prompt()
    {
        // We can't observe the prompt directly through the public API,
        // but we can verify the endpoint doesn't crash on a huge
        // evidence string. The implementation truncates to 4k chars.
        var hugeEvidence = new string('A', 50_000);
        using var host = BuildHostWithStub((
            """{"realScore":10,"reasoning":"truncated","fix":"none"}"""));
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/triage",
            new { title = "huge evidence test", evidence = hugeEvidence },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static IHost BuildHostWithStub(string canned)
    {
        // CA2000: StubChatClient.Dispose is a no-op; the host owns the
        // singleton's lifetime anyway. Suppress so the test reads
        // cleanly without a wrapper-disposable.
#pragma warning disable CA2000
        var chatClient = new StubChatClient(canned);
#pragma warning restore CA2000
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
                       s.AddSingleton<IChatClient>(chatClient);
                       s.AddBowireAi(new ConfigurationBuilder().Build());
                   });
            })
            .Start();
    }

    private static IHost BuildHostWithoutClient()
    {
        // Register the minimum surface the other endpoints in the
        // group need (BowireAiOptions + BowireAiRuntime) but leave
        // IChatClient unregistered so the triage endpoint's null
        // check is the path the test exercises.
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
                       s.AddSingleton(sp => new BowireAiRuntime(sp.GetRequiredService<BowireAiOptions>()));
                   });
            })
            .Start();
    }

    private sealed record VerdictResponse(int RealScore, string Reasoning, string Fix);

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

    private sealed class TempStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }
}
