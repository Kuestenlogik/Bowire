// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// <c>POST /api/parallel/start-local</c> and <c>/start</c> — the worker and
/// the coordinator for parallel sessions across hosts (#132 Phase 2).
/// </summary>
/// <remarks>
/// The coordinator fans a run out to other Bowire hosts, so one of these
/// routes is what a peer sees arrive over the network. Its handling of a
/// body it cannot read is therefore a contract with something the operator
/// does not control, and that is what is pinned here: a refusal that names
/// the route and the shape it wanted, rather than a 500 on a peer's
/// malformed post.
/// </remarks>
public sealed class BowireParallelEndpointsTests
{
    [Theory]
    [InlineData("/api/parallel/start-local")]
    [InlineData("/api/parallel/start")]
    public async Task A_Body_That_Is_Not_Json_Is_A_Problem_Document(string route)
    {
        using var host = await BuildHost();

        var (status, body) = await PostAsync(host, route, "{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("urn:bowire:invalid-input", doc.RootElement.GetProperty("type").GetString());
        // The parser's own message travels: "invalid JSON" alone leaves the
        // caller guessing which byte.
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("detail").GetString()));
    }

    [Theory]
    [InlineData("/api/parallel/start-local", "targets, sessionCount")]
    [InlineData("/api/parallel/start", "targets, sessions, hosts?")]
    public async Task An_Empty_Body_Is_Told_What_Was_Expected(string route, string shape)
    {
        // JSON "null" deserialises to null rather than throwing, so this is a
        // separate path from the one above — and the answer names the body
        // the route wanted, because a caller who sent nothing has no other
        // way to find out.
        using var host = await BuildHost();

        var (status, body) = await PostAsync(host, route, "null");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        using var doc = JsonDocument.Parse(body);
        var detail = doc.RootElement.GetProperty("detail").GetString()!;
        Assert.Contains(route, detail, StringComparison.Ordinal);
        Assert.Contains(shape, detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/parallel/start-local")]
    [InlineData("/api/parallel/start")]
    public async Task The_Refusal_Names_The_Route_It_Came_From(string route)
    {
        // instance is what tells a coordinator log which of the two routes
        // refused, when both are being called at once.
        using var host = await BuildHost();

        var (_, body) = await PostAsync(host, route, "{ not json");

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(route, doc.RootElement.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task A_Run_With_No_Targets_Comes_Back_Rather_Than_Failing()
    {
        // The workbench posts whatever is on screen, and an empty target
        // list is what the first paint has.
        using var host = await BuildHost();

        var (status, body) = await PostAsync(
            host, "/api/parallel/start-local", """{"targets":[],"sessionCount":2}""");

        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        // A run happened -- it just had nothing to do. The counts are what
        // the workbench renders, and zeroes are a result, not a blank.
        Assert.Equal(2, doc.RootElement.GetProperty("sessionCount").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("targetCount").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("passCount").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("failCount").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task With_No_Hosts_The_Coordinator_Runs_It_Itself()
    {
        // Documented collapse to an in-process run, so the workbench does not
        // need a second code path for the single-host case.
        using var host = await BuildHost();

        var (status, body) = await PostAsync(
            host, "/api/parallel/start", """{"targets":[],"sessionCount":3,"hosts":[]}""");

        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(3, doc.RootElement.GetProperty("sessionCount").GetInt32());
        // No peers were asked, so there is no per-host roll-up -- which is
        // how the workbench tells a distributed run from a local one. Absent
        // rather than an empty array: nulls are dropped on the way out, and
        // a client that only checks for the key has to cope with that.
        Assert.True(
            !doc.RootElement.TryGetProperty("hosts", out var hosts)
            || hosts.ValueKind is JsonValueKind.Null
            || hosts.GetArrayLength() == 0,
            hosts.ToString());
    }

    // ---- harness ----

    private static async Task<IHost> BuildHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .ConfigureServices(s => s.AddRouting())
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireParallelEndpoints(string.Empty));
                   });
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(
        IHost host, string route, string json)
    {
        using var client = host.GetTestClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            new Uri(route, UriKind.Relative), content, TestContext.Current.CancellationToken);
        return (response.StatusCode,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
