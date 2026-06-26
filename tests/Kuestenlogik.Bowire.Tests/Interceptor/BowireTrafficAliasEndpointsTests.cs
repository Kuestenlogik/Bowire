// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Interceptor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Interceptor;

/// <summary>
/// Coverage for #315 — the unified Traffic rail's <c>/api/traffic/*</c>
/// alias of the legacy <c>/api/intercepted/*</c> endpoints. Identical
/// shape; both prefixes must resolve to the same backing store, and the
/// legacy prefix must keep working so existing operators / scripts
/// aren't broken by the rail unification.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class BowireTrafficAliasEndpointsTests
{
    private static InterceptedFlow Flow(long id) => new()
    {
        Id = id,
        CapturedAt = DateTimeOffset.UtcNow,
        Method = "GET",
        Url = "http://example/foo",
        Path = "/foo",
        Scheme = "http",
        RequestHeaders = Array.Empty<KeyValuePair<string, string>>(),
        ResponseStatus = 200,
        ResponseHeaders = Array.Empty<KeyValuePair<string, string>>(),
        ResponseBody = "{}",
        LatencyMs = 1,
    };

    private static async Task<(WebApplication app, HttpClient http, InterceptedFlowStore store)> StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddBowireInterceptorCore();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.MapBowireInterceptorEndpoints(string.Empty);
        await app.StartAsync(ct);
        var addr = app.Urls.First();
        var http = new HttpClient { BaseAddress = new Uri(addr) };
        var store = app.Services.GetRequiredService<InterceptedFlowStore>();
        return (app, http, store);
    }

    [Fact]
    public async Task TrafficFlows_AliasOfInterceptedFlows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store) = await StartAsync(ct);
        await using var _ = app;
        using var __ = http;
        store.Add(Flow(1));
        store.Add(Flow(2));

        // Both prefixes resolve the same snapshot.
        using var legacy = await http.GetAsync(new Uri("/api/intercepted/flows", UriKind.Relative), ct);
        using var canonical = await http.GetAsync(new Uri("/api/traffic/flows", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.Equal(HttpStatusCode.OK, canonical.StatusCode);

        var legacyPayload = await legacy.Content.ReadFromJsonAsync<JsonElement>(ct);
        var canonicalPayload = await canonical.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(
            legacyPayload.GetProperty("flows").GetArrayLength(),
            canonicalPayload.GetProperty("flows").GetArrayLength());
    }

    [Fact]
    public async Task TrafficFlowDetail_AliasReturnsSameFullPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store) = await StartAsync(ct);
        await using var _ = app;
        using var __ = http;
        store.Add(Flow(42));

        using var legacy = await http.GetAsync(new Uri("/api/intercepted/flows/42", UriKind.Relative), ct);
        using var canonical = await http.GetAsync(new Uri("/api/traffic/flows/42", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.Equal(HttpStatusCode.OK, canonical.StatusCode);
        var legacyBody = await legacy.Content.ReadAsStringAsync(ct);
        var canonicalBody = await canonical.Content.ReadAsStringAsync(ct);
        Assert.Equal(legacyBody, canonicalBody);
    }

    [Fact]
    public async Task TrafficFlows_DeleteAlias_ClearsStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store) = await StartAsync(ct);
        await using var _ = app;
        using var __ = http;
        store.Add(Flow(1));
        store.Add(Flow(2));

        using var resp = await http.DeleteAsync(new Uri("/api/traffic/flows", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public async Task TrafficMocks_AliasReturnsRulesAndEnabledFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, _) = await StartAsync(ct);
        await using var _app = app;
        using var __ = http;

        using var resp = await http.GetAsync(new Uri("/api/traffic/mocks", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        // Same shape as /api/intercepted/mocks — { enabled: bool, rules: [] }.
        Assert.True(payload.TryGetProperty("enabled", out _));
        Assert.True(payload.TryGetProperty("rules", out var rules));
        Assert.Equal(JsonValueKind.Array, rules.ValueKind);
    }
}
