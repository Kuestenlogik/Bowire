// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// <c>POST /api/lint</c> — the Lint rail's half of CLI/UI parity.
/// </summary>
/// <remarks>
/// <para>
/// The rail POSTs the service list it already holds rather than triggering a
/// second discovery, so this endpoint is a thin adapter over the same engine
/// <c>bowire lint</c> drives. What it owes the UI is a stable envelope: a
/// findings array and a summary the rail renders as counts per severity.
/// </para>
/// <para>
/// The failure worth guarding is a lint surface that goes down for a reason
/// unrelated to the schema being linted — a malformed request body, or a
/// broken <c>.bowire/rules.json</c> lying around in the working directory.
/// </para>
/// </remarks>
public sealed class BowireLintEndpointTests
{
    private static async Task<IHost> BuildHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireLintEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static BowireServiceInfo Service(string name, params BowireMethodInfo[] methods)
        => new(name, name.Contains('.', StringComparison.Ordinal)
            ? name[..name.LastIndexOf('.')]
            : "", [.. methods]);

    private static BowireMethodInfo Method(string name, string service)
        => new(name, $"{service}.{name}", false, false,
            new BowireMessageInfo("Request", $"{service}.Request", []),
            new BowireMessageInfo("Response", $"{service}.Response", []),
            "unary");

    private static async Task<JsonElement> Lint(IHost host, object body)
    {
        using var resp = await host.GetTestClient().PostAsJsonAsync(
            new Uri("/api/lint", UriKind.Relative), body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task An_Empty_Service_List_Lints_To_Nothing()
    {
        // What the rail sends before any discovery has run. An error here
        // would show as a broken pane on an empty workspace.
        using var host = await BuildHost();

        var body = await Lint(host, new { services = Array.Empty<object>() });

        Assert.Empty(body.GetProperty("findings").EnumerateArray());
        Assert.Equal(0, body.GetProperty("summary").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_Request_With_No_Services_Field_Is_Treated_As_An_Empty_List()
    {
        // `{}` rather than `{"services":[]}` — an older client, or a hand-made
        // curl. Null-coalescing to an empty list is what keeps that a 200.
        using var host = await BuildHost();

        var body = await Lint(host, new { });

        Assert.Equal(0, body.GetProperty("summary").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_Envelope_Always_Carries_Both_Findings_And_A_Summary()
    {
        // The rail indexes both on every response; a shape that varies with
        // the result would break it exactly when there is nothing to show.
        using var host = await BuildHost();

        var body = await Lint(host, new { services = Array.Empty<object>() });

        Assert.Equal(JsonValueKind.Array, body.GetProperty("findings").ValueKind);
        var summary = body.GetProperty("summary");
        foreach (var counter in new[] { "total", "high", "medium", "low", "info" })
        {
            Assert.True(summary.TryGetProperty(counter, out _), $"summary.{counter} missing");
        }
    }

    [Fact]
    public async Task A_Real_Service_List_Comes_Back_As_Findings_The_Rail_Can_Render()
    {
        // Each finding is flattened for the UI — rule id, a severity as a
        // string rather than an enum ordinal, and the location. Whatever the
        // rule set says about this service, the row shape is the contract.
        using var host = await BuildHost();
        var services = new[]
        {
            Service("orders.v1.OrderService", Method("getOrder", "orders.v1.OrderService")),
        };

        var body = await Lint(host, new { services });

        foreach (var finding in body.GetProperty("findings").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(finding.GetProperty("ruleId").GetString()));
            Assert.Equal(JsonValueKind.String, finding.GetProperty("severity").ValueKind);
            Assert.True(finding.TryGetProperty("message", out _));
        }
    }

    [Fact]
    public async Task The_Summary_Counts_Agree_With_The_Findings_It_Summarises()
    {
        // The rail renders the counts and the list side by side; a summary
        // computed over a different set is the kind of bug nobody reports
        // because both halves look plausible on their own.
        using var host = await BuildHost();
        var services = new[]
        {
            Service("orders.v1.OrderService", Method("getOrder", "orders.v1.OrderService")),
            Service("Catalog", Method("List", "Catalog")),
        };

        var body = await Lint(host, new { services });

        var findings = body.GetProperty("findings").EnumerateArray().ToList();
        var summary = body.GetProperty("summary");
        Assert.Equal(findings.Count, summary.GetProperty("total").GetInt32());
        Assert.Equal(
            findings.Count,
            summary.GetProperty("high").GetInt32()
            + summary.GetProperty("medium").GetInt32()
            + summary.GetProperty("low").GetInt32()
            + summary.GetProperty("info").GetInt32());
    }

    [Fact]
    public async Task A_Body_That_Is_Not_Json_Is_A_400_With_A_Reason()
    {
        // A 500 here would read as "lint is broken" rather than "your request
        // was", and the rail has no way to tell those apart.
        using var host = await BuildHost();
        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");

        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/lint", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Malformed lint request", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Body_Of_The_Wrong_Shape_Is_A_400_Rather_Than_A_Crash()
    {
        // An array where the envelope belongs — the commonest hand-written
        // mistake, since /api/services returns exactly that array.
        using var host = await BuildHost();
        using var content = new StringContent("[]", Encoding.UTF8, "application/json");

        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/lint", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
