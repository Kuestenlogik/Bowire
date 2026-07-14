// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security;
using Kuestenlogik.Bowire.Security.Scanner;
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
// No [Collection] needed — ScanCommand.RunAsync now accepts a TextWriter
// pair directly (System.CommandLine's InvocationConfiguration.Output /
// Error in production, the StringWriter pair Capture builds for tests),
// so this class never touches process-global Console.Out.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861:Prefer static readonly fields over constant array arguments", Justification = "Test scope — array allocations are negligible")]
public sealed class ScanCommandTests
{
    // Hands a fresh StringWriter pair to the test body and harvests
    // whatever it wrote — no Console.SetOut / Console.SetError, no
    // process-global state touched. Each test gets its own writer pair,
    // so this class is safely parallelisable with everything else.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed",
        Justification = "The Task<int> is synchronously joined via GetAwaiter().GetResult() before the writers leave scope, so the writers are guaranteed live for the entire task lifetime.")]
    private static (int code, string stdout, string stderr) Capture(Func<StringWriter, StringWriter, Task<int>> action)
    {
        using var sbOut = new StringWriter();
        using var sbErr = new StringWriter();
        var code = action(sbOut, sbErr).GetAwaiter().GetResult();
        return (code, sbOut.ToString(), sbErr.ToString());
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
        var path = SafePath.Combine(Path.GetTempPath(), $"bowire-scan-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(rec), ct);
        return path;
    }

    // ---------------- arg validation ----------------

    [Fact]
    public async Task RunAsync_EmptyTarget_ReturnsUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _, stderr) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions(), ct, @out, err));
        Assert.Equal(2, code);
        Assert.Contains("Usage", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RunAsync_NonHttpTarget_SkipsHttpChecksWithoutCrashing()
    {
        // Regression: a mqtt:// (or any non-http/https) target used to sink the
        // whole scan when the passive built-ins handed the scheme to HttpClient.
        // It must now skip the HTTP-only work and report cleanly instead.
        var ct = TestContext.Current.CancellationToken;
        var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
        {
            Target = "mqtt://broker.example.com:1883",
        }, ct, @out, err));

        Assert.Equal(0, code);
        Assert.Contains("not an http/https URL", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_NoTemplatesAndNoBuiltins_ReturnsUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _, stderr) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
        {
            Target = "https://example.invalid",
            RunBuiltins = false,
        }, ct, @out, err));
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
            var (code, _, stderr) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = "not a url",
                Template = path,
                RunBuiltins = false,
            }, ct, @out, err));
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
            var (code, _, stderr) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = "https://other.example.com",
                Template = path,
                Scope = new[] { "api.example.com" },
                RunBuiltins = false,
            }, ct, @out, err));
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
    public async Task RunAsync_TemplateMatchesUpstream_ReportsVulnerableAndReturns0()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);

        // Template expects status 200; upstream returns 200 → match.
        // Findings are the scanner's *product*, not a failure — so
        // even with a vulnerable target the scan-step itself exits 0.
        // Pipelines that want to gate on findings post-process SARIF.
        var path = await WriteAsync(AttackRecording(expectedStatus: 200), ct);
        try
        {
            var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Template = path,
                RunBuiltins = false,
                TimeoutSeconds = 10,
            }, ct, @out, err));
            Assert.Equal(0, code);
            Assert.Contains("[VULN]", stdout, StringComparison.Ordinal);
            Assert.Contains("BWR-T-001", stdout, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_WithOutSarif_WritesSarifReport()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);
        var path = await WriteAsync(AttackRecording(expectedStatus: 200), ct);
        var sarif = SafePath.Combine(Path.GetTempPath(), $"bowire-scan-{Guid.NewGuid():N}.sarif");
        try
        {
            var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Template = path,
                RunBuiltins = false,
                TimeoutSeconds = 10,
                OutSarif = sarif,
            }, ct, @out, err));

            Assert.Equal(0, code);
            Assert.Contains("SARIF", stdout, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sarif));
            var body = await File.ReadAllTextAsync(sarif, ct);
            Assert.Contains("BWR-T-001", body, StringComparison.Ordinal);
            Assert.Contains("sarif", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
            if (File.Exists(sarif)) File.Delete(sarif);
        }
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
            var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Template = path,
                RunBuiltins = false,
                TimeoutSeconds = 10,
            }, ct, @out, err));
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
            var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Template = path,
                RunBuiltins = false,
                MinSeverity = "high",
            }, ct, @out, err));
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
            var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Template = path,
                RunBuiltins = false,
            }, ct, @out, err));
            Assert.Equal(0, code);
            Assert.Contains("not yet supported by scanner", stdout, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("signalr", "POST", "/hubs/probe/negotiate")]
    [InlineData("socketio", "GET", "/socket.io/?EIO=4&transport=polling")]
    [InlineData("mcp", "POST", "/mcp")]
    public async Task RunAsync_HandshakeProtocolTemplate_IsProbedAsHttpClass(string protocol, string verb, string httpPath)
    {
        // SignalR / Socket.IO / MCP are HTTP-class for the request the
        // template probes (negotiate / EIO polling handshake / JSON-RPC POST),
        // so these templates must be probed (and can fire), NOT skipped with
        // "transport not yet supported". Guards the IsHttpClassProtocol
        // allow-list against a regression that would silently stop running
        // every template for one of these protocols.
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);

        var rec = AttackRecording(); // default predicate: status == 200
        rec.Steps[0].Protocol = protocol;
        rec.Steps[0].HttpVerb = verb;
        rec.Steps[0].HttpPath = httpPath;
        var path = await WriteAsync(rec, ct);
        try
        {
            var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Template = path,
                RunBuiltins = false,
            }, ct, @out, err));
            Assert.Equal(0, code);
            Assert.DoesNotContain("not yet supported by scanner", stdout, StringComparison.Ordinal);
            // The upstream returns 200, so the status==200 predicate fires.
            Assert.Contains("VULN", stdout, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_WebSocketTemplate_ProbesHandshake_AndFires()
    {
        // The WebSocket probe does the raw upgrade handshake and evaluates the
        // predicate against the 101 status + response headers. This upstream
        // accepts any upgrade and echoes the requested subprotocol, so the
        // template (status 101 + reflected Sec-WebSocket-Protocol) fires —
        // proving the whole handshake probe path end-to-end, not just the
        // dispatch.
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartWebSocketUpstreamAsync(ct);

        var rec = new BowireRecording
        {
            Name = "ws-test",
            Attack = true,
            Vulnerability = new AttackVulnerability { Id = "BWR-WS-T", Severity = "high" },
            VulnerableWhen = new AttackPredicate
            {
                Status = 101,
                HeaderEquals = new Dictionary<string, string> { ["Sec-WebSocket-Protocol"] = "chat.attacker" },
            },
            Steps =
            {
                new BowireRecordingStep
                {
                    Protocol = "websocket",
                    HttpVerb = "GET",
                    HttpPath = "/ws",
                    Metadata = new Dictionary<string, string> { ["Sec-WebSocket-Protocol"] = "chat.attacker" },
                },
            },
        };
        var path = await WriteAsync(rec, ct);
        try
        {
            var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Template = path,
                RunBuiltins = false,
            }, ct, @out, err));
            Assert.Equal(0, code);
            Assert.DoesNotContain("not yet supported by scanner", stdout, StringComparison.Ordinal);
            Assert.Contains("VULN", stdout, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_WebSocketTemplate_NonUpgradePath_IsSafe()
    {
        // The same probe against a path that doesn't answer the upgrade with a
        // 101 must NOT match the status-101 predicate — the reject side of the
        // handshake check.
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartWebSocketUpstreamAsync(ct);

        var rec = new BowireRecording
        {
            Name = "ws-test-safe",
            Attack = true,
            Vulnerability = new AttackVulnerability { Id = "BWR-WS-T2", Severity = "high" },
            VulnerableWhen = new AttackPredicate { Status = 101 },
            Steps =
            {
                new BowireRecordingStep { Protocol = "websocket", HttpVerb = "GET", HttpPath = "/not-a-socket" },
            },
        };
        var path = await WriteAsync(rec, ct);
        try
        {
            var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Template = path,
                RunBuiltins = false,
            }, ct, @out, err));
            Assert.Equal(0, code);
            Assert.DoesNotContain("[VULN]", stdout, StringComparison.Ordinal);
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
            var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = "http://127.0.0.1:1",
                Template = path,
                RunBuiltins = false,
                TimeoutSeconds = 2,
            }, ct, @out, err));
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

        var corpusDir = SafePath.Combine(Path.GetTempPath(), $"bowire-corpus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(corpusDir);
        try
        {
            var rec1 = AttackRecording(expectedStatus: 200);
            var rec2 = AttackRecording(expectedStatus: 418);
            await File.WriteAllTextAsync(SafePath.Combine(corpusDir, "a.json"), JsonSerializer.Serialize(rec1), ct);
            await File.WriteAllTextAsync(SafePath.Combine(corpusDir, "b.json"), JsonSerializer.Serialize(rec2), ct);

            var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Templates = corpusDir,
                RunBuiltins = false,
                TimeoutSeconds = 10,
            }, ct, @out, err));
            Assert.Equal(0, code);
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
        var sarifPath = SafePath.Combine(Path.GetTempPath(), $"bowire-scan-{Guid.NewGuid():N}.sarif");
        try
        {
            var (code, _, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Template = template,
                OutSarif = sarifPath,
                RunBuiltins = false,
                TimeoutSeconds = 10,
            }, ct, @out, err));
            Assert.Equal(0, code);
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

    [Fact]
    public async Task RunAsync_CorruptTemplateInCorpus_SkippedWithWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);

        var corpusDir = SafePath.Combine(Path.GetTempPath(), $"bowire-corpus-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(corpusDir);
        try
        {
            // One usable template + one corrupt JSON.
            await File.WriteAllTextAsync(SafePath.Combine(corpusDir, "good.json"), JsonSerializer.Serialize(AttackRecording()), ct);
            await File.WriteAllTextAsync(SafePath.Combine(corpusDir, "bad.json"), "{not json", ct);

            var (code, _, stderr) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Templates = corpusDir,
                RunBuiltins = false,
                TimeoutSeconds = 10,
            }, ct, @out, err));
            Assert.Equal(0, code);
            Assert.Contains("Skipping bad.json", stderr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(corpusDir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_WithBuiltinsAgainstHttpTarget_EmitsBuiltinFindings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);

        var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
        {
            Target = upstream.Urls.First(),
            RunBuiltins = true,  // hits the builtin-merge branch
            TimeoutSeconds = 5,
        }, ct, @out, err));
        Assert.Equal(0, code);
        Assert.Contains("[VULN]", stdout, StringComparison.Ordinal);
        Assert.Contains("BWR-BUILTIN-TLS-001", stdout, StringComparison.Ordinal);  // plaintext-http finding
    }

    [Fact]
    public async Task RunAsync_BuiltinFindingBelowSeverityThreshold_MarkedSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);

        var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
        {
            Target = upstream.Urls.First(),
            RunBuiltins = true,
            MinSeverity = "critical",   // every finding is below this, including the high-sev plaintext-http
            TimeoutSeconds = 5,
        }, ct, @out, err));
        Assert.Equal(0, code);
        Assert.Contains("below severity threshold", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NucleiCorpus_LoadsAndFiresFinding()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);

        // Nuclei template that fires against the upstream's
        // default response (status 200 + body contains "hello").
        // Hits the full pipeline: YAML parse → matcher translation
        // → variable substitution → scanner probe execution.
        var nucleiDir = SafePath.Combine(Path.GetTempPath(), $"bowire-nuclei-{Guid.NewGuid():N}");
        Directory.CreateDirectory(nucleiDir);
        try
        {
            await File.WriteAllTextAsync(SafePath.Combine(nucleiDir, "hello.yaml"), """
                id: nuclei-hello-test
                info:
                  name: Hello probe
                  severity: low
                  description: Test template — fires when upstream returns "hello"
                http:
                  - method: GET
                    path:
                      - '{{BaseURL}}/'
                    matchers-condition: and
                    matchers:
                      - type: status
                        status:
                          - 200
                      - type: word
                        words:
                          - "hello"
                        part: body
                """, ct);

            var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Nuclei = nucleiDir,
                RunBuiltins = false,
                TimeoutSeconds = 10,
            }, ct, @out, err));

            Assert.Equal(0, code);
            Assert.Contains("Loaded 1 nuclei template(s)", stdout, StringComparison.Ordinal);
            Assert.Contains("[VULN]", stdout, StringComparison.Ordinal);
            Assert.Contains("nuclei-hello-test", stdout, StringComparison.Ordinal);
        }
        finally { Directory.Delete(nucleiDir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_NucleiCorpus_MultiPathUnfoldsAndAllProbe()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);

        var nucleiDir = SafePath.Combine(Path.GetTempPath(), $"bowire-nuclei-{Guid.NewGuid():N}");
        Directory.CreateDirectory(nucleiDir);
        try
        {
            await File.WriteAllTextAsync(SafePath.Combine(nucleiDir, "multipath.yaml"), """
                id: nuclei-multipath-test
                info:
                  name: Multi-path probe
                  severity: low
                http:
                  - method: GET
                    path:
                      - '{{BaseURL}}/admin'
                      - '{{BaseURL}}/login'
                      - '{{BaseURL}}/api'
                    matchers:
                      - type: status
                        status:
                          - 200
                """, ct);

            var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
            {
                Target = upstream.Urls.First(),
                Nuclei = nucleiDir,
                RunBuiltins = false,
                TimeoutSeconds = 10,
            }, ct, @out, err));

            Assert.Equal(0, code);
            // Unfolded to 3 recordings — output shows the loaded count.
            Assert.Contains("Loaded 3 nuclei template(s)", stdout, StringComparison.Ordinal);
        }
        finally { Directory.Delete(nucleiDir, recursive: true); }
    }

    // ---------------- named suites (#184): protocol / all ----------------

    [Fact]
    public async Task RunAsync_SuiteProtocol_HttpTarget_WritesCoverageTableAndSkipsHttpOwaspProbes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);

        // --suite=protocol against an http target: only the protocol-specific
        // probes run (the plugins are absent in tests → PLUGIN-ABSENT skips),
        // the HTTP OWASP probes are skipped by design, and the coverage table
        // is still written.
        var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
        {
            Target = upstream.Urls.First(),
            Suite = "protocol",
            RunBuiltins = false,
            TimeoutSeconds = 10,
        }, ct, @out, err));

        Assert.Equal(0, code);
        Assert.Contains("OWASP API Security Top 10", stdout, StringComparison.Ordinal);
        // The "HTTP OWASP probes skipped — non-HTTP target" note belongs to the
        // owasp-api/all path; in protocol mode the HTTP OWASP probes are simply
        // never invoked, so that note must NOT appear even though the target IS http.
        Assert.DoesNotContain("HTTP OWASP probes skipped", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SuiteAll_WritesCoverageTable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);

        // --suite=all is the "run everything" alias: HTTP OWASP probes (http
        // target) + protocol probes + the coverage table.
        var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
        {
            Target = upstream.Urls.First(),
            Suite = "all",
            RunBuiltins = false,
            TimeoutSeconds = 10,
        }, ct, @out, err));

        Assert.Equal(0, code);
        Assert.Contains("OWASP API Security Top 10", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void RunAsync_SuiteProtocol_NonHttpTarget_RunsProtocolProbesAndWritesTable()
    {
        // --suite=protocol makes non-HTTP targets first-class: no HTTP work is
        // attempted (the isHttpTarget guards skip templates/builtins), the
        // protocol probes still run (PLUGIN-ABSENT skips in tests, which is
        // fine), and the coverage table is still written.
        var ct = TestContext.Current.CancellationToken;
        var (code, stdout, _) = Capture((@out, err) => ScanCommand.RunAsync(new ScanOptions
        {
            Target = "mqtt://localhost:1883",
            Suite = "protocol",
            RunBuiltins = true,   // exercise the non-http built-in skip note too
            TimeoutSeconds = 5,
        }, ct, @out, err));

        Assert.Equal(0, code);
        Assert.Contains("OWASP API Security Top 10", stdout, StringComparison.Ordinal);
        Assert.Contains("not an http/https URL", stdout, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<WebApplication> StartUpstreamAsync(CancellationToken ct)
    {
        // Pin the content root to a known-existing directory so this
        // helper is immune to peer tests that swap the process CWD into
        // a temp dir and delete it before restoring (workspace-tests
        // were observed doing this on CI). Without the override
        // CreateSlimBuilder() throws ArgumentException on a dead CWD.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.Run(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"hello\":\"world\"}", ctx.RequestAborted);
        });
        await app.StartAsync(ct);
        return app;
    }

    // In-process WebSocket upstream: accepts any upgrade (no Origin check) and
    // echoes the first requested subprotocol — the two behaviours the WS
    // handshake templates detect. Plain HTTP/1.1 so the probe connects without
    // TLS.
    private static async Task<WebApplication> StartWebSocketUpstreamAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            var requested = ctx.WebSockets.WebSocketRequestedProtocols;
            var sub = requested.Count > 0 ? requested[0] : null;
            using var socket = sub is null
                ? await ctx.WebSockets.AcceptWebSocketAsync()
                : await ctx.WebSockets.AcceptWebSocketAsync(sub);
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ctx.RequestAborted);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
            {
                // The probe drops the TCP connection after reading the 101 head.
            }
        });
        await app.StartAsync(ct);
        return app;
    }
}
