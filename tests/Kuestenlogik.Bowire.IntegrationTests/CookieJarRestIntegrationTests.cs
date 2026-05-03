// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Rest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end test for the per-env cookie jar: a Kestrel host with two
/// endpoints, <c>POST /login</c> setting a session cookie and <c>GET /me</c>
/// reading it. Two RestInvoker calls in sequence with the same envId on
/// the <c>__bowireCookieEnv__</c> marker — the second call sees the
/// cookie the first one received via Set-Cookie.
/// </summary>
public class CookieJarRestIntegrationTests
{
    [Fact]
    public async Task Login_Then_Me_PersistsSessionCookieAcrossCalls()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();

        await using var app = builder.Build();
        app.MapPost("/login", (HttpContext ctx) =>
        {
            // Test fixture runs against http://127.0.0.1:port — Secure=true
            // would suppress the cookie, defeating the round-trip test.
            // lgtm[cs/web/cookie-secure-not-set]
            ctx.Response.Cookies.Append("session", "alice-token", new CookieOptions
            {
                Path = "/",
                HttpOnly = true,
                Secure = false,
                SameSite = SameSiteMode.Lax
            });
            return Results.Json(new { ok = true });
        });
        app.MapGet("/me", (HttpContext ctx) =>
        {
            var session = ctx.Request.Cookies.TryGetValue("session", out var s) ? s : null;
            return Results.Json(new { session, hasSession = session is not null });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Use a unique envId so concurrent test runs don't share jar state.
        var envId = "cookietest-" + Guid.NewGuid().ToString("N");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            var loginInfo = new BowireMethodInfo(
                Name: "Login",
                FullName: "POST /login",
                ClientStreaming: false, ServerStreaming: false,
                InputType: new BowireMessageInfo("LoginRequest", "LoginRequest", []),
                OutputType: new BowireMessageInfo("LoginResponse", "LoginResponse", []),
                MethodType: "Unary")
            {
                HttpMethod = "POST",
                HttpPath = "/login"
            };

            var meInfo = new BowireMethodInfo(
                Name: "Me",
                FullName: "GET /me",
                ClientStreaming: false, ServerStreaming: false,
                InputType: new BowireMessageInfo("MeRequest", "MeRequest", []),
                OutputType: new BowireMessageInfo("MeResponse", "MeResponse", []),
                MethodType: "Unary")
            {
                HttpMethod = "GET",
                HttpPath = "/me"
            };

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CookieJar.MarkerKey] = envId
            };

            // 1. POST /login — server returns a Set-Cookie; the per-env
            //    CookieContainer captures it.
            var loginResult = await RestInvoker.InvokeAsync(
                http, url, loginInfo,
                jsonMessages: ["{}"],
                requestMetadata: metadata,
                ct: TestContext.Current.CancellationToken);
            Assert.Equal("OK", loginResult.Status);

            // 2. GET /me — same envId on the marker; the cookie rides along
            //    on the request, server reads "session=alice-token".
            var meResult = await RestInvoker.InvokeAsync(
                http, url, meInfo,
                jsonMessages: ["{}"],
                requestMetadata: metadata,
                ct: TestContext.Current.CancellationToken);
            Assert.Equal("OK", meResult.Status);
            Assert.NotNull(meResult.Response);
            Assert.Contains("\"session\":\"alice-token\"", meResult.Response!, StringComparison.Ordinal);
            Assert.Contains("\"hasSession\":true", meResult.Response!, StringComparison.Ordinal);

            // 3. Snapshot inspection — the helper exposes what's on disk
            //    so the workbench can render the jar contents.
            var snapshot = CookieJar.Snapshot(envId);
            Assert.Single(snapshot);
            Assert.Equal("session", snapshot[0].Name);
            Assert.Equal("alice-token", snapshot[0].Value);

            // 4. Clear — third request without the cookie should see no session.
            CookieJar.Clear(envId);
            var meResult2 = await RestInvoker.InvokeAsync(
                http, url, meInfo,
                jsonMessages: ["{}"],
                requestMetadata: metadata,
                ct: TestContext.Current.CancellationToken);
            Assert.Contains("\"hasSession\":false", meResult2.Response!, StringComparison.Ordinal);
        }
        finally
        {
            CookieJar.Clear(envId);
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DifferentEnvs_DoNotShareCookies()
    {
        // Two envs, two parallel sessions. Setting a cookie in env A must
        // not bleed into env B's jar — that's the whole reason the marker
        // key is per-env, not global.
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();

        await using var app = builder.Build();
        app.MapPost("/login", (HttpContext ctx) =>
        {
            // Test fixture runs against http:// — Secure/HttpOnly defaults
            // are intentional so the cookie round-trips through the test
            // HttpClient. lgtm[cs/web/cookie-secure-not-set]
            // lgtm[cs/web/cookie-httponly-not-set]
            ctx.Response.Cookies.Append("session", "env-a-only", new CookieOptions { Path = "/" });
            return Results.Json(new { ok = true });
        });
        app.MapGet("/me", (HttpContext ctx) =>
        {
            var session = ctx.Request.Cookies.TryGetValue("session", out var s) ? s : null;
            return Results.Json(new { hasSession = session is not null });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var envA = "envA-" + Guid.NewGuid().ToString("N");
        var envB = "envB-" + Guid.NewGuid().ToString("N");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var loginInfo = new BowireMethodInfo(
                "Login", "POST /login", false, false,
                new BowireMessageInfo("Req", "Req", []),
                new BowireMessageInfo("Res", "Res", []),
                "Unary")
            { HttpMethod = "POST", HttpPath = "/login" };
            var meInfo = new BowireMethodInfo(
                "Me", "GET /me", false, false,
                new BowireMessageInfo("Req", "Req", []),
                new BowireMessageInfo("Res", "Res", []),
                "Unary")
            { HttpMethod = "GET", HttpPath = "/me" };

            // Login under env A only.
            await RestInvoker.InvokeAsync(http, url, loginInfo, ["{}"],
                new Dictionary<string, string>(StringComparer.Ordinal) { [CookieJar.MarkerKey] = envA },
                TestContext.Current.CancellationToken);

            // env A sees the cookie...
            var meA = await RestInvoker.InvokeAsync(http, url, meInfo, ["{}"],
                new Dictionary<string, string>(StringComparer.Ordinal) { [CookieJar.MarkerKey] = envA },
                TestContext.Current.CancellationToken);
            Assert.Contains("\"hasSession\":true", meA.Response!, StringComparison.Ordinal);

            // ...env B does not.
            var meB = await RestInvoker.InvokeAsync(http, url, meInfo, ["{}"],
                new Dictionary<string, string>(StringComparer.Ordinal) { [CookieJar.MarkerKey] = envB },
                TestContext.Current.CancellationToken);
            Assert.Contains("\"hasSession\":false", meB.Response!, StringComparison.Ordinal);
        }
        finally
        {
            CookieJar.Clear(envA);
            CookieJar.Clear(envB);
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
