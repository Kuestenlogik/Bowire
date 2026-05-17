// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for <see cref="FuzzExecutor"/> — the shared schema-aware
/// fuzzing core behind both <c>bowire fuzz</c> and the workbench's
/// right-click "Fuzz this field" menu. Exercises the four payload
/// categories, the value-shape skip guard, the baseline-diff oracle,
/// the per-category heuristics in isolation, and the major error
/// paths (missing field, malformed body, unknown category).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
    Justification = "Test scope: the StubHandler is owned by the HttpClient via disposeHandler:true; CA2000 doesn't follow that transfer.")]
public sealed class FuzzExecutorTests
{
    /// <summary>HttpClient handler that hands every request to a
    /// caller-supplied responder. No socket traffic — keeps the
    /// fuzz-runner tests fully deterministic.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (int status, string body, int? delayMs)> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, (int status, string body, int? delayMs)> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (status, body, delay) = _responder(request);
            if (delay is > 0) await Task.Delay(delay.Value, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static FuzzExecutorRequest Req(string field, string category, string body = "{\"username\":\"alice\"}", bool force = false, HttpClient? http = null) => new()
    {
        Target = "http://example.invalid",
        HttpVerb = "POST",
        HttpPath = "/login",
        Body = body,
        Field = field,
        Category = category,
        Force = force,
        Http = http,
    };

    // ---------------- value-shape guard ----------------

    [Theory]
    [InlineData(JsonValueKind.Number, "sqli", true)]
    [InlineData(JsonValueKind.True, "xss", true)]
    [InlineData(JsonValueKind.False, "cmdinj", true)]
    [InlineData(JsonValueKind.String, "sqli", false)]
    [InlineData(JsonValueKind.Object, "xss", false)]
    public void ShouldSkipForValueShape_FollowsTheValueShapeRule(JsonValueKind kind, string category, bool expected)
    {
        Assert.Equal(expected, FuzzExecutor.ShouldSkipForValueShape(kind, category));
    }

    // ---------------- request validation ----------------

    [Fact]
    public async Task RunAsync_UnknownCategory_ReturnsErrorResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await FuzzExecutor.RunAsync(Req("$.username", "totally-bogus", http: NewHttp((200, "{}", null))), ct);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Unknown payload category", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_EmptyBody_ReturnsErrorResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await FuzzExecutor.RunAsync(Req("$.username", "sqli", body: "", http: NewHttp((200, "{}", null))), ct);
        Assert.Contains("body is empty", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_MalformedJsonBody_ReturnsErrorResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await FuzzExecutor.RunAsync(Req("$.username", "sqli", body: "{not json", http: NewHttp((200, "{}", null))), ct);
        Assert.Contains("not valid JSON", result.ErrorMessage ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FieldNotInBody_ReturnsErrorResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await FuzzExecutor.RunAsync(Req("$.doesntExist", "sqli", http: NewHttp((200, "{}", null))), ct);
        Assert.Contains("not found", result.ErrorMessage ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NumericFieldWithStringPayload_SkippedUnlessForced()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await FuzzExecutor.RunAsync(Req("$.limit", "sqli", body: "{\"limit\":10}", http: NewHttp((200, "{}", null))), ct);
        Assert.Contains("Force / --force", result.ErrorMessage ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NumericFieldWithForceFlag_RunsThePayloads()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = NewHttp((200, "{}", null));
        var result = await FuzzExecutor.RunAsync(Req("$.limit", "sqli", body: "{\"limit\":10}", force: true, http: http), ct);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(5, result.Rows.Count);
    }

    [Fact]
    public async Task RunAsync_MissingHttpClient_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var req = Req("$.username", "sqli", http: null);
        await Assert.ThrowsAsync<ArgumentException>(async () => await FuzzExecutor.RunAsync(req, ct));
    }

    // ---------------- happy paths ----------------

    [Fact]
    public async Task RunAsync_AllPayloadsBenign_AllRowsSafe()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = NewHttp((200, "{\"ok\":true}", null));
        var result = await FuzzExecutor.RunAsync(Req("$.username", "sqli", http: http), ct);
        Assert.Equal(5, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.Equal(FuzzOutcome.Safe, r.Outcome));
        Assert.NotNull(result.BaselineStatus);
        Assert.NotNull(result.BaselineBodySize);
    }

    [Fact]
    public async Task RunAsync_NestedField_MutatesAtRightPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(_ => (200, "{}", null));
        using var http = new HttpClient(handler, disposeHandler: true);
        var body = "{\"filter\":{\"id\":\"abc\"}}";
        var result = await FuzzExecutor.RunAsync(Req("$.filter.id", "sqli", body: body, http: http), ct);
        Assert.Null(result.ErrorMessage);
        // First request is the baseline; payload requests follow.
        Assert.True(handler.Requests.Count >= 6);
    }

    // ---------------- heuristics ----------------

    [Fact]
    public void EvaluateHeuristic_SqliWith500AndSqlBanner_FiresVulnerable()
    {
        var resp = new AttackProbeResponse { Status = 500, Body = "internal SQL error: missing column", LatencyMs = 50 };
        var hit = FuzzExecutor.EvaluateHeuristic("sqli", "' OR 1=1", resp, null);
        Assert.NotNull(hit);
    }

    [Fact]
    public void EvaluateHeuristic_PostgresSyntaxBanner_FiresVulnerable()
    {
        var resp = new AttackProbeResponse { Status = 200, Body = "syntax error at or near \"OR\"", LatencyMs = 30 };
        Assert.NotNull(FuzzExecutor.EvaluateHeuristic("sqli", "x", resp, null));
    }

    [Fact]
    public void EvaluateHeuristic_MssqlBanner_FiresVulnerable()
    {
        var resp = new AttackProbeResponse { Status = 200, Body = "Microsoft SQL Server: incorrect syntax", LatencyMs = 20 };
        Assert.NotNull(FuzzExecutor.EvaluateHeuristic("sqli", "x", resp, null));
    }

    [Fact]
    public void EvaluateHeuristic_SqliteBanner_FiresVulnerable()
    {
        var resp = new AttackProbeResponse { Status = 200, Body = "SQLite encountered an error", LatencyMs = 20 };
        Assert.NotNull(FuzzExecutor.EvaluateHeuristic("sqli", "x", resp, null));
    }

    [Fact]
    public void EvaluateHeuristic_XssReflectedVerbatim_FiresVulnerable()
    {
        var payload = "<script>alert('bowire-xss')</script>";
        var resp = new AttackProbeResponse { Status = 200, Body = $"hello {payload} world", LatencyMs = 20 };
        Assert.NotNull(FuzzExecutor.EvaluateHeuristic("xss", payload, resp, null));
    }

    [Fact]
    public void EvaluateHeuristic_PathTrav_PasswdReturned_FiresVulnerable()
    {
        var resp = new AttackProbeResponse { Status = 200, Body = "root:x:0:0:root:/root:/bin/bash", LatencyMs = 20 };
        Assert.NotNull(FuzzExecutor.EvaluateHeuristic("pathtrav", "../etc/passwd", resp, null));
    }

    [Fact]
    public void EvaluateHeuristic_PathTrav_WindowsBootIni_FiresVulnerable()
    {
        var resp = new AttackProbeResponse { Status = 200, Body = "[boot loader]\ntimeout=30\nWindows", LatencyMs = 20 };
        Assert.NotNull(FuzzExecutor.EvaluateHeuristic("pathtrav", "..\\boot.ini", resp, null));
    }

    [Fact]
    public void EvaluateHeuristic_PathTrav_WinIni_FiresVulnerable()
    {
        var resp = new AttackProbeResponse { Status = 200, Body = "[fonts]\nArial=", LatencyMs = 20 };
        Assert.NotNull(FuzzExecutor.EvaluateHeuristic("pathtrav", "..\\win.ini", resp, null));
    }

    [Fact]
    public void EvaluateHeuristic_CmdInj_IdOutput_FiresVulnerable()
    {
        var resp = new AttackProbeResponse { Status = 200, Body = "uid=0(root) gid=0(root) groups=0", LatencyMs = 20 };
        Assert.NotNull(FuzzExecutor.EvaluateHeuristic("cmdinj", "; id", resp, null));
    }

    [Fact]
    public void EvaluateHeuristic_CmdInj_PasswdReturned_FiresVulnerable()
    {
        var resp = new AttackProbeResponse { Status = 200, Body = "root:x:0:0:bin/bash", LatencyMs = 20 };
        Assert.NotNull(FuzzExecutor.EvaluateHeuristic("cmdinj", "; cat /etc/passwd", resp, null));
    }

    [Fact]
    public void EvaluateHeuristic_LatencySpike_FiresBlindOracle()
    {
        var baseline = new AttackProbeResponse { Status = 200, Body = "{}", LatencyMs = 20 };
        var resp = new AttackProbeResponse { Status = 200, Body = "{}", LatencyMs = 6000 };
        var hit = FuzzExecutor.EvaluateHeuristic("sqli", "SLEEP(5)", resp, baseline);
        Assert.NotNull(hit);
        Assert.Contains("latency", hit!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateHeuristic_BenignResponse_ReturnsNull()
    {
        var resp = new AttackProbeResponse { Status = 200, Body = "{\"ok\":true}", LatencyMs = 30 };
        Assert.Null(FuzzExecutor.EvaluateHeuristic("sqli", "x", resp, null));
        Assert.Null(FuzzExecutor.EvaluateHeuristic("xss", "x", resp, null));
        Assert.Null(FuzzExecutor.EvaluateHeuristic("pathtrav", "x", resp, null));
        Assert.Null(FuzzExecutor.EvaluateHeuristic("cmdinj", "x", resp, null));
    }

    // ---------------- payload-row outcomes via runner ----------------

    [Fact]
    public async Task RunAsync_RespondsWithSqliBanner_RowsMarkedVulnerable()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = NewHttp((500, "Microsoft SQL Server: incorrect syntax near OR", null));
        var result = await FuzzExecutor.RunAsync(Req("$.username", "sqli", http: http), ct);
        Assert.All(result.Rows, r => Assert.Equal(FuzzOutcome.Vulnerable, r.Outcome));
    }

    [Fact]
    public async Task RunAsync_HttpClientThrowsOnPayload_RowsMarkedError()
    {
        var ct = TestContext.Current.CancellationToken;
        // First call (baseline) succeeds, every subsequent call throws.
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) return (200, "{}", null);
            throw new HttpRequestException("upstream gone");
        });
        using var http = new HttpClient(handler, disposeHandler: true);
        var result = await FuzzExecutor.RunAsync(Req("$.username", "sqli", http: http), ct);
        Assert.All(result.Rows, r => Assert.Equal(FuzzOutcome.Error, r.Outcome));
    }

    [Fact]
    public async Task RunAsync_BaselineFailure_ContinuesWithoutBaseline()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new HttpRequestException("baseline boom");
            return (200, "{}", null);
        });
        using var http = new HttpClient(handler, disposeHandler: true);
        var result = await FuzzExecutor.RunAsync(Req("$.username", "sqli", http: http), ct);
        Assert.Null(result.BaselineStatus);
        Assert.Equal(5, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.Equal(FuzzOutcome.Safe, r.Outcome));
    }

    [Fact]
    public async Task RunAsync_GetVerbWithFieldPath_SendsRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = NewHttp((200, "{}", null));
        var req = new FuzzExecutorRequest
        {
            Target = "http://example.invalid",
            HttpVerb = "GET",
            HttpPath = "search",                       // no leading slash on purpose
            Body = "{\"q\":\"hello\"}",
            Field = "$.q",
            Category = "sqli",
            Http = http,
        };
        var result = await FuzzExecutor.RunAsync(req, ct);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(5, result.Rows.Count);
    }

    [Fact]
    public async Task RunAsync_WithCustomHeaders_PropagatesToRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        var seenAuth = false;
        var handler = new StubHandler(req =>
        {
            if (req.Headers.TryGetValues("Authorization", out var v) && v.Single() == "Bearer xyz") seenAuth = true;
            return (200, "{}", null);
        });
        using var http = new HttpClient(handler, disposeHandler: true);
        var fuzz = new FuzzExecutorRequest
        {
            Target = "http://example.invalid",
            HttpVerb = "POST",
            HttpPath = "/login",
            Body = "{\"u\":\"alice\"}",
            Field = "$.u",
            Category = "xss",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer xyz" },
            Http = http,
        };
        await FuzzExecutor.RunAsync(fuzz, ct);
        Assert.True(seenAuth);
    }

    [Fact]
    public async Task RunAsync_PayloadRoundTrip_BodyContainsSubstitution()
    {
        var ct = TestContext.Current.CancellationToken;
        string? lastBody = null;
        var handler = new StubHandler(req =>
        {
            lastBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return (200, "{}", null);
        });
        using var http = new HttpClient(handler, disposeHandler: true);
        var fuzz = new FuzzExecutorRequest
        {
            Target = "http://example.invalid",
            HttpVerb = "POST",
            HttpPath = "/login",
            Body = "{\"u\":\"alice\"}",
            Field = "$.u",
            Category = "sqli",
            Http = http,
        };
        await FuzzExecutor.RunAsync(fuzz, ct);
        Assert.NotNull(lastBody);
        // Last payload was "1 AND SLEEP(5)" — body should now carry it.
        Assert.Contains("SLEEP(5)", lastBody!, StringComparison.Ordinal);
    }

    private static HttpClient NewHttp((int status, string body, int? delayMs) fixedResponse)
    {
        var handler = new StubHandler(_ => fixedResponse);
        return new HttpClient(handler, disposeHandler: true);
    }
}
