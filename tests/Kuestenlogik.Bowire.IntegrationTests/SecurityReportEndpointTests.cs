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
/// In-process tests for <c>POST /api/ai/security-report</c> (#107). The
/// deterministic report (SARIF → grouped markdown) is always returned; an AI
/// executive summary is prepended when an IChatClient is registered.
/// </summary>
public sealed class SecurityReportEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sarif()
    {
        var doc = new
        {
            runs = new[]
            {
                new
                {
                    tool = new { driver = new { rules = new[]
                    {
                        new { id = "R1", name = "BOLA on getOrder", properties = new Dictionary<string, string> { ["owaspApi"] = "API1-2023-BOLA", ["security-severity"] = "7.5" } },
                    } } },
                    results = new[]
                    {
                        new { ruleId = "R1", level = "error", message = new { text = "m" }, partialFingerprints = new Dictionary<string, string> { ["bowire/v1"] = "fp1" } },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(doc);
    }

    [Fact]
    public async Task SecurityReport_WithClient_IncludesExecutiveSummary()
    {
        using var host = BuildHost("The API leaks orders across tenants — fix the BOLA first.");
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/security-report", new { sarif = Sarif(), target = "api.example.com" }, Ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("aiAvailable").GetBoolean());
        Assert.Equal(1, root.GetProperty("findingCount").GetInt32());
        var md = root.GetProperty("markdown").GetString()!;
        Assert.Contains("# Security scan report — api.example.com", md, StringComparison.Ordinal);
        Assert.Contains("## Executive summary", md, StringComparison.Ordinal);
        Assert.Contains("leaks orders across tenants", md, StringComparison.Ordinal);
        Assert.Contains("### [high] BOLA on getOrder", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecurityReport_WithoutClient_DeterministicOnly()
    {
        using var host = BuildHost(null);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/security-report", new { sarif = Sarif() }, Ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        var root = doc.RootElement;

        Assert.False(root.GetProperty("aiAvailable").GetBoolean());
        var md = root.GetProperty("markdown").GetString()!;
        Assert.DoesNotContain("## Executive summary", md, StringComparison.Ordinal);
        Assert.Contains("### [high] BOLA on getOrder", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecurityReport_MissingSarif_Returns400()
    {
        using var host = BuildHost(null);
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync("/api/ai/security-report", new { target = "x" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static IHost BuildHost(string? cannedSummary)
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
                    if (cannedSummary is not null)
                    {
                        s.AddSingleton<IChatClient>(_ => new StubChatClient(cannedSummary));
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
