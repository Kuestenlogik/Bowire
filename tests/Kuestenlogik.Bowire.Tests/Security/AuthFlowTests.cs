// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Security.Scanner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// #190 headless auth flow: variable/secret substitution, token extraction
/// (JSON path, regex, header, cookie), and an end-to-end login → token chain
/// against a loopback token endpoint whose token is then injected.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class AuthFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Parse_ReadsStepsAndInjectionDefaults()
    {
        var flow = AuthFlowRunner.Parse(
            """{ "grant": "client_credentials", "steps": [ { "url": "https://idp/token", "form": { "grant_type": "client_credentials" }, "capture": [ { "var": "access_token", "json": "access_token" } ] } ] }""");

        Assert.Equal("client_credentials", flow.Grant);
        Assert.Single(flow.Steps);
        Assert.Equal("Authorization", flow.InjectHeader);
        Assert.Equal("Bearer ", flow.InjectPrefix);
    }

    [Fact]
    public async Task Run_ClientCredentials_ExtractsAndInjectsBearer()
    {
        await using var idp = await StartAsync(Ct);
        using var http = new HttpClient();
        var flow = AuthFlowRunner.Parse(
            "{ \"steps\": [ { \"url\": \"" + idp.Urls.First() + "/token\", "
            + "\"form\": { \"grant_type\": \"client_credentials\", \"client_id\": \"svc\", \"client_secret\": \"{{env.BWR_TEST_SECRET}}\" }, "
            + "\"capture\": [ { \"var\": \"access_token\", \"json\": \"access_token\" } ] } ] }");

        Environment.SetEnvironmentVariable("BWR_TEST_SECRET", "s3cr3t");
        try
        {
            var result = await AuthFlowRunner.RunAsync(flow, http, Ct);
            Assert.Equal("tok-svc-s3cr3t", result.Token);
            Assert.Equal("Authorization: Bearer tok-svc-s3cr3t", result.HeaderLine);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BWR_TEST_SECRET", null);
        }
    }

    [Fact]
    public async Task Run_MissingSecretEnv_Throws()
    {
        using var http = new HttpClient();
        var flow = AuthFlowRunner.Parse(
            "{ \"steps\": [ { \"url\": \"https://idp/token\", \"form\": { \"s\": \"{{env.BWR_ABSENT_SECRET}}\" }, "
            + "\"capture\": [ { \"var\": \"access_token\", \"json\": \"access_token\" } ] } ] }");

        var ex = await Assert.ThrowsAsync<AuthFlowException>(() => AuthFlowRunner.RunAsync(flow, http, Ct));
        Assert.Contains("BWR_ABSENT_SECRET", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_LoginThenToken_ChainsCapturedVariable()
    {
        await using var idp = await StartAsync(Ct);
        using var http = new HttpClient();
        var b = idp.Urls.First();
        var flow = AuthFlowRunner.Parse(
            "{ \"steps\": [ "
            + "{ \"url\": \"" + b + "/login\", \"json\": \"{\\\"u\\\":\\\"bob\\\"}\", "
            + "\"capture\": [ { \"var\": \"sid\", \"cookie\": \"session\" } ] }, "
            + "{ \"url\": \"" + b + "/token\", \"method\": \"GET\", \"headers\": { \"X-Session\": \"{{sid}}\" }, "
            + "\"capture\": [ { \"var\": \"access_token\", \"header\": \"X-Token\" } ] } ] }");

        var result = await AuthFlowRunner.RunAsync(flow, http, Ct);
        // /token echoes the session it received back as the token header.
        Assert.Equal("Authorization: Bearer sess-abc", result.HeaderLine);
        Assert.Equal("sess-abc", result.Variables["sid"]);
    }

    [Theory]
    [InlineData("access_token")]
    [InlineData("$.access_token")]
    public async Task Run_JsonPathVariants_Resolve(string path)
    {
        await using var idp = await StartAsync(Ct);
        using var http = new HttpClient();
        var flow = AuthFlowRunner.Parse(
            "{ \"steps\": [ { \"url\": \"" + idp.Urls.First() + "/token\", "
            + "\"form\": { \"client_id\": \"svc\", \"client_secret\": \"x\" }, "
            + "\"capture\": [ { \"var\": \"access_token\", \"json\": \"" + path + "\" } ] } ] }");

        var result = await AuthFlowRunner.RunAsync(flow, http, Ct);
        Assert.StartsWith("tok-svc-", result.Token, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_NoTokenCaptured_Throws()
    {
        await using var idp = await StartAsync(Ct);
        using var http = new HttpClient();
        var flow = AuthFlowRunner.Parse(
            "{ \"steps\": [ { \"url\": \"" + idp.Urls.First() + "/token\", "
            + "\"form\": { \"client_id\": \"svc\", \"client_secret\": \"x\" }, "
            + "\"capture\": [ { \"var\": \"unrelated\", \"json\": \"token_type\" } ] } ] }");

        var ex = await Assert.ThrowsAsync<AuthFlowException>(() => AuthFlowRunner.RunAsync(flow, http, Ct));
        Assert.Contains("no recognisable token", ex.Message, StringComparison.Ordinal);
    }

    // Loopback IdP:
    //  POST /login  → Set-Cookie: session=sess-abc
    //  /token       → { access_token: "tok-<client_id>-<client_secret>", token_type: "Bearer" }
    //                 and echoes any X-Session header back as X-Token.
    private static async Task<WebApplication> StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();

        app.MapPost("/login", (HttpContext ctx) =>
        {
            ctx.Response.Headers.SetCookie = "session=sess-abc; Path=/; HttpOnly";
            return Results.Json(new { ok = true });
        });

        app.MapMethods("/token", ["GET", "POST"], async (HttpContext ctx) =>
        {
            if (ctx.Request.Headers.TryGetValue("X-Session", out var sess))
                ctx.Response.Headers["X-Token"] = sess.ToString();

            var clientId = "svc";
            var secret = "x";
            if (ctx.Request.HasFormContentType)
            {
                var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                if (form.TryGetValue("client_id", out var ci)) clientId = ci.ToString();
                if (form.TryGetValue("client_secret", out var cs)) secret = cs.ToString();
            }
            await ctx.Response.WriteAsJsonAsync(
                new { access_token = $"tok-{clientId}-{secret}", token_type = "Bearer", expires_in = 3600 },
                ctx.RequestAborted);
        });

        await app.StartAsync(ct);
        return app;
    }
}
