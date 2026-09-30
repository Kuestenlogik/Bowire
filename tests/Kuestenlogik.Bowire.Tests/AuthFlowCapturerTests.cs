// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #563: the Scanner-backed <see cref="AuthFlowCapturer"/> translates a
/// misconfigured / failing flow into a Core-visible
/// <see cref="AuthFlowCaptureException"/> (so the auth-recording endpoint can
/// surface a clean error without referencing the Scanner sibling), and maps a
/// successful flow's inject prefix onto the recording's scheme. The
/// error-boundary tests run with no network (they fail before any request is
/// sent); the success tests drive a loopback token endpoint end-to-end.
/// </summary>
public sealed class AuthFlowCapturerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Malformed_Flow_Json_Throws_AuthFlowCaptureException()
    {
        await Assert.ThrowsAsync<AuthFlowCaptureException>(() =>
            new AuthFlowCapturer().CaptureAsync("not json at all", Ct));
    }

    [Fact]
    public async Task Flow_With_No_Steps_Throws_AuthFlowCaptureException()
    {
        // Valid JSON, but the flow yields no token → the adapter wraps the
        // AuthFlowException as a Core-visible AuthFlowCaptureException. No
        // request is sent (it fails before the first step).
        await Assert.ThrowsAsync<AuthFlowCaptureException>(() =>
            new AuthFlowCapturer().CaptureAsync("""{"steps":[]}""", Ct));
    }

    [Theory]
    [InlineData("Bearer ", "bearer")]   // default prefix → bearer
    [InlineData("Basic ", "basic")]
    [InlineData("", "apikey")]          // no prefix → a raw api-key header
    public async Task Successful_Flow_Maps_InjectPrefix_To_Scheme(string injectPrefix, string expectedScheme)
    {
        await using var idp = await StartTokenEndpointAsync(Ct);
        var flow =
            "{ \"injectHeader\": \"X-Api-Key\", \"injectPrefix\": \"" + injectPrefix + "\", "
            + "\"steps\": [ { \"url\": \"" + idp.Urls.First() + "/token\", "
            + "\"form\": { \"client_id\": \"svc\", \"client_secret\": \"x\" }, "
            + "\"capture\": [ { \"var\": \"access_token\", \"json\": \"access_token\" } ] } ] }";

        var result = await new AuthFlowCapturer().CaptureAsync(flow, Ct);

        Assert.Equal("tok-svc-x", result.Credential);
        Assert.Equal(expectedScheme, result.Scheme);
        Assert.Equal("X-Api-Key", result.Header);   // the flow's inject header rides through
    }

    [Fact]
    public async Task Empty_Captured_Token_Fails_Closed()
    {
        // The endpoint returns an empty access_token; the adapter must never hand
        // back a blank credential (it would arm #562's gate presence-only).
        await using var idp = await StartTokenEndpointAsync(Ct, emptyToken: true);
        var flow =
            "{ \"steps\": [ { \"url\": \"" + idp.Urls.First() + "/token\", "
            + "\"capture\": [ { \"var\": \"access_token\", \"json\": \"access_token\" } ] } ] }";

        await Assert.ThrowsAsync<AuthFlowCaptureException>(() =>
            new AuthFlowCapturer().CaptureAsync(flow, Ct));
    }

    // ---- restricted capture (LockServerUrl / AllowedAuthUrls) ----

    [Fact]
    public async Task Restricted_Capture_Refuses_A_Step_Outside_The_Allowlist_Before_Sending()
    {
        // The guard sits on the wire, so the refusal comes before any socket
        // is opened — the metadata address is never dialed.
        const string flow = """{ "steps": [ { "url": "http://169.254.169.254/latest/meta-data" } ] }""";

        var ex = await Assert.ThrowsAsync<AuthFlowCaptureException>(() =>
            new AuthFlowCapturer().CaptureAsync(flow, _ => false, Ct));

        var refused = Assert.IsType<BowireTargetRefusedException>(ex.InnerException);
        Assert.Equal("http://169.254.169.254/latest/meta-data", refused.Target);
    }

    [Fact]
    public async Task Restricted_Capture_Runs_Steps_The_Allowlist_Covers()
    {
        await using var idp = await StartTokenEndpointAsync(Ct);
        var authority = new Uri(idp.Urls.First()).Authority;
        var flow = TokenFlow(idp.Urls.First() + "/token");

        var result = await new AuthFlowCapturer().CaptureAsync(flow, uri => uri.Authority == authority, Ct);

        Assert.Equal("tok-svc-x", result.Credential);
    }

    [Fact]
    public async Task Restricted_Capture_Does_Not_Follow_Redirects_Past_The_Guard()
    {
        // /hop redirects to /token. Only /hop is allowed: a followed redirect
        // would be resolved below the guard and fetch the token anyway, so
        // the capture succeeding would mean the guard was bypassed.
        await using var idp = await StartTokenEndpointAsync(Ct);
        var flow = TokenFlow(idp.Urls.First() + "/hop");

        await Assert.ThrowsAsync<AuthFlowCaptureException>(() =>
            new AuthFlowCapturer().CaptureAsync(flow, uri => uri.AbsolutePath == "/hop", Ct));
    }

    [Fact]
    public async Task Unrestricted_Capture_Still_Follows_Redirects()
    {
        await using var idp = await StartTokenEndpointAsync(Ct);
        var flow = TokenFlow(idp.Urls.First() + "/hop");

        var result = await new AuthFlowCapturer().CaptureAsync(flow, Ct);

        Assert.Equal("tok-svc-x", result.Credential);
    }

    private static string TokenFlow(string url) =>
        "{ \"steps\": [ { \"url\": \"" + url + "\", "
        + "\"capture\": [ { \"var\": \"access_token\", \"json\": \"access_token\" } ] } ] }";

    // Loopback token endpoint: /token → { access_token: "tok-<client_id>-<client_secret>" }
    // (or an empty token when emptyToken is set, to exercise the fail-closed guard).
    private static async Task<WebApplication> StartTokenEndpointAsync(CancellationToken ct, bool emptyToken = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();

        app.MapMethods("/token", ["GET", "POST"], async (HttpContext ctx) =>
        {
            var clientId = "svc";
            var secret = "x";
            if (ctx.Request.HasFormContentType)
            {
                var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                if (form.TryGetValue("client_id", out var ci)) clientId = ci.ToString();
                if (form.TryGetValue("client_secret", out var cs)) secret = cs.ToString();
            }
            await ctx.Response.WriteAsJsonAsync(
                new { access_token = emptyToken ? "" : $"tok-{clientId}-{secret}", token_type = "Bearer" },
                ctx.RequestAborted);
        });

        // /hop → 302 to /token, for the redirect tests.
        app.MapPost("/hop", (HttpContext ctx) =>
        {
            ctx.Response.Redirect("/token");
            return Task.CompletedTask;
        });

        await app.StartAsync(ct);
        return app;
    }
}
