// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for <see cref="ScanCommand"/> — the <c>bowire scan</c> CLI
/// orchestrator. Drives the argument-validation branches without
/// network, then a few happy-path runs against an in-process Kestrel
/// upstream + temporary template JSON files.
/// </summary>
[Collection("ConsoleRedirect")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861:Prefer static readonly fields over constant array arguments", Justification = "Test scope — array allocations are negligible")]
public sealed class ScanCommandTests
{
    private static (int code, string stdout, string stderr) Capture(Func<Task<int>> action)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        using var sbOut = new StringWriter();
        using var sbErr = new StringWriter();
        Console.SetOut(sbOut);
        Console.SetError(sbErr);
        try
        {
            var code = action().GetAwaiter().GetResult();
            return (code, sbOut.ToString(), sbErr.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    private static BowireRecording AttackRecording(int expectedStatus = 200, string? bodyContains = null) => new()
    {
        Name = "test-attack",
        Attack = true,
        Vulnerability = new AttackVulnerability { Id = "BWR-T-001", Severity = "high" },
        VulnerableWhen = new AttackPredicate { Status = expectedStatus, BodyContains = bodyContains },
        Steps =
        {
            new BowireRecordingStep
            {
                Protocol = "rest",
                HttpVerb = "GET",
                HttpPath = "/probe",
            },
        },
    };

    private static async Task<string> WriteAsync(BowireRecording rec, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bowire-scan-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(rec), ct);
        return path;
    }

    // ---------------- arg validation ----------------

    [Fact]
    public async Task RunAsync_EmptyTarget_ReturnsUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _, stderr) = Capture(() => ScanCommand.RunAsync(new ScanOptions(), ct));
        Assert.Equal(2, code);
        Assert.Contains("Usage", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoTemplatesAndNoBuiltins_ReturnsUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _, stderr) = Capture(() => ScanCommand.RunAsync(new ScanOptions
        {
            Target = "https://example.invalid",
            RunBuiltins = false,
        }, ct));
        Assert.Equal(2, code);
        Assert.Contains("No vulnerability templates", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MalformedTargetUrl_ReturnsUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await WriteAsync(AttackRecording(), ct);
        try
        {
            var (code, _, stderr) = Capture(() => ScanCommand.RunAsync(new ScanOptions
            {
                Target = "not a url",
                Template = path,
                RunBuiltins = false,
            }, ct));
            Assert.Equal(2, code);
            Assert.Contains("Could not parse --target", stderr, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_TargetOutsideScope_RefusesToScan()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await WriteAsync(AttackRecording(), ct);
        try
        {
            var (code, _, stderr) = Capture(() => ScanCommand.RunAsync(new ScanOptions
            {
                Target = "https://other.example.com",
                Template = path,
                Scope = new[] { "api.example.com" },
                RunBuiltins = false,
            }, ct));
            Assert.Equal(2, code);
            Assert.Contains("outside the configured --scope", stderr, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_NullOptions_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await ScanCommand.RunAsync(null!, ct));
    }

    // ---------------- CompileScope helper ----------------

    [Fact]
    public void CompileScope_EmptyList_DerivesFromTargetHost()
    {
        var pred = ScanCommand.CompileScope(new List<string>(), "https://api.example.com/foo");
        Assert.True(pred("api.example.com"));
        Assert.False(pred("other.example.com"));
    }

    [Fact]
    public void CompileScope_LiteralHostname_ExactMatch()
    {
        var pred = ScanCommand.CompileScope(new[] { "a.com" }, "");
        Assert.True(pred("a.com"));
        Assert.True(pred("A.COM"));   // case-insensitive
        Assert.False(pred("api.a.com"));
    }

    [Fact]
    public void CompileScope_WildcardSuffix_MatchesSubdomainsNotApex()
    {
        var pred = ScanCommand.CompileScope(new[] { "*.example.com" }, "");
        Assert.True(pred("api.example.com"));
        Assert.True(pred("a.b.example.com"));
        Assert.False(pred("example.com"));        // apex doesn't match
        Assert.False(pred("other.invalid"));
    }

    [Fact]
    public void CompileScope_CommaSeparated_SplitsIntoMultiplePatterns()
    {
        var pred = ScanCommand.CompileScope(new[] { "a.com,b.com" }, "");
        Assert.True(pred("a.com"));
        Assert.True(pred("b.com"));
        Assert.False(pred("c.com"));
    }

    [Fact]
    public void CompileScope_EmptyPatternsAndUnparsableTarget_AllowsEverything()
    {
        var pred = ScanCommand.CompileScope(new List<string>(), "::not a url::");
        Assert.True(pred("anything"));
    }

    // ---------------- ApplyAuthHeaders helper ----------------

    [Fact]
    public void ApplyAuthHeaders_NullOrEmpty_NoOp()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://x.invalid");
        ScanCommand.ApplyAuthHeaders(req, headers: null!);
        ScanCommand.ApplyAuthHeaders(req, headers: new List<string>());
        Assert.Empty(req.Headers);
    }

    [Fact]
    public void ApplyAuthHeaders_BearerAndApiKey_SetOnRequest()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://x.invalid");
        ScanCommand.ApplyAuthHeaders(req, new[] { "Authorization: Bearer abc", "X-Api-Key: xyz" });
        Assert.Equal("Bearer abc", req.Headers.GetValues("Authorization").Single());
        Assert.Equal("xyz", req.Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public void ApplyAuthHeaders_MissingColonOrEmptyName_SilentlyDropped()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://x.invalid");
        ScanCommand.ApplyAuthHeaders(req, new[] { "no-colon", " : value", "" });
        Assert.Empty(req.Headers);
    }

    // ---------------- happy path via in-process upstream ----------------

    [Fact]
    public async Task RunAsync_TemplateMatchesUpstream_ReportsVulnerableAndReturns1()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);

        // Template expects status 200; upstream returns 200 → match.
        var path = await WriteAsync(AttackRecording(expectedStatus: 200), ct);
        try
        {
            var (code, stdout, _) = Capture(() => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Template = path,
                RunBuiltins = false,
                TimeoutSeconds = 10,
            }, ct));
            Assert.Equal(1, code);
            Assert.Contains("[VULN]", stdout, StringComparison.Ordinal);
            Assert.Contains("BWR-T-001", stdout, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_TemplateDoesNotMatch_ReportsSafeAndReturns0()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);

        // Template expects 418 (teapot); upstream returns 200 → no match.
        var path = await WriteAsync(AttackRecording(expectedStatus: 418), ct);
        try
        {
            var (code, stdout, _) = Capture(() => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Template = path,
                RunBuiltins = false,
                TimeoutSeconds = 10,
            }, ct));
            Assert.Equal(0, code);
            Assert.Contains("[ok]", stdout, StringComparison.Ordinal);
            Assert.Contains("No vulnerabilities matched", stdout, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_SeverityBelowThreshold_ReportsAsSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);

        var rec = AttackRecording();
        rec.Vulnerability!.Severity = "low";
        var path = await WriteAsync(rec, ct);
        try
        {
            var (code, stdout, _) = Capture(() => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Template = path,
                RunBuiltins = false,
                MinSeverity = "high",
            }, ct));
            Assert.Equal(0, code);
            Assert.Contains("[skip]", stdout, StringComparison.Ordinal);
            Assert.Contains("below severity threshold", stdout, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_NonHttpProtocolTemplate_ReportsAsSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);

        var rec = AttackRecording();
        rec.Steps[0].Protocol = "grpc"; // not yet supported by scanner v1
        var path = await WriteAsync(rec, ct);
        try
        {
            var (code, stdout, _) = Capture(() => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Template = path,
                RunBuiltins = false,
            }, ct));
            Assert.Equal(0, code);
            Assert.Contains("not yet supported by scanner", stdout, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_TargetUnreachable_RecordsErrorFinding()
    {
        var ct = TestContext.Current.CancellationToken;
        // Target is parseable but unreachable (port 1 won't bind on the test host).
        var path = await WriteAsync(AttackRecording(), ct);
        try
        {
            var (code, stdout, _) = Capture(() => ScanCommand.RunAsync(new ScanOptions
            {
                Target = "http://127.0.0.1:1",
                Template = path,
                RunBuiltins = false,
                TimeoutSeconds = 2,
            }, ct));
            // Either error or 0 depending on platform — the value we
            // really care about is the stdout error marker.
            Assert.True(code is 0 or 1);
            Assert.Contains("[err]", stdout, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_CorpusDirectoryWithMultipleTemplates_LoadsAndRunsAll()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);

        var corpusDir = Path.Combine(Path.GetTempPath(), $"bowire-corpus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(corpusDir);
        try
        {
            var rec1 = AttackRecording(expectedStatus: 200);
            var rec2 = AttackRecording(expectedStatus: 418);
            await File.WriteAllTextAsync(Path.Combine(corpusDir, "a.json"), JsonSerializer.Serialize(rec1), ct);
            await File.WriteAllTextAsync(Path.Combine(corpusDir, "b.json"), JsonSerializer.Serialize(rec2), ct);

            var (code, stdout, _) = Capture(() => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Corpus = corpusDir,
                RunBuiltins = false,
                TimeoutSeconds = 10,
            }, ct));
            Assert.Equal(1, code);
            Assert.Contains("[VULN]", stdout, StringComparison.Ordinal);
            Assert.Contains("[ok]", stdout, StringComparison.Ordinal);
        }
        finally { Directory.Delete(corpusDir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_SarifOutput_WritesFile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);
        var template = await WriteAsync(AttackRecording(), ct);
        var sarifPath = Path.Combine(Path.GetTempPath(), $"bowire-scan-{Guid.NewGuid():N}.sarif");
        try
        {
            var (code, _, _) = Capture(() => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Template = template,
                OutSarif = sarifPath,
                RunBuiltins = false,
                TimeoutSeconds = 10,
            }, ct));
            Assert.Equal(1, code);
            Assert.True(File.Exists(sarifPath));
            var sarif = await File.ReadAllTextAsync(sarifPath, ct);
            Assert.Contains("\"BWR-T-001\"", sarif, StringComparison.Ordinal);
            Assert.Contains("\"2.1.0\"", sarif, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(template);
            if (File.Exists(sarifPath)) File.Delete(sarifPath);
        }
    }

    private static async Task<WebApplication> StartUpstreamAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        ((IApplicationBuilder)app).Run(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"hello\":\"world\"}", ctx.RequestAborted);
        });
        await app.StartAsync(ct);
        return app;
    }
}
