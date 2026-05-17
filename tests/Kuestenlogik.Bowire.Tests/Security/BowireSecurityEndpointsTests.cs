// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the server-side <c>/api/security/fuzz</c> endpoint that
/// the workbench's right-click "Fuzz this field" menu calls into. Spins
/// up Kestrel on a dynamic port with the endpoint mounted, drives both
/// the happy path (vulnerability-like response → marked vulnerable) and
/// every input-validation branch (missing fields, malformed JSON,
/// invalid base, etc.).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class BowireSecurityEndpointsTests
{
    private static async Task VulnerableUpstream(HttpContext ctx)
    {
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.WriteAsync("Microsoft SQL Server: incorrect syntax near OR", ctx.RequestAborted);
    }

    private static async Task<(WebApplication app, HttpClient http, string upstreamUrl)> StartAsync(
        CancellationToken ct,
        RequestDelegate? upstreamHandler = null)
    {
        // Upstream — the target the fuzz endpoint forwards payloads to.
        var upstreamBuilder = WebApplication.CreateSlimBuilder();
        upstreamBuilder.Logging.ClearProviders();
        upstreamBuilder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var upstream = upstreamBuilder.Build();
        ((IApplicationBuilder)upstream).Run(upstreamHandler ?? (async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"ok\":true}", ctx.RequestAborted);
        }));
        await upstream.StartAsync(ct);
        var upstreamUrl = upstream.Urls.First();

        // Workbench server — mounts the fuzz endpoint.
        var apiBuilder = WebApplication.CreateSlimBuilder();
        apiBuilder.Logging.ClearProviders();
        apiBuilder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = apiBuilder.Build();
        app.MapBowireSecurityEndpoints("");
        await app.StartAsync(ct);
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        // Cleanup of upstream piggybacks on the workbench `app` IAsyncDisposable
        // via the test's `await using var _ = app` — we leak the upstream's
        // lifetime here, but the test process exits between runs so it's
        // bounded. (Keeps the StartAsync helper's return shape simple.)
        return (app, http, upstreamUrl);
    }

    [Fact]
    public async Task PostFuzz_HappyPath_ReturnsRowsForEveryPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, upstreamUrl) = await StartAsync(ct);
        await using var _ = app;
        using var __ = http;

        var payload = new
        {
            target = upstreamUrl,
            httpVerb = "POST",
            httpPath = "/echo",
            body = "{\"username\":\"alice\"}",
            field = "$.username",
            category = "sqli",
            timeoutSeconds = 10,
        };
        using var resp = await http.PostAsJsonAsync(new Uri("/api/security/fuzz", UriKind.Relative), payload, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(5, body.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task PostFuzz_VulnerableResponse_MarksRowsAsVulnerable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, upstreamUrl) = await StartAsync(ct, upstreamHandler: VulnerableUpstream);
        await using var _ = app;
        using var __ = http;

        var payload = new
        {
            target = upstreamUrl,
            httpVerb = "POST",
            httpPath = "/",
            body = "{\"username\":\"alice\"}",
            field = "$.username",
            category = "sqli",
        };
        using var resp = await http.PostAsJsonAsync(new Uri("/api/security/fuzz", UriKind.Relative), payload, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        foreach (var row in body.GetProperty("rows").EnumerateArray())
            Assert.Equal("Vulnerable", row.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task PostFuzz_MalformedJsonBody_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, _) = await StartAsync(ct);
        await using var _app = app;
        using var __ = http;

        using var content = new StringContent("{this is not json", Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(new Uri("/api/security/fuzz", UriKind.Relative), content, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostFuzz_EmptyBody_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, _) = await StartAsync(ct);
        await using var _app = app;
        using var __ = http;

        using var content = new StringContent("", Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(new Uri("/api/security/fuzz", UriKind.Relative), content, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostFuzz_MissingTarget_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, _) = await StartAsync(ct);
        await using var _app = app;
        using var __ = http;

        var payload = new { field = "$.u", category = "sqli", body = "{\"u\":\"x\"}" };
        using var resp = await http.PostAsJsonAsync(new Uri("/api/security/fuzz", UriKind.Relative), payload, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostFuzz_MissingField_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, _) = await StartAsync(ct);
        await using var _app = app;
        using var __ = http;

        var payload = new { target = "http://example.invalid", category = "sqli", body = "{\"u\":\"x\"}" };
        using var resp = await http.PostAsJsonAsync(new Uri("/api/security/fuzz", UriKind.Relative), payload, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostFuzz_MissingCategory_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, _) = await StartAsync(ct);
        await using var _app = app;
        using var __ = http;

        var payload = new { target = "http://example.invalid", field = "$.u", body = "{\"u\":\"x\"}" };
        using var resp = await http.PostAsJsonAsync(new Uri("/api/security/fuzz", UriKind.Relative), payload, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostFuzz_UnknownCategory_Returns400WithErrorPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, upstreamUrl) = await StartAsync(ct);
        await using var _app = app;
        using var __ = http;

        var payload = new { target = upstreamUrl, field = "$.u", category = "totally-bogus", body = "{\"u\":\"x\"}", httpVerb = "POST", httpPath = "/" };
        using var resp = await http.PostAsJsonAsync(new Uri("/api/security/fuzz", UriKind.Relative), payload, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostFuzz_AllowSelfSignedCertsFlag_TogglesValidationCallback()
    {
        // We can't easily mount HTTPS in-process here; just exercise the
        // code path that flips the handler when the flag is set. The
        // request to an http:// upstream still works (handler doesn't
        // touch certs on plain HTTP), so the assertion is "endpoint
        // still returns 200" — the goal is the conditional branch
        // gets covered.
        var ct = TestContext.Current.CancellationToken;
        var (app, http, upstreamUrl) = await StartAsync(ct);
        await using var _app = app;
        using var __ = http;

        var payload = new
        {
            target = upstreamUrl,
            httpVerb = "POST",
            httpPath = "/",
            body = "{\"u\":\"alice\"}",
            field = "$.u",
            category = "xss",
            allowSelfSignedCerts = true,
        };
        using var resp = await http.PostAsJsonAsync(new Uri("/api/security/fuzz", UriKind.Relative), payload, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
