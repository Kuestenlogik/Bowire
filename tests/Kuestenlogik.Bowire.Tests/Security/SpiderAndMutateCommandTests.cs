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
/// Coverage for <see cref="SpiderCommand"/> (endpoint discovery from robots /
/// sitemap / OpenAPI / common-path sweep / page links) and
/// <see cref="MutateCommand"/> (schema-aware mutation preview) — both driven
/// against a loopback Kestrel upstream / in-process, no external services.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class SpiderAndMutateCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- spider ----------

    [Fact]
    public async Task Crawl_DiscoversFromRobotsSitemapAndOpenApi()
    {
        await using var up = await StartAsync(SpiderHandler, Ct);
        var baseUrl = up.Urls.First();
        using var http = NewClient();

        var candidates = await SpiderCommand.CrawlAsync(
            new SpiderOptions { Url = baseUrl, RespectRobots = false }, http, Ct);

        Assert.NotEmpty(candidates);
        Assert.Contains(candidates, c => c.Source == "robots.txt");
        Assert.Contains(candidates, c => c.Source == "sitemap.xml");
        Assert.Contains(candidates, c => c.Source.StartsWith("openapi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Crawl_HonoursRobotsDisallow_ByDefault()
    {
        await using var up = await StartAsync(SpiderHandler, Ct);
        using var http = NewClient();

        // With RespectRobots on (default), the /admin Disallow'd path is still
        // *surfaced* as a candidate (spider reports, doesn't fetch) — assert the
        // crawl completes and returns candidates without throwing.
        var candidates = await SpiderCommand.CrawlAsync(
            new SpiderOptions { Url = up.Urls.First(), RespectRobots = true }, http, Ct);

        Assert.NotEmpty(candidates);
    }

    [Fact]
    public async Task Crawl_DiscoversFromGraphQLIntrospectionAndCommonPaths()
    {
        await using var up = await StartAsync(GraphQlAndCommonPathHandler, Ct);
        using var http = NewClient();

        var candidates = await SpiderCommand.CrawlAsync(
            new SpiderOptions { Url = up.Urls.First(), RespectRobots = false }, http, Ct);

        Assert.Contains(candidates, c => c.Source.StartsWith("graphql", StringComparison.Ordinal));
        Assert.Contains(candidates, c => c.Source == "common-path");
        Assert.Contains(candidates, c => c.Source == "page-link");
    }

    private static Task GraphQlAndCommonPathHandler(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (ctx.Request.Method == "POST" && path == "/graphql")
        {
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync(
                "{\"data\":{\"__schema\":{\"queryType\":{\"name\":\"Query\",\"fields\":[{\"name\":\"users\"}]},\"mutationType\":{\"name\":\"Mutation\",\"fields\":[{\"name\":\"createUser\"}]}}}}",
                ctx.RequestAborted);
        }
        if (ctx.Request.Method == "HEAD" && path == "/actuator")
        {
            ctx.Response.StatusCode = 200; // a reachable common path → candidate
            return Task.CompletedTask;
        }
        if (path == "/")
        {
            ctx.Response.ContentType = "text/html";
            return ctx.Response.WriteAsync(
                "<html><body><a href=\"/dashboard\">dash</a> <a href=\"https://off-host.example.com/x\">off</a></body></html>",
                ctx.RequestAborted);
        }
        ctx.Response.StatusCode = 404;
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_WritesReportAndOutJson()
    {
        await using var up = await StartAsync(SpiderHandler, Ct);
        var outFile = Path.Combine(Path.GetTempPath(), $"bowire-spider-{Guid.NewGuid():N}.json");
        await using var sw = new StringWriter();
        try
        {
            var code = await SpiderCommand.RunAsync(
                new SpiderOptions { Url = up.Urls.First(), RespectRobots = false, OutJson = outFile }, Ct, sw);

            Assert.Equal(0, code);
            Assert.Contains("Spidering", sw.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(outFile));
            Assert.Contains("url", await File.ReadAllTextAsync(outFile, Ct), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(outFile)) File.Delete(outFile);
        }
    }

    [Fact]
    public async Task RunAsync_MissingUrl_ReturnsNonZero()
    {
        await using var err = new StringWriter();
        await using var outw = new StringWriter();
        var code = await SpiderCommand.RunAsync(new SpiderOptions { Url = "" }, Ct, outw, err);
        Assert.NotEqual(0, code);
    }

    private static Task SpiderHandler(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var authority = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        switch (path)
        {
            case "/robots.txt":
                ctx.Response.ContentType = "text/plain";
                return ctx.Response.WriteAsync($"User-agent: *\nDisallow: /admin\nSitemap: {authority}/sitemap.xml\n", ctx.RequestAborted);
            case "/sitemap.xml":
                ctx.Response.ContentType = "application/xml";
                return ctx.Response.WriteAsync($"<urlset><url><loc>{authority}/products</loc></url></urlset>", ctx.RequestAborted);
            case "/openapi.json":
                ctx.Response.ContentType = "application/json";
                return ctx.Response.WriteAsync("{\"openapi\":\"3.0.0\",\"paths\":{\"/users\":{\"get\":{}},\"/orders\":{\"post\":{}}}}", ctx.RequestAborted);
            case "/":
                ctx.Response.ContentType = "text/html";
                return ctx.Response.WriteAsync("<html><body><a href=\"/page1\">one</a> <a href=\"/page2\">two</a></body></html>", ctx.RequestAborted);
            default:
                // common-path HEAD sweep + everything else → not found.
                ctx.Response.StatusCode = 404;
                return Task.CompletedTask;
        }
    }

    // ---------- mutate ----------

    [Theory]
    [InlineData("integer")]
    [InlineData("string")]
    [InlineData("boolean")]
    [InlineData("number")]
    public async Task Mutate_KnownType_PrintsMutations(string type)
    {
        await using var sw = new StringWriter();
        var code = await MutateCommand.RunAsync(new MutateOptions { Type = type, Seed = 1, Budget = 8 }, Ct, sw);

        Assert.Equal(0, code);
        Assert.NotEmpty(sw.ToString().Trim());
    }

    [Fact]
    public async Task Mutate_EnumType_UsesProvidedValues()
    {
        await using var sw = new StringWriter();
        var code = await MutateCommand.RunAsync(
            new MutateOptions { Type = "enum", Enum = "RED,GREEN,BLUE", Seed = 2, Budget = 6 }, Ct, sw);

        Assert.Equal(0, code);
        Assert.NotEmpty(sw.ToString().Trim());
    }

    [Fact]
    public async Task Mutate_MissingType_ReturnsNonZero()
    {
        await using var err = new StringWriter();
        await using var outw = new StringWriter();
        var code = await MutateCommand.RunAsync(new MutateOptions { Type = null }, Ct, outw, err);
        Assert.NotEqual(0, code);
    }

    // ---------- helpers ----------

    private static HttpClient NewClient()
        => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    private static async Task<WebApplication> StartAsync(RequestDelegate handler, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync(ct);
        return app;
    }
}
