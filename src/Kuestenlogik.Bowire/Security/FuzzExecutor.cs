// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Security;

/// <summary>
/// Server-side runtime for the schema-aware fuzzer. Shared between
/// the <c>bowire fuzz</c> CLI subcommand and the <c>/api/security/fuzz</c>
/// HTTP endpoint that the workbench's right-click "Fuzz this field"
/// menu calls into. Stateless; payload wordlists + per-category
/// response heuristics live as static data so neither consumer pays
/// for instance state.
/// </summary>
/// <remarks>
/// <para>
/// v1 scope: HTTP-class targets, JSON request bodies, four payload
/// categories (<c>sqli</c> / <c>xss</c> / <c>pathtrav</c> / <c>cmdinj</c>),
/// five payloads per category. Heuristics fire on response-body
/// markers (SQL error banners, /etc/passwd content, command-output
/// markers like <c>uid=…gid=</c>) plus a cross-category latency-spike
/// check that catches blind-execution oracles.
/// </para>
/// <para>
/// Schema-awareness — the ADR's killer differentiator — is the
/// value-shape skip in <see cref="ShouldSkipForValueShape"/>: numeric
/// / boolean fields don't take string payloads cleanly, so the
/// caller is told to drop SQLi / XSS / etc. on those before any
/// network traffic happens. Full frame-semantics-driven skipping
/// (don't fuzz <c>coordinate.latitude</c> with SQLi even when the
/// value happens to round-trip as a string) lives in the workbench-
/// side menu, which has the annotation store and asks the executor
/// to run the categories that survive that filter.
/// </para>
/// </remarks>
public static class FuzzExecutor
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Built-in payload wordlists keyed by category name.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Payloads = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["sqli"] = new[]
        {
            "' OR '1'='1",
            "'; DROP TABLE users; --",
            "1' UNION SELECT NULL,NULL,NULL--",
            "admin'--",
            "1 AND SLEEP(5)",
        },
        ["xss"] = new[]
        {
            "<script>alert('bowire-xss')</script>",
            "\"><script>alert('bowire-xss')</script>",
            "<img src=x onerror=alert('bowire-xss')>",
            "javascript:alert('bowire-xss')",
            "'\"--></style></script><svg onload=alert(1)>",
        },
        ["pathtrav"] = new[]
        {
            "../../../etc/passwd",
            "..\\..\\..\\windows\\win.ini",
            "%2e%2e%2f%2e%2e%2f%2e%2e%2fetc%2fpasswd",
            "....//....//etc/passwd",
            "/etc/passwd%00.png",
        },
        ["cmdinj"] = new[]
        {
            "; cat /etc/passwd",
            "&& whoami",
            "| id",
            "`id`",
            "$(sleep 5)",
        },
    };

    /// <summary>
    /// Schema-awareness check — value-shape vs payload-class. Returns
    /// true when the targeted field's existing value is a kind the
    /// payload category doesn't fit (numeric / bool field for SQLi /
    /// XSS / pathtrav / cmdinj string payloads). Caller decides
    /// whether to skip or force.
    /// </summary>
    public static bool ShouldSkipForValueShape(JsonValueKind kind, string category)
    {
        // The category-skip table is uniform across the current
        // wordlist — every payload is a string — so the check is
        // just "is the field a non-string scalar?". Once binary
        // payloads (file-upload mutators for image.bytes) land, this
        // splits per-category.
        return kind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
    }

    /// <summary>
    /// Run the fuzz session: capture a baseline against the original
    /// body, replay every payload in the category against the same
    /// endpoint with the field mutated, evaluate heuristics, return
    /// a structured result list (one entry per payload — both fired
    /// and not-fired so the caller can render a per-row table).
    /// </summary>
    public static async Task<FuzzRunResult> RunAsync(FuzzExecutorRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Payloads.TryGetValue(request.Category, out var payloads))
            return FuzzRunResult.Error($"Unknown payload category '{request.Category}'. Available: {string.Join(", ", Payloads.Keys)}.");

        if (string.IsNullOrEmpty(request.Body))
            return FuzzRunResult.Error("Request body is empty — fuzz needs a JSON body to mutate.");

        JsonElement bodyRoot;
        try
        {
            using var doc = JsonDocument.Parse(request.Body);
            bodyRoot = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return FuzzRunResult.Error($"Request body is not valid JSON: {ex.Message}");
        }

        if (!TryNavigatePath(bodyRoot, request.Field, out var existingValue))
            return FuzzRunResult.Error($"Field path '{request.Field}' not found in request body.");

        if (!request.Force && ShouldSkipForValueShape(existingValue.ValueKind, request.Category))
            return FuzzRunResult.Error(
                $"Field is {existingValue.ValueKind}; the {request.Category} category sends string payloads. Use Force / --force to override.");

        var http = request.Http ?? throw new ArgumentException("FuzzExecutorRequest.Http is required.", nameof(request));

        // Baseline pass with the UNMUTATED body so per-payload
        // heuristics can diff against "what does this endpoint
        // normally return".
        AttackProbeResponse? baseline = null;
        try
        {
            baseline = await SendAsync(http, request, request.Body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Baseline failure isn't fatal — every payload runs without
            // baseline-diff heuristics; latency-spike check shorts out.
            baseline = null;
        }

        var rows = new List<FuzzResultRow>();
        for (var i = 0; i < payloads.Count; i++)
        {
            var payload = payloads[i];
            var mutated = ReplaceField(request.Body, request.Field, payload);
            if (mutated is null)
            {
                rows.Add(new FuzzResultRow(payload, FuzzOutcome.Error, "could not substitute payload (path mismatch)", null));
                continue;
            }
            try
            {
                var resp = await SendAsync(http, request, mutated, ct).ConfigureAwait(false);
                var marker = EvaluateHeuristic(request.Category, payload, resp, baseline);
                rows.Add(marker is null
                    ? new FuzzResultRow(payload, FuzzOutcome.Safe, $"status={resp.Status} body={resp.Body.Length}B latency={resp.LatencyMs}ms", resp)
                    : new FuzzResultRow(payload, FuzzOutcome.Vulnerable, marker, resp));
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                rows.Add(new FuzzResultRow(payload, FuzzOutcome.Error, ex.Message, null));
            }
        }

        return new FuzzRunResult
        {
            Rows = rows,
            BaselineStatus = baseline?.Status,
            BaselineLatencyMs = baseline?.LatencyMs,
            BaselineBodySize = baseline?.Body.Length,
        };
    }

    private static async Task<AttackProbeResponse> SendAsync(HttpClient http, FuzzExecutorRequest request, string body, CancellationToken ct)
    {
        var url = CombineUrl(request.Target, request.HttpPath ?? "/");
        var verb = (request.HttpVerb ?? "POST").ToUpperInvariant();
        using var req = new HttpRequestMessage(new HttpMethod(verb), url);

        if (request.Headers is { Count: > 0 } h)
        {
            foreach (var (k, v) in h) req.Headers.TryAddWithoutValidation(k, v);
        }

        if (verb is "POST" or "PUT" or "PATCH" or "DELETE")
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var bodyText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        sw.Stop();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hh in resp.Headers) headers[hh.Key] = string.Join(",", hh.Value);
        foreach (var hh in resp.Content.Headers) headers[hh.Key] = string.Join(",", hh.Value);

        return new AttackProbeResponse
        {
            Status = (int)resp.StatusCode,
            Headers = headers,
            Body = bodyText,
            LatencyMs = (int)sw.ElapsedMilliseconds,
        };
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        var b = baseUrl.TrimEnd('/');
        var p = string.IsNullOrEmpty(path) ? "/" : (path.StartsWith('/') ? path : "/" + path);
        return b + p;
    }

    private static bool TryNavigatePath(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (string.IsNullOrEmpty(path) || path == "$") return true;
        var trimmed = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..]
                     : path.StartsWith('$') ? path[1..]
                     : path;
        foreach (var segment in trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (value.ValueKind != JsonValueKind.Object) return false;
            if (!value.TryGetProperty(segment, out value)) return false;
        }
        return true;
    }

    private static string? ReplaceField(string bodyJson, string fieldPath, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(bodyJson);
            var trimmed = fieldPath.StartsWith("$.", StringComparison.Ordinal) ? fieldPath[2..]
                         : fieldPath.StartsWith('$') ? fieldPath[1..]
                         : fieldPath;
            var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;

            var root = JsonElementToObject(doc.RootElement);
            if (root is not Dictionary<string, object?> rootMap) return null;

            Dictionary<string, object?> cursor = rootMap;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!cursor.TryGetValue(segments[i], out var next) || next is not Dictionary<string, object?> subMap)
                    return null;
                cursor = subMap;
            }
            cursor[segments[^1]] = payload;

            return JsonSerializer.Serialize(rootMap, s_jsonOpts);
        }
        catch (JsonException) { return null; }
    }

    private static object? JsonElementToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>
    /// Per-category response heuristic — public so callers (and
    /// tests) can drive the marker logic in isolation without
    /// orchestrating a full fuzz run.
    /// </summary>
    public static string? EvaluateHeuristic(string category, string payload, AttackProbeResponse resp, AttackProbeResponse? baseline)
    {
        if (baseline is not null && resp.LatencyMs - baseline.LatencyMs > 4000)
            return $"latency spiked +{resp.LatencyMs - baseline.LatencyMs}ms vs baseline — suggests blind-execution oracle";

        if (resp.Body.Contains(payload, StringComparison.Ordinal) && category.Equals("xss", StringComparison.OrdinalIgnoreCase))
            return "payload reflected verbatim in response body — XSS-shape match";

        return category.ToUpperInvariant() switch
        {
            "SQLI" => SqlMarker(resp),
            "XSS" => null,
            "PATHTRAV" => PathTravMarker(resp),
            "CMDINJ" => CmdInjMarker(resp),
            _ => null,
        };
    }

    private static string? SqlMarker(AttackProbeResponse resp)
    {
        var b = resp.Body;
        if (resp.Status == 500 && (b.Contains("SQL", StringComparison.OrdinalIgnoreCase)
                                  || b.Contains("ORA-", StringComparison.Ordinal)
                                  || b.Contains("ODBC", StringComparison.Ordinal))) return "HTTP 500 + SQL-flavoured error in body";
        if (b.Contains("syntax error at or near", StringComparison.OrdinalIgnoreCase)) return "PostgreSQL-style syntax-error banner";
        if (b.Contains("Microsoft SQL Server", StringComparison.OrdinalIgnoreCase)) return "MSSQL-error banner";
        if (b.Contains("SQLite", StringComparison.OrdinalIgnoreCase) && b.Contains("error", StringComparison.OrdinalIgnoreCase)) return "SQLite-error banner";
        return null;
    }

    private static string? PathTravMarker(AttackProbeResponse resp)
    {
        var b = resp.Body;
        if (b.Contains("root:x:0:0:", StringComparison.Ordinal)) return "/etc/passwd content returned";
        if (b.Contains("[boot loader]", StringComparison.OrdinalIgnoreCase) && b.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows boot.ini fragment returned";
        if (b.Contains("[fonts]", StringComparison.OrdinalIgnoreCase)) return "Windows win.ini section returned";
        return null;
    }

    private static string? CmdInjMarker(AttackProbeResponse resp)
    {
        var b = resp.Body;
        if (b.Contains("uid=", StringComparison.Ordinal) && b.Contains("gid=", StringComparison.Ordinal)) return "`id` output detected — command injection landed";
        if (b.Contains("root:x:0:0", StringComparison.Ordinal)) return "`cat /etc/passwd` output detected";
        return null;
    }
}

/// <summary>Input bundle for <see cref="FuzzExecutor.RunAsync"/>.</summary>
public sealed class FuzzExecutorRequest
{
    public string Target { get; init; } = "";
    public string? HttpVerb { get; init; }
    public string? HttpPath { get; init; }
    public string Body { get; init; } = "";
    public IDictionary<string, string>? Headers { get; init; }
    public string Field { get; init; } = "";
    public string Category { get; init; } = "";
    public bool Force { get; init; }
    public HttpClient? Http { get; init; }
}

/// <summary>One row of the fuzz result — per-payload outcome.</summary>
public sealed record FuzzResultRow(string Payload, FuzzOutcome Outcome, string Detail, AttackProbeResponse? Response);

/// <summary>Per-payload outcome bucket.</summary>
public enum FuzzOutcome { Safe, Vulnerable, Error }

/// <summary>Aggregate result of one fuzz run.</summary>
public sealed class FuzzRunResult
{
    public IReadOnlyList<FuzzResultRow> Rows { get; init; } = Array.Empty<FuzzResultRow>();
    public int? BaselineStatus { get; init; }
    public int? BaselineLatencyMs { get; init; }
    public int? BaselineBodySize { get; init; }
    public string? ErrorMessage { get; init; }

    internal static FuzzRunResult Error(string error) => new() { ErrorMessage = error };
}
