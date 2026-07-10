// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
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
/// In-process tests for <c>POST /api/ai/owasp-panel</c> (#106): deterministic
/// per-method OWASP rows are always returned; an AI review is added when an
/// IChatClient is registered.
/// </summary>
public sealed class OwaspPanelEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OwaspPanel_ReturnsTenRows_WithBolaAtRisk()
    {
        using var host = BuildHost(null);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/owasp-panel", new { path = "/orders/{id}", verb = "GET" }, Ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        var root = doc.RootElement;

        Assert.False(root.GetProperty("aiAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("aiReview").ValueKind);
        var rows = root.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(10, rows.Length);
        var api1 = rows.Single(r => r.GetProperty("entry").GetString() == "API1:2023");
        Assert.Equal("AtRisk", api1.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(api1.GetProperty("suggestedProbe").GetString()));
    }

    [Fact]
    public async Task OwaspPanel_WithClient_AddsAiReview()
    {
        using var host = BuildHost("Focus on the BOLA — the id in the path isn't ownership-checked.");
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/owasp-panel", new { path = "/orders/{id}", verb = "GET" }, Ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("aiAvailable").GetBoolean());
        Assert.Contains("BOLA", root.GetProperty("aiReview").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwaspPanel_MissingPath_Returns400()
    {
        using var host = BuildHost(null);
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync("/api/ai/owasp-panel", new { verb = "GET" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static IHost BuildHost(string? cannedReview)
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
                    if (cannedReview is not null)
                    {
                        s.AddSingleton<IChatClient>(_ => new StubChatClient(cannedReview));
                        s.AddBowireAi(new ConfigurationBuilder().Build());
                    }
                    else
                    {
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
