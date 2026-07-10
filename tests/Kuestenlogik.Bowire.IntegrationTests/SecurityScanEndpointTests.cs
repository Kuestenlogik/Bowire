// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Ai;
using Kuestenlogik.Bowire.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// In-process tests for <c>POST /api/ai/security-scan</c> (#104): the
/// orchestration endpoint chains threat-model → probe → triage → report. The
/// AI is a routing stub (per-stage canned responses) and the probe stage is a
/// fake runner; the without-AI path exercises the deterministic degrade.
/// </summary>
public sealed class SecurityScanEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body() => new
    {
        endpoints = new[]
        {
            new { endpointId = "e1", path = "/admin/users", method = "DELETE" },
            new { endpointId = "e2", path = "/health", method = "GET" },
        },
        target = "https://api.example.com",
    };

    [Fact]
    public async Task SecurityScan_WithAiAndRunner_ChainsFullPipeline()
    {
        using var host = BuildHost(withAi: true, runner: new FakeRunner());
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/security-scan", Body(), Ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("aiAvailable").GetBoolean());
        Assert.True(root.GetProperty("probeExecuted").GetBoolean());
        // AI ranked e1=9, e2=2 → only e1 is above threshold 5.
        Assert.Equal(["e1"], root.GetProperty("probed").EnumerateArray().Select(e => e.GetString()));
        var findings = root.GetProperty("findings").EnumerateArray().ToArray();
        var f = Assert.Single(findings);
        Assert.Equal("R-e1", f.GetProperty("ruleId").GetString());
        Assert.Equal(80, f.GetProperty("realScore").GetInt32()); // triage stub
        Assert.Contains("admin endpoint is exposed", root.GetProperty("reportMarkdown").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecurityScan_NoAi_DegradesToHeuristicAndKeepsFindings()
    {
        using var host = BuildHost(withAi: false, runner: new FakeRunner());
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/security-scan", Body(), Ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        var root = doc.RootElement;

        Assert.False(root.GetProperty("aiAvailable").GetBoolean());
        // Heuristic: /admin/users (+admin +user +write) is above threshold; /health is not.
        Assert.Equal(["e1"], root.GetProperty("probed").EnumerateArray().Select(e => e.GetString()));
        var f = Assert.Single(root.GetProperty("findings").EnumerateArray().ToArray());
        Assert.Equal(100, f.GetProperty("realScore").GetInt32()); // kept-by-default without AI triage
    }

    [Fact]
    public async Task SecurityScan_NoRunner_PlanOnlyNoFindings()
    {
        using var host = BuildHost(withAi: false, runner: null);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/security-scan", Body(), Ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        var root = doc.RootElement;

        Assert.False(root.GetProperty("probeExecuted").GetBoolean());
        Assert.Empty(root.GetProperty("findings").EnumerateArray());
    }

    [Fact]
    public async Task SecurityScan_MissingEndpoints_Returns400()
    {
        using var host = BuildHost(withAi: false, runner: null);
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync("/api/ai/security-scan", new { target = "x" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static IHost BuildHost(bool withAi, ISecurityScanProbeRunner? runner)
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
                    if (runner is not null) s.AddSingleton(runner);
                    if (withAi)
                    {
                        s.AddSingleton<IChatClient>(_ => new RoutingStubChatClient());
                        s.AddBowireAi(new ConfigurationBuilder().Build());
                    }
                    else
                    {
                        s.AddSingleton(new BowireAiOptions());
                        s.AddSingleton(sp => new BowireAiRuntime(sp.GetRequiredService<BowireAiOptions>()));
                    }
                }))
            .Start();

    // One finding per probed endpoint.
    private sealed class FakeRunner : ISecurityScanProbeRunner
    {
        public Task<IReadOnlyList<OrchestratedFinding>> RunAsync(OrchestratorEndpoint endpoint, string target, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OrchestratedFinding>>(
                [new OrchestratedFinding(endpoint.EndpointId, "R-" + endpoint.EndpointId, "Issue on " + endpoint.Path, "high", "API1-2023-BOLA")]);
    }

    // Returns a per-stage canned response based on the system prompt.
    private sealed class RoutingStubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var system = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";
            var text = system switch
            {
                _ when system.Contains("rank API endpoints", StringComparison.Ordinal)
                    => """[{"endpointId":"e1","risk":9,"reason":"admin delete"},{"endpointId":"e2","risk":2,"reason":"health"}]""",
                _ when system.Contains("triage a security finding", StringComparison.Ordinal)
                    => """{"realScore":80,"reasoning":"reachable admin delete"}""",
                _ => "The admin endpoint is exposed and should be gated.",
            };
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
