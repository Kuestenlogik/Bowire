// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the <c>bowire fuzz</c> CLI orchestrator. Drives the
/// argument-validation branches without network traffic, then a few
/// happy-path runs against an in-process Kestrel upstream.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
public sealed class FuzzCommandTests
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

    private static string WriteTemplate(BowireRecording rec)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bowire-fuzz-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(rec));
        return path;
    }

    private static BowireRecording MakeRecording(string body = "{\"username\":\"alice\"}", string verb = "POST", string path = "/login") => new()
    {
        Name = "fuzz-target",
        Steps =
        {
            new BowireRecordingStep
            {
                Protocol = "rest",
                Method = "login",
                HttpVerb = verb,
                HttpPath = path,
                Body = body,
            },
        },
    };

    [Fact]
    public async Task RunAsync_EmptyTarget_ReturnsUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _, stderr) = Capture(() => FuzzCommand.RunAsync(new FuzzOptions
        {
            Target = "", Template = "x.json", Field = "$.u", Category = "sqli",
        }, ct));
        Assert.Equal(2, code);
        Assert.Contains("Usage", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_TemplateFileMissing_ReturnsUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _, stderr) = Capture(() => FuzzCommand.RunAsync(new FuzzOptions
        {
            Target = "http://example.invalid", Template = "/no/such/path.json", Field = "$.u", Category = "sqli",
        }, ct));
        Assert.Equal(2, code);
        Assert.Contains("template", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_EmptyField_ReturnsUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteTemplate(MakeRecording());
        try
        {
            var (code, _, stderr) = Capture(() => FuzzCommand.RunAsync(new FuzzOptions
            {
                Target = "http://example.invalid", Template = path, Field = "", Category = "sqli",
            }, ct));
            Assert.Equal(2, code);
            Assert.Contains("--field", stderr, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_UnknownCategory_ReturnsUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteTemplate(MakeRecording());
        try
        {
            var (code, _, stderr) = Capture(() => FuzzCommand.RunAsync(new FuzzOptions
            {
                Target = "http://example.invalid", Template = path, Field = "$.username", Category = "bogus",
            }, ct));
            Assert.Equal(2, code);
            Assert.Contains("Unknown payload category", stderr, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_MalformedTemplate_ReturnsParseError()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"bowire-fuzz-test-bad-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{this is not json", ct);
        try
        {
            var (code, _, stderr) = Capture(() => FuzzCommand.RunAsync(new FuzzOptions
            {
                Target = "http://example.invalid", Template = path, Field = "$.u", Category = "sqli",
            }, ct));
            Assert.Equal(1, code);
            Assert.Contains("parse", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_RecordingWithNoSteps_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var rec = new BowireRecording { Name = "empty" }; // no Steps
        var path = WriteTemplate(rec);
        try
        {
            var (code, _, stderr) = Capture(() => FuzzCommand.RunAsync(new FuzzOptions
            {
                Target = "http://example.invalid", Template = path, Field = "$.u", Category = "sqli",
            }, ct));
            Assert.Equal(1, code);
            Assert.Contains("no steps", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_ProbeWithoutBody_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var rec = MakeRecording(body: "");
        var path = WriteTemplate(rec);
        try
        {
            var (code, _, stderr) = Capture(() => FuzzCommand.RunAsync(new FuzzOptions
            {
                Target = "http://example.invalid", Template = path, Field = "$.u", Category = "sqli",
            }, ct));
            Assert.Equal(1, code);
            Assert.Contains("no body", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_FieldPathNotInBody_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var rec = MakeRecording();
        var path = WriteTemplate(rec);
        try
        {
            var (code, _, stderr) = Capture(() => FuzzCommand.RunAsync(new FuzzOptions
            {
                Target = "http://example.invalid", Template = path, Field = "$.nonexistent", Category = "sqli",
            }, ct));
            Assert.Equal(1, code);
            Assert.Contains("not found", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_NumericFieldWithoutForce_SkipsWithExitZero()
    {
        var ct = TestContext.Current.CancellationToken;
        var rec = MakeRecording(body: "{\"limit\":10}");
        var path = WriteTemplate(rec);
        try
        {
            var (code, _, stderr) = Capture(() => FuzzCommand.RunAsync(new FuzzOptions
            {
                Target = "http://example.invalid", Template = path, Field = "$.limit", Category = "sqli",
            }, ct));
            Assert.Equal(0, code);
            Assert.Contains("Skipping", stderr, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_HappyPathAgainstBenignUpstream_ExitsClean()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var upstream = builder.Build();
        ((IApplicationBuilder)upstream).Run(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"ok\":true}", ctx.RequestAborted);
        });
        await upstream.StartAsync(ct);
        await using var _ = upstream;
        var upstreamUrl = upstream.Urls.First();

        var rec = MakeRecording();
        var path = WriteTemplate(rec);
        try
        {
            var (code, stdout, _) = Capture(() => FuzzCommand.RunAsync(new FuzzOptions
            {
                Target = upstreamUrl, Template = path, Field = "$.username", Category = "xss", TimeoutSeconds = 10,
            }, ct));
            Assert.Equal(0, code);
            Assert.Contains("Fuzzing", stdout, StringComparison.Ordinal);
            Assert.Contains("baseline:", stdout, StringComparison.Ordinal);
            Assert.Contains("No heuristics fired", stdout, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_VulnerableUpstream_ReturnsExit1()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var upstream = builder.Build();
        ((IApplicationBuilder)upstream).Run(async ctx =>
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync("Microsoft SQL Server: incorrect syntax near OR", ctx.RequestAborted);
        });
        await upstream.StartAsync(ct);
        await using var _ = upstream;

        var rec = MakeRecording();
        var path = WriteTemplate(rec);
        try
        {
            var (code, stdout, _) = Capture(() => FuzzCommand.RunAsync(new FuzzOptions
            {
                Target = upstream.Urls.First(), Template = path, Field = "$.username", Category = "sqli", TimeoutSeconds = 10,
            }, ct));
            Assert.Equal(1, code);
            Assert.Contains("[VULN]", stdout, StringComparison.Ordinal);
            Assert.Contains("suspicious", stdout, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_NullOptions_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await FuzzCommand.RunAsync(null!, ct));
    }
}
