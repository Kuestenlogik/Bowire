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
/// In-process tests for <c>POST /api/ai/threat-model</c> (#59). Drives
/// the endpoint via a TestServer with a stub <see cref="IChatClient"/>
/// returning canned ranked-list JSON, so we cover the happy path +
/// markdown-fence recovery + garbage fallback + topN cap + endpoint
/// cap + 503 / 400 error paths without standing up a real model.
/// </summary>
public sealed class BowireAiThreatModelEndpointTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _tempRoot;

    public BowireAiThreatModelEndpointTests()
    {
        _originalStore = BowireUserContext.Current;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"bowire-tm-test-{Guid.NewGuid():N}");
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
    public async Task ThreatModel_Returns_503_When_No_IChatClient_Registered()
    {
        using var host = BuildHostWithoutClient();
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "e1", path = "/probe" } } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task ThreatModel_Returns_400_For_Empty_Endpoints()
    {
        using var host = BuildHostWithStub("""{"ranked":[]}""");
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/threat-model",
            new { endpoints = Array.Empty<object>() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ThreatModel_Parses_Clean_Ranked_Json()
    {
        const string Canned = """
        {
          "ranked": [
            {"endpointId": "e1", "risk": 8, "why": "id-bearing path with anonymous access", "suggestedTemplates": ["idor", "auth-bypass"]},
            {"endpointId": "e2", "risk": 3, "why": "read-only listing", "suggestedTemplates": []}
          ]
        }
        """;
        using var host = BuildHostWithStub(Canned);
        using var client = host.GetTestClient();

        var payload = new
        {
            endpoints = new[]
            {
                new { endpointId = "e1", path = "/api/orders/{id}", verb = "GET", protocol = "rest" },
                new { endpointId = "e2", path = "/api/orders", verb = "GET", protocol = "rest" }
            }
        };
        var resp = await client.PostAsJsonAsync("/api/ai/threat-model", payload,
            TestContext.Current.CancellationToken);

        var bodyText = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, "expected OK, got " + resp.StatusCode + ": " + bodyText);
        var body = await resp.Content.ReadFromJsonAsync<RankingResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(2, body!.Ranked.Length);
        Assert.Equal("e1", body.Ranked[0].EndpointId);
        Assert.Equal(8, body.Ranked[0].Risk);
        Assert.Contains("idor", body.Ranked[0].SuggestedTemplates);
    }

    [Fact]
    public async Task ThreatModel_Recovers_When_Model_Wraps_Json_In_Prose()
    {
        const string Wrapped = """
        Sure! Here's my ranking:

        ```json
        {"ranked": [{"endpointId": "e1", "risk": 6, "why": "string-in body", "suggestedTemplates": ["injection-sqli"]}]}
        ```

        Hope this helps!
        """;
        using var host = BuildHostWithStub(Wrapped);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "e1", path = "/login", verb = "POST", protocol = "rest" } } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<RankingResponse>(TestContext.Current.CancellationToken);
        var row = Assert.Single(body!.Ranked);
        Assert.Equal(6, row.Risk);
    }

    [Fact]
    public async Task ThreatModel_Falls_Back_To_Empty_Ranking_For_Garbage()
    {
        using var host = BuildHostWithStub("I don't know how to rank these.");
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "e1", path = "/anything" } } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<RankingResponse>(TestContext.Current.CancellationToken);
        Assert.Empty(body!.Ranked);
    }

    [Fact]
    public async Task ThreatModel_Clamps_Risk_To_0_10()
    {
        const string OutOfRange = """{"ranked":[{"endpointId":"e1","risk":99,"why":"x","suggestedTemplates":[]}]}""";
        using var host = BuildHostWithStub(OutOfRange);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "e1", path = "/x" } } },
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<RankingResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(10, body!.Ranked[0].Risk);
    }

    [Fact]
    public async Task ThreatModel_Respects_TopN_Cap()
    {
        // Model returns 5 entries; we ask for topN=2 → only 2 reach the
        // caller. Guards against a runaway model that emits a 100-row
        // list when we only need the top picks.
        const string FiveRows = """
        {
          "ranked": [
            {"endpointId": "e1", "risk": 9, "why": "a", "suggestedTemplates": []},
            {"endpointId": "e2", "risk": 8, "why": "b", "suggestedTemplates": []},
            {"endpointId": "e3", "risk": 7, "why": "c", "suggestedTemplates": []},
            {"endpointId": "e4", "risk": 6, "why": "d", "suggestedTemplates": []},
            {"endpointId": "e5", "risk": 5, "why": "e", "suggestedTemplates": []}
          ]
        }
        """;
        using var host = BuildHostWithStub(FiveRows);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/threat-model",
            new
            {
                topN = 2,
                endpoints = new[]
                {
                    new { endpointId = "e1", path = "/a" },
                    new { endpointId = "e2", path = "/b" },
                    new { endpointId = "e3", path = "/c" },
                }
            },
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<RankingResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(2, body!.Ranked.Length);
        Assert.Equal("e1", body.Ranked[0].EndpointId);
        Assert.Equal("e2", body.Ranked[1].EndpointId);
    }

    [Fact]
    public async Task ThreatModel_Truncates_When_More_Than_200_Endpoints_Sent()
    {
        // Long-tail-cut guard: the model only sees the first 200; the
        // response signals truncated=true so the UI can show "ranked
        // from first 200 of N".
        using var host = BuildHostWithStub("""{"ranked":[]}""");
        using var client = host.GetTestClient();

        var endpoints = new object[300];
        for (var i = 0; i < endpoints.Length; i++)
            endpoints[i] = new { endpointId = $"e{i}", path = $"/probe/{i}" };

        var resp = await client.PostAsJsonAsync("/api/ai/threat-model",
            new { endpoints },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<TruncatedResponse>(TestContext.Current.CancellationToken);
        Assert.True(body!.Truncated);
        Assert.Equal(200, body.InputCount);
    }

    [Fact]
    public async Task ThreatModel_Skips_Rows_With_Missing_EndpointId()
    {
        // Malicious / sloppy model output that omits the endpointId
        // can't be correlated back to anything in the UI — drop those
        // rows instead of emitting garbage.
        const string PartialRows = """
        {
          "ranked": [
            {"endpointId": "e1", "risk": 7, "why": "valid", "suggestedTemplates": []},
            {"risk": 9, "why": "no endpointId — skipped", "suggestedTemplates": []},
            {"endpointId": "", "risk": 4, "why": "empty endpointId — skipped", "suggestedTemplates": []},
            {"endpointId": "e2", "risk": 3, "why": "also valid", "suggestedTemplates": []}
          ]
        }
        """;
        using var host = BuildHostWithStub(PartialRows);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "e1", path = "/a" } } },
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<RankingResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(2, body!.Ranked.Length);
        Assert.Equal("e1", body.Ranked[0].EndpointId);
        Assert.Equal("e2", body.Ranked[1].EndpointId);
    }

    private static IHost BuildHostWithStub(string canned)
    {
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

    private sealed record RankingResponse(RankedRow[] Ranked);
    private sealed record RankedRow(string EndpointId, int Risk, string Why, string[] SuggestedTemplates);
    private sealed record TruncatedResponse(int InputCount, bool Truncated);

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
