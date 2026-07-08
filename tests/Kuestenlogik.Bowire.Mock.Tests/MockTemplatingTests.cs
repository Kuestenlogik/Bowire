// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Mock.Replay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// #406: richer response templating — ${faker.*} generators, bodyFileName
/// (file-backed response body), and the response-transformer hook.
/// </summary>
public sealed class MockTemplatingTests : IDisposable
{
    private readonly string _tempDir;

    public MockTemplatingTests()
    {
        _tempDir = SafePath.Combine(Path.GetTempPath(), "bowire-tmpl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ---- faker units ----

    [Fact]
    public void Faker_Generators_ProduceExpectedShapes()
    {
        Assert.Contains("@example.com", MockFaker.Generate("email")!, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", MockFaker.Generate("firstName")!, StringComparison.Ordinal);
        Assert.Contains(' ', MockFaker.Generate("name")!); // first + last
        Assert.True(Guid.TryParse(MockFaker.Generate("uuid"), out _));
        Assert.Equal("5", MockFaker.Generate("int(5,5)"));
        Assert.InRange(int.Parse(MockFaker.Generate("int(1,3)")!, System.Globalization.CultureInfo.InvariantCulture), 1, 3);
        Assert.Equal(3, MockFaker.Generate("lorem(3)")!.Split(' ').Length);
        Assert.True(MockFaker.Generate("bool") is "true" or "false");
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", MockFaker.Generate("date")!);
        // Unknown spec → null (caller keeps the literal).
        Assert.Null(MockFaker.Generate("nope"));
    }

    [Fact]
    public void Substitutor_ResolvesFakerTokens_CanonicalCurly()
    {
        var result = ResponseBodySubstitutor.Substitute("""{"user":"{{faker.name}}","age":{{faker.int(18,18)}}}""");
        Assert.DoesNotContain("{{", result, StringComparison.Ordinal);
        Assert.Contains("\"age\":18", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Substitutor_LeafTokens_Curly()
    {
        Assert.True(Guid.TryParse(ResponseBodySubstitutor.Substitute("{{uuid}}"), out _));
        Assert.Matches(@"^\d+$", ResponseBodySubstitutor.Substitute("{{now}}"));
        // Unknown token stays literal.
        Assert.Equal("{{nope}}", ResponseBodySubstitutor.Substitute("{{nope}}"));
    }

    [Theory]
    [InlineData("{{math:2 + 3 * 4}}", "14")]
    [InlineData("{{math:(2 + 3) * 4}}", "20")]
    [InlineData("{{math:10 / 4}}", "2.5")]
    [InlineData("{{math:7 % 3}}", "1")]
    [InlineData("{{math:{{now}} - {{now}}}}", "0")] // nested tokens resolve first
    public void Substitutor_MathHelper(string input, string expected)
        => Assert.Equal(expected, ResponseBodySubstitutor.Substitute(input));

    [Theory]
    [InlineData("{{if:1 == 1 ? yes : no}}", "yes")]
    [InlineData("{{if:2 < 1 ? yes : no}}", "no")]
    [InlineData("{{if:a == a ? same : diff}}", "same")]
    [InlineData("{{if:true ? on : off}}", "on")]
    [InlineData("{{if:false ? on : off}}", "off")]
    public void Substitutor_IfHelper(string input, string expected)
        => Assert.Equal(expected, ResponseBodySubstitutor.Substitute(input));

    [Fact]
    public void Substitutor_FormatHelpers()
    {
        Assert.Equal("HELLO", ResponseBodySubstitutor.Substitute("{{upper:hello}}"));
        Assert.Equal("hello", ResponseBodySubstitutor.Substitute("{{lower:HELLO}}"));
        Assert.Equal("fallback", ResponseBodySubstitutor.Substitute("{{default:|fallback}}"));
        Assert.Equal("primary", ResponseBodySubstitutor.Substitute("{{default:primary|fallback}}"));
    }

    [Fact]
    public void Substitutor_Escape_KeepsLiteral()
    {
        Assert.Equal("{{uuid}}", ResponseBodySubstitutor.Substitute("{{{{uuid}}}}"));
    }

    [Fact]
    public void Substitutor_DeprecatedDollarSyntax_StillResolves()
    {
        // ${…} is deprecated but kept working for pre-{{}} recordings.
        Assert.True(Guid.TryParse(ResponseBodySubstitutor.Substitute("${uuid}"), out _));
        // Helpers are {{…}}-exclusive — a ${math:…} stays literal.
        Assert.Equal("${math:1+1}", ResponseBodySubstitutor.Substitute("${math:1+1}"));
    }

    // ---- bodyFileName + transformer (e2e) ----

    [Fact]
    public async Task BodyFileName_ServesFileContents_WithSubstitution()
    {
        var bodyFile = SafePath.Combine(_tempDir, "resp.json");
        await File.WriteAllTextAsync(bodyFile, """{"from":"file","id":"{{uuid}}"}""", TestContext.Current.CancellationToken);

        var rec = new BowireRecording
        {
            Id = "r", Name = "r", RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "s", Protocol = "rest", Service = "S", Method = "M", MethodType = "Unary",
                    HttpPath = "/f", HttpVerb = "GET", Status = "OK",
                    Response = """{"inline":true}""", ResponseBodyFile = "resp.json",
                },
            },
        };

        using var host = BuildHost(rec, opts => opts.RecordingDirectory = _tempDir);
        var resp = await host.GetTestClient().GetAsync(new Uri("/f", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("\"from\":\"file\"", body, StringComparison.Ordinal); // file body, not inline
        Assert.DoesNotContain("{{uuid}}", body, StringComparison.Ordinal);    // substitution applied
        Assert.DoesNotContain("inline", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseTransformer_MutatesFinalBody()
    {
        var rec = new BowireRecording
        {
            Id = "r", Name = "r", RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "s", Protocol = "rest", Service = "S", Method = "M", MethodType = "Unary",
                    HttpPath = "/t", HttpVerb = "GET", Status = "OK", Response = """{"x":"REPLACE"}""",
                },
            },
        };

        using var host = BuildHost(rec, opts =>
            opts.ResponseTransformer = (_, body) => body.Replace("REPLACE", "DONE", StringComparison.Ordinal));
        var resp = await host.GetTestClient().GetAsync(new Uri("/t", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal("""{"x":"DONE"}""", await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static IHost BuildHost(BowireRecording recording, Action<MockOptions> configure) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                    .Configure(app =>
                    {
                        app.UseBowireMock(recording, opts =>
                        {
                            opts.Watch = false;
                            opts.ReplaySpeed = 0;
                            configure(opts);
                        });
                        app.Run(ctx => { ctx.Response.StatusCode = 418; return Task.CompletedTask; });
                    });
            })
            .Start();
}
