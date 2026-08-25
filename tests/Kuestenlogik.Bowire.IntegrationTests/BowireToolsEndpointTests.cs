// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Interceptor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// <c>/api/tools/reverse-proxy</c> — the surface the workbench's Tools rail
/// drives.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is about the answers given <em>before</em> a socket is
/// bound: a host that never registered the registry, a payload that cannot
/// name an upstream, a port outside the legal range, a port already taken.
/// Those are the paths an operator actually hits, and each one has to say
/// which of the four it was — a single 400 for all of them would leave them
/// guessing at a form with two fields.
/// </para>
/// <para>
/// Binding a real port is <c>BowireReverseProxyHostTests</c>' job; this stays
/// on the endpoint contract.
/// </para>
/// </remarks>
public sealed class BowireToolsEndpointTests
{
    private static async Task<IHost> BuildHost(bool withRegistry = true)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireToolsEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       if (withRegistry) s.AddSingleton<ReverseProxyRegistry>();
                   });
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    // ---- listing ----

    [Fact]
    public async Task Listing_On_A_Host_Without_The_Registry_Is_An_Empty_List()
    {
        // A rail package owes an embedded host a degraded pane, not a broken
        // one: the Tools rail asks for this on load, and a 500 there would
        // take the panel down on a host that simply never opted in.
        using var host = await BuildHost(withRegistry: false);

        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/tools/reverse-proxy", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = await ReadJson(resp);
        Assert.Equal(0, doc.RootElement.GetProperty("proxies").GetArrayLength());
    }

    [Fact]
    public async Task Listing_With_No_Proxies_Running_Is_An_Empty_List()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/tools/reverse-proxy", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = await ReadJson(resp);
        Assert.Equal(0, doc.RootElement.GetProperty("proxies").GetArrayLength());
    }

    // ---- start: refusals ----

    [Fact]
    public async Task Starting_Without_A_Registry_Says_The_Host_Did_Not_Wire_It()
    {
        // 503 with a reason, not 500: nothing is broken, the capability is
        // absent, and the operator can only fix it in the host.
        using var host = await BuildHost(withRegistry: false);

        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/tools/reverse-proxy/start", UriKind.Relative),
            Json("""{"upstream":"https://api.example.com","port":18080}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        using var doc = await ReadJson(resp);
        Assert.Equal("urn:bowire:tools:reverse-proxy:no-registry", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_Body_That_Is_Not_Json_Is_A_400_Naming_The_Parse_Failure()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/tools/reverse-proxy/start", UriKind.Relative),
            Json("{ not json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = await ReadJson(resp);
        Assert.Equal("urn:bowire:tools:reverse-proxy:bad-request", doc.RootElement.GetProperty("type").GetString());
        // The parser's own message rides along — "invalid payload" alone
        // leaves the caller with nothing to correct.
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("detail").GetString()));
    }

    [Theory]
    [InlineData("""{"port":18080}""")]                              // no upstream at all
    [InlineData("""{"upstream":"","port":18080}""")]                // blank
    [InlineData("""{"upstream":"   ","port":18080}""")]
    [InlineData("""{"upstream":"example.com","port":18080}""")]     // not absolute
    [InlineData("""{"upstream":"/relative","port":18080}""")]
    public async Task An_Upstream_That_Is_Not_An_Absolute_Url_Is_Refused(string body)
    {
        // The proxy forwards to it; "example.com" has no scheme to forward
        // with, and guessing http:// would silently downgrade the connection.
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/tools/reverse-proxy/start", UriKind.Relative), Json(body),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = await ReadJson(resp);
        Assert.Contains("upstream", doc.RootElement.GetProperty("title").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task A_Port_Outside_The_Legal_Range_Is_Refused_With_The_Range(int port)
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/tools/reverse-proxy/start", UriKind.Relative),
            Json($$"""{"upstream":"https://api.example.com","port":{{port}}}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = await ReadJson(resp);
        // Naming the range is the difference between a message that fixes the
        // input and one that only reports it.
        Assert.Contains("65535", doc.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Empty_Body_Is_Refused_Rather_Than_Started_With_Defaults()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/tools/reverse-proxy/start", UriKind.Relative), Json("null"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- stop ----

    [Fact]
    public async Task Stopping_Without_A_Registry_Says_So()
    {
        using var host = await BuildHost(withRegistry: false);

        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/tools/reverse-proxy/stop", UriKind.Relative),
            Json("""{"port":18080}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task Stopping_A_Port_Nothing_Runs_On_Is_A_404_Not_A_Success()
    {
        // Reporting success would tell the rail to drop a row that was never
        // there, and hide a port the operator has genuinely mistyped.
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/tools/reverse-proxy/stop", UriKind.Relative),
            Json("""{"port":18081}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Stopping_With_A_Body_That_Is_Not_Json_Is_A_400()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/tools/reverse-proxy/stop", UriKind.Relative), Json("{ not json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- routing ----

    [Fact]
    public async Task The_Endpoints_Mount_Under_A_Base_Path()
    {
        // Embedded hosts mount the whole surface under a prefix; a route that
        // ignored basePath would work standalone and 404 embedded.
        var host = new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapBowireToolsEndpoints(basePath: "/bowire"));
                })
                .ConfigureServices(s => { s.AddRouting(); s.AddSingleton<ReverseProxyRegistry>(); }))
            .Build();
        await host.StartAsync();

        using (host)
        {
            using var resp = await host.GetTestClient().GetAsync(
                new Uri("/bowire/api/tools/reverse-proxy", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
    }

    [Fact]
    public void Mapping_Rejects_A_Null_Builder()
        => Assert.Throws<ArgumentNullException>(
            () => BowireToolsEndpoints.MapBowireToolsEndpoints(null!, string.Empty));
}
