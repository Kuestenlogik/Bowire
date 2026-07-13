// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Kuestenlogik.Bowire.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// #102 — the read-only /api/monitoring/* endpoints the Monitoring rail
/// renders: probe overview (last outcome + sparkline tail) and the
/// per-probe outcome history. The endpoints mount through the
/// <c>IBowireEndpointContribution</c> seam, so these tests also pin that
/// the contribution is discovered from the Monitoring assembly.
/// </summary>
public sealed class BowireMonitoringEndpointTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bowire-mon-api-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private static ProbeOutcome Outcome(long t, ProbeResult r, double latencyMs = 0, string? error = null)
        => new() { TimestampUnixMs = t, Result = r, LatencyMs = latencyMs, Error = error };

    [Fact]
    public async Task Probes_ReturnsEmptyList_WhenLedgerRootIsEmpty()
    {
        await using var host = await CreateHost();
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/monitoring/probes", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, doc.RootElement.GetProperty("probes").GetArrayLength());
    }

    [Fact]
    public async Task Probes_ReturnsLastOutcomeAndHistoryPerProbe()
    {
        var ledger = new OutcomeLedger(_dir);
        ledger.Append("checkout", Outcome(100, ProbeResult.Pass, latencyMs: 12));
        ledger.Append("checkout", Outcome(200, ProbeResult.Fail, latencyMs: 34));
        ledger.Append("inventory", Outcome(300, ProbeResult.Error, error: "connection refused"));

        await using var host = await CreateHost();
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/monitoring/probes", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var probes = doc.RootElement.GetProperty("probes");
        Assert.Equal(2, probes.GetArrayLength());

        var checkout = probes[0];
        Assert.Equal("checkout", checkout.GetProperty("name").GetString());
        Assert.Equal("fail", checkout.GetProperty("last").GetProperty("result").GetString());
        Assert.Equal(2, checkout.GetProperty("history").GetArrayLength());
        // Ledger order — oldest first, so the sparkline reads left→right.
        Assert.Equal(100, checkout.GetProperty("history")[0].GetProperty("t").GetInt64());

        var inventory = probes[1];
        Assert.Equal("inventory", inventory.GetProperty("name").GetString());
        Assert.Equal("connection refused", inventory.GetProperty("last").GetProperty("error").GetString());
    }

    [Fact]
    public async Task Outcomes_ReturnsRowsForOneProbe_WithLimitTail()
    {
        var ledger = new OutcomeLedger(_dir);
        for (var i = 1; i <= 5; i++)
        {
            ledger.Append("checkout", Outcome(i * 100, ProbeResult.Pass, latencyMs: i));
        }

        await using var host = await CreateHost();
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/monitoring/probes/checkout/outcomes?limit=2", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("checkout", doc.RootElement.GetProperty("name").GetString());
        var outcomes = doc.RootElement.GetProperty("outcomes");
        Assert.Equal(2, outcomes.GetArrayLength());
        Assert.Equal(400, outcomes[0].GetProperty("t").GetInt64());
        Assert.Equal(500, outcomes[1].GetProperty("t").GetInt64());
    }

    [Fact]
    public async Task Outcomes_UnknownProbe_ReturnsEmptyRows()
    {
        await using var host = await CreateHost();
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/monitoring/probes/nope/outcomes", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, doc.RootElement.GetProperty("outcomes").GetArrayLength());
    }

    [Fact]
    public async Task Outcomes_PathTraversalName_StaysInsideTheLedgerRoot()
    {
        var ledger = new OutcomeLedger(_dir);
        ledger.Append("checkout", Outcome(100, ProbeResult.Pass));

        await using var host = await CreateHost();
        var client = host.GetTestClient();

        // PathFor re-sanitises the stem, so a traversal-ish name resolves to
        // a (missing) file inside the root instead of escaping it.
        var resp = await client.GetAsync(new Uri("/bowire/api/monitoring/probes/..%2F..%2Fsecrets/outcomes", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, doc.RootElement.GetProperty("outcomes").GetArrayLength());
    }

    private async Task<WebApplication> CreateHost()
    {
        // Force-load the Monitoring assembly so the endpoint-contribution
        // scan finds it (same pattern as the protocol force-loads in the
        // sibling endpoint tests).
        _ = typeof(OutcomeLedger).Assembly;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        // Pin the ledger to this test's temp root — never the developer's
        // real ~/.bowire/monitoring.
        builder.Services.AddSingleton(new OutcomeLedger(_dir));

        var app = builder.Build();
        app.UseStaticFiles();
        app.MapBowire("/bowire");

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
