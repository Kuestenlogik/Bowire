// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Ai;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// In-process tests for <c>POST /api/ai/jwt-analyze</c> (#105). The
/// deterministic JWT analysis (JwtSecurityAnalyzer) is always returned; the AI
/// narrative is additive and degrades gracefully when no IChatClient is
/// registered. Driven via a TestServer with a stub chat client.
/// </summary>
public sealed class JwtAnalyzeEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    // alg:none token whose payload also carries a read+admin scope (scope creep).
    private static string AlgNoneToken() => $"{B64("""{"alg":"none"}""")}.{B64("""{"sub":"x","scope":"read admin"}""")}.";

    [Fact]
    public async Task JwtAnalyze_WithClient_ReturnsFlagsAndAiNarrative()
    {
        using var host = BuildHost("This token is unsigned — reject it.");
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/jwt-analyze", new { token = AlgNoneToken() }, Ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("parsed").GetBoolean());
        Assert.Equal("none", root.GetProperty("algorithm").GetString());
        Assert.True(root.GetProperty("aiAvailable").GetBoolean());
        Assert.Equal("This token is unsigned — reject it.", root.GetProperty("aiNarrative").GetString());
        // Deterministic flags present: alg=none (HIGH) + scope creep (MEDIUM).
        var flags = root.GetProperty("flags").EnumerateArray().ToArray();
        Assert.Contains(flags, f => f.GetProperty("level").GetString() == "HIGH" && f.GetProperty("claim").GetString() == "alg");
        Assert.Contains(flags, f => f.GetProperty("claim").GetString() == "scope");
    }

    [Fact]
    public async Task JwtAnalyze_WithoutClient_ReturnsFlagsOnly()
    {
        using var host = BuildHost(null);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/jwt-analyze", new { token = AlgNoneToken() }, Ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        var root = doc.RootElement;

        Assert.False(root.GetProperty("aiAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("aiNarrative").ValueKind);
        Assert.NotEmpty(root.GetProperty("flags").EnumerateArray());
    }

    [Fact]
    public async Task JwtAnalyze_MalformedToken_Returns400()
    {
        using var host = BuildHost(null);
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync("/api/ai/jwt-analyze", new { token = "not-a-jwt" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task JwtAnalyze_MissingToken_Returns400()
    {
        using var host = BuildHost(null);
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync("/api/ai/jwt-analyze", new { audience = "svc" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // cannedNarrative == null → no IChatClient registered at all (degrade path);
    // otherwise a stub chat client is registered (mirrors the other AI endpoint tests).
    private static IHost BuildHost(string? cannedNarrative)
        => new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapBowireAiEndpoints(basePath: string.Empty));
                })
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    if (cannedNarrative is not null)
                    {
                        s.AddSingleton<IChatClient>(_ => new StubChatClient(cannedNarrative));
                        s.AddBowireAi(new ConfigurationBuilder().Build());
                    }
                    else
                    {
                        // No IChatClient registered → [FromServices] IChatClient? is null.
                        s.AddSingleton(new BowireAiOptions());
                        s.AddSingleton(sp => new BowireAiRuntime(sp.GetRequiredService<BowireAiOptions>()));
                    }
                }))
            .Start();

    private sealed class StubChatClient(string responseText) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
