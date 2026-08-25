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
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    /// <summary>POST a JSON body, disposing the content the call created.</summary>
    private static async Task<HttpResponseMessage> PostJson(IHost host, string path, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await host.GetTestClient().PostAsync(
            new Uri(path, UriKind.Relative), content, TestContext.Current.CancellationToken);
    }

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

        using var resp = await PostJson(host, "/api/tools/reverse-proxy/start", """{"upstream":"https://api.example.com","port":18080}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        using var doc = await ReadJson(resp);
        Assert.Equal("urn:bowire:tools:reverse-proxy:no-registry", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_Body_That_Is_Not_Json_Is_A_400_Naming_The_Parse_Failure()
    {
        using var host = await BuildHost();

        using var resp = await PostJson(host, "/api/tools/reverse-proxy/start", "{ not json");

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

        using var resp = await PostJson(host, "/api/tools/reverse-proxy/start", body);

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

        using var resp = await PostJson(host, "/api/tools/reverse-proxy/start", $$"""{"upstream":"https://api.example.com","port":{{port}}}""");

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

        using var resp = await PostJson(host, "/api/tools/reverse-proxy/start", "null");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- stop ----

    // Stop answers differently from start, and deliberately so: stopping is
    // idempotent, so "there was nothing to stop" is the desired end state
    // rather than an error. It says so in the body instead of the status —
    // `stopped: false` — which is what lets the rail refresh a row without
    // having to treat a race with another client as a failure.
    //
    // Worth noting the asymmetry: start on a registry-less host is a 503,
    // stop on one is a 200. Defensible (nothing to stop either way), but not
    // obvious, which is exactly why it is pinned here.

    [Fact]
    public async Task Stopping_On_A_Host_Without_The_Registry_Reports_Nothing_Stopped()
    {
        using var host = await BuildHost(withRegistry: false);

        using var resp = await PostJson(host, "/api/tools/reverse-proxy/stop", """{"port":18080}""");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = await ReadJson(resp);
        Assert.False(doc.RootElement.GetProperty("stopped").GetBoolean());
    }

    [Fact]
    public async Task Stopping_A_Port_Nothing_Runs_On_Reports_Nothing_Stopped()
    {
        using var host = await BuildHost();

        using var resp = await PostJson(host, "/api/tools/reverse-proxy/stop", """{"port":18081}""");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = await ReadJson(resp);
        Assert.False(doc.RootElement.GetProperty("stopped").GetBoolean());
        // The port is echoed so a rail handling several at once can tell which
        // request this answer belongs to.
        Assert.Equal(18081, doc.RootElement.GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task Stopping_Without_A_Port_Is_A_400()
    {
        // The one stop-path that is an error: no port means the request
        // cannot be acted on at all.
        using var host = await BuildHost();

        using var resp = await PostJson(host, "/api/tools/reverse-proxy/stop", """{"port":0}""");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Stopping_With_A_Body_That_Is_Not_Json_Is_A_400()
    {
        using var host = await BuildHost();

        using var resp = await PostJson(host, "/api/tools/reverse-proxy/stop", "{ not json");

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
        await host.StartAsync(TestContext.Current.CancellationToken);

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
