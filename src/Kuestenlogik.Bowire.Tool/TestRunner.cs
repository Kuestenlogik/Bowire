// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// CLI test runner — loads a test collection JSON file, invokes each test
/// in-process via the protocol registry (no HTTP detour), runs assertions,
/// prints results, and exits with code 0/1. Supports the same operators as
/// the in-browser Tests tab so collections are portable in both directions.
/// </summary>
internal static class TestRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ANSI colour escapes only when (a) the writer IS the real Console.Out
    // and (b) the real Console isn't redirected. A StringWriter handed in
    // by a test or by an embedded caller gets plain text — no escapes
    // pollute the captured output, no escape-sequence mismatch breaks
    // string-Contains assertions.
    private static bool UseColor(TextWriter writer)
        => ReferenceEquals(writer, Console.Out) && !Console.IsOutputRedirected;

    /// <summary>
    /// Run a Bowire test collection. <paramref name="output"/> and
    /// <paramref name="error"/> redirect stdout / stderr without touching
    /// <see cref="Console.Out"/>; defaults wire to the real console. The
    /// CLI handler hands the framework's
    /// <c>ParseResult.InvocationConfiguration.Output/.Error</c> through,
    /// tests pass their own <see cref="StringWriter"/> for capture.
    /// </summary>
    public static async Task<int> RunAsync(
        TestCliOptions cli,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(cli);

        var stdout = output ?? Console.Out;
        var stderr = error ?? Console.Error;

        if (string.IsNullOrEmpty(cli.CollectionPath))
        {
            await WriteErrorAsync(stderr, "Usage: bowire test <collection.json> [--report path.html] [--junit path.xml]").ConfigureAwait(false);
            return 2;
        }

        if (!File.Exists(cli.CollectionPath))
        {
            await WriteErrorAsync(stderr, $"Collection file not found: {cli.CollectionPath}").ConfigureAwait(false);
            return 2;
        }

        // v2.2 — `bowire test` accepts EITHER a recording/test-collection
        // (the v2.1 shape this runner has always handled) OR a Flow JSON
        // document (the v2.2 T2 deliverable). Discriminated by JSON
        // shape: a top-level "nodes" array is the Flow canonical
        // discriminator the workbench writes. Dispatch happens here
        // rather than at the CLI layer because the file format is the
        // operator's choice — they shouldn't have to pick a flag.
        string rawJson;
        try
        {
            rawJson = await File.ReadAllTextAsync(cli.CollectionPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await WriteErrorAsync(stderr, $"Failed to read file: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
        if (FlowTestRunner.LooksLikeFlow(rawJson))
        {
            var flowCli = new FlowTestCliOptions
            {
                FlowPath = cli.CollectionPath,
                ReportPath = cli.ReportPath,
                JUnitPath = cli.JUnitPath,
                SarifPath = cli.SarifPath,
                Annotations = cli.Annotations,
                BaseUrl = cli.BaseUrl,
                EnvOverrides = cli.EnvOverrides,
            };
            return await FlowTestRunner.RunAsync(flowCli, stdout, stderr).ConfigureAwait(false);
        }

        TestCollection? collection;
        try
        {
            collection = JsonSerializer.Deserialize<TestCollection>(rawJson, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            await WriteErrorAsync(stderr, $"Failed to parse collection: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        if (collection is null || collection.Tests is null || collection.Tests.Count == 0)
        {
            await WriteErrorAsync(stderr, "Collection has no tests.").ConfigureAwait(false);
            return 2;
        }

        // Initialize the protocol registry once and reuse for every test
        var registry = BowireProtocolRegistry.Discover();
        // Don't initialize protocols with a service provider — standalone mode

        await stdout.WriteLineAsync().ConfigureAwait(false);
        await WriteHeaderAsync(stdout, $"Bowire Test Runner   collection: {Path.GetFileName(cli.CollectionPath)}").ConfigureAwait(false);
        await stdout.WriteLineAsync().ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        var report = new RunReport
        {
            CollectionName = collection.Name ?? Path.GetFileNameWithoutExtension(cli.CollectionPath),
            CollectionPath = cli.CollectionPath,
            StartedAt = DateTime.UtcNow
        };

        var totalAssertions = 0;
        var passedAssertions = 0;
        var failedTests = 0;

        foreach (var test in collection.Tests)
        {
            var result = await RunTestAsync(test, collection, registry);
            report.Tests.Add(result);

            totalAssertions += result.Assertions.Count;
            var passed = 0;
            var anyFailed = false;
            foreach (var a in result.Assertions)
            {
                if (a.Passed) passed++;
                else anyFailed = true;
            }
            passedAssertions += passed;
            if (anyFailed || !string.IsNullOrEmpty(result.Error)) failedTests++;

            await PrintTestResultAsync(stdout, result).ConfigureAwait(false);
        }

        sw.Stop();
        report.DurationMs = sw.ElapsedMilliseconds;
        report.PassedAssertions = passedAssertions;
        report.TotalAssertions = totalAssertions;
        report.FailedTests = failedTests;

        await stdout.WriteLineAsync().ConfigureAwait(false);
        var summaryText = $"  {report.Tests.Count - failedTests}/{report.Tests.Count} tests passed   "
            + $"{passedAssertions}/{totalAssertions} assertions   "
            + $"in {sw.ElapsedMilliseconds} ms";
        var useColor = UseColor(stdout);
        await stdout.WriteLineAsync(failedTests > 0 ? Red(summaryText, useColor) : Green(summaryText, useColor)).ConfigureAwait(false);
        await stdout.WriteLineAsync().ConfigureAwait(false);

        if (!string.IsNullOrEmpty(cli.ReportPath))
        {
            try
            {
                await File.WriteAllTextAsync(cli.ReportPath, HtmlReport.Render(report));
                await stdout.WriteLineAsync($"  HTML report written to {cli.ReportPath}").ConfigureAwait(false);
                await stdout.WriteLineAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
            {
                await WriteErrorAsync(stderr, $"Failed to write report: {ex.Message}").ConfigureAwait(false);
            }
        }

        if (!string.IsNullOrEmpty(cli.JUnitPath))
        {
            try
            {
                await File.WriteAllTextAsync(cli.JUnitPath, JUnitReport.Render(report));
                await stdout.WriteLineAsync($"  JUnit XML written to {cli.JUnitPath}").ConfigureAwait(false);
                await stdout.WriteLineAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
            {
                await WriteErrorAsync(stderr, $"Failed to write JUnit report: {ex.Message}").ConfigureAwait(false);
            }
        }

        if (!string.IsNullOrEmpty(cli.SarifPath))
        {
            try
            {
                await File.WriteAllTextAsync(cli.SarifPath, TestSarifReport.Render(report));
                await stdout.WriteLineAsync($"  SARIF written to {cli.SarifPath}").ConfigureAwait(false);
                await stdout.WriteLineAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
            {
                await WriteErrorAsync(stderr, $"Failed to write SARIF report: {ex.Message}").ConfigureAwait(false);
            }
        }

        if (cli.Annotations)
        {
            await GitHubAnnotations.WriteAsync(stdout, report).ConfigureAwait(false);
        }

        return failedTests > 0 ? 1 : 0;
    }

    private static async Task<TestResult> RunTestAsync(TestEntry test, TestCollection collection, BowireProtocolRegistry registry)
    {
        var result = new TestResult
        {
            Name = test.Name ?? (test.Service + "/" + test.Method),
            Service = test.Service ?? "",
            Method = test.Method ?? ""
        };

        if (string.IsNullOrEmpty(test.Service) || string.IsNullOrEmpty(test.Method))
        {
            result.Error = "Test missing service or method";
            return result;
        }

        var serverUrl = test.ServerUrl ?? collection.ServerUrl ?? "";
        var protocolId = test.Protocol ?? collection.Protocol;
        var environment = MergeEnvironments(collection.Environment, test.Environment);

        // Substitute ${var} placeholders in messages and metadata
        var messages = (test.Messages ?? ["{}"]).Select(m => SubstituteVars(m, environment)).ToList();
        var metadata = test.Metadata?.ToDictionary(
            kv => kv.Key,
            kv => SubstituteVars(kv.Value, environment));

        // Pick the protocol — explicit override or first available
        IBowireProtocol? protocol;
        if (!string.IsNullOrEmpty(protocolId))
        {
            protocol = registry.GetById(protocolId);
        }
        else
        {
            protocol = registry.Protocols.Count > 0 ? registry.Protocols[0] : null;
        }

        if (protocol is null)
        {
            result.Error = $"Protocol '{protocolId ?? "<any>"}' not registered";
            return result;
        }

        // Discover services so the plugin's internal cache is primed
        // Plugin DiscoverAsync: 3rd-party transport; report failure +
        // continue to next test rather than aborting the run.
#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            await protocol.DiscoverAsync(serverUrl, showInternalServices: false);
        }
        catch (Exception ex)
#pragma warning restore CA1031
        {
            result.Error = $"Discovery failed: {ex.Message}";
            return result;
        }

        // Invoke
        var sw = Stopwatch.StartNew();
        InvokeResult? invocation;
        // Plugin InvokeAsync: 3rd-party transport surface as above.
#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            invocation = await protocol.InvokeAsync(serverUrl, test.Service, test.Method, messages, false, metadata);
        }
        catch (Exception ex)
#pragma warning restore CA1031
        {
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            result.Error = $"Invocation failed: {ex.Message}";
            return result;
        }
        sw.Stop();
        result.DurationMs = sw.ElapsedMilliseconds;
        result.Status = invocation.Status;
        result.Response = invocation.Response;

        // Parse the response body once for assertions
        JsonNode? parsed = null;
        if (!string.IsNullOrEmpty(invocation.Response))
        {
            try { parsed = JsonNode.Parse(invocation.Response); }
            catch { /* ignore — assertions on a non-JSON body fall through to string compare */ }
        }

        if (test.Assert is not null)
        {
            foreach (var a in test.Assert)
            {
                var rendered = new AssertionResult
                {
                    Path = a.Path ?? "",
                    Op = a.Op ?? "eq",
                    Expected = SubstituteVars(a.Expected ?? "", environment)
                };
                EvaluateAssertion(a, invocation.Status, parsed, invocation.Response, environment, rendered);
                result.Assertions.Add(rendered);
            }
        }

        return result;
    }

    private static void EvaluateAssertion(
        Assertion test, string statusName, JsonNode? parsedBody, string? rawBody,
        Dictionary<string, string> environment, AssertionResult rendered)
    {
        var path = test.Path ?? string.Empty;
        var op = test.Op ?? "eq";
        var expected = SubstituteVars(test.Expected ?? string.Empty, environment);

        // Resolve actual
        object? actual;
        if (path == "status")
        {
            actual = statusName;
        }
        else if (string.IsNullOrEmpty(path) || path == "response")
        {
            actual = parsedBody is not null ? (object?)parsedBody : rawBody;
        }
        else
        {
            var stripped = path.StartsWith("response.", StringComparison.Ordinal) ? path.Substring("response.".Length) : path;
            actual = WalkJsonPath(parsedBody, stripped);
        }

        rendered.ActualText = FormatActual(actual);

        try
        {
            rendered.Passed = op switch
            {
                "eq"        => DeepEqualsLoose(actual, expected),
                "ne"        => !DeepEqualsLoose(actual, expected),
                "gt"        => TryNumber(actual, out var an1) && TryNumber(expected, out var en1) && an1 > en1,
                "gte"       => TryNumber(actual, out var an2) && TryNumber(expected, out var en2) && an2 >= en2,
                "lt"        => TryNumber(actual, out var an3) && TryNumber(expected, out var en3) && an3 < en3,
                "lte"       => TryNumber(actual, out var an4) && TryNumber(expected, out var en4) && an4 <= en4,
                "contains"  => Contains(actual, expected),
                "matches"   => actual is not null && System.Text.RegularExpressions.Regex.IsMatch(FormatActual(actual), expected),
                "exists"    => actual is not null,
                "notexists" => actual is null,
                "type"      => string.Equals(TypeOf(actual), expected, StringComparison.OrdinalIgnoreCase),
                _           => false
            };
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException or InvalidCastException or InvalidOperationException)
        {
            // Assertion-evaluation surface: regex compile (ArgumentException
            // / RegexMatchTimeoutException), number conversion (FormatException),
            // type coercion (InvalidCastException / InvalidOperationException
            // from JsonNode access).
            rendered.Passed = false;
            rendered.Error = ex.Message;
        }
    }

    private static JsonNode? WalkJsonPath(JsonNode? root, string path)
    {
        if (root is null || string.IsNullOrEmpty(path)) return root;
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current is null) return null;
            if (current is JsonArray arr)
            {
                if (!int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)) return null;
                if (idx < 0 || idx >= arr.Count) return null;
                current = arr[idx];
            }
            else if (current is JsonObject obj)
            {
                current = obj[segment];
            }
            else
            {
                return null;
            }
        }
        return current;
    }

    private static bool DeepEqualsLoose(object? actual, object? expected)
    {
        if (actual is null && expected is null) return true;
        if (actual is null || expected is null) return string.IsNullOrEmpty(actual?.ToString()) && string.IsNullOrEmpty(expected?.ToString());

        var actualText = FormatActual(actual);
        var expectedText = expected.ToString() ?? string.Empty;

        // Numeric coercion — precision-tolerant compare. Both sides
        // come from arbitrary user-supplied JSON / spec strings, so a
        // value like 0.1 + 0.2 vs 0.3 must still match the operator's
        // "loose equals" intent. Uses a hybrid absolute+relative
        // epsilon so integer-valued doubles and tiny fractional ones
        // both fall in tolerance (closes cs/equality-on-floats).
        if (TryNumber(actual, out var a) && TryNumber(expected, out var e))
        {
            var diff = Math.Abs(a - e);
            var scale = Math.Max(Math.Abs(a), Math.Abs(e));
            return diff <= Math.Max(1e-9, scale * 1e-9);
        }

        // Boolean coercion
        if (string.Equals(expectedText, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(expectedText, "false", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(actualText, expectedText, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(actualText, expectedText, StringComparison.Ordinal);
    }

    private static bool TryNumber(object? value, out double n)
    {
        n = 0;
        if (value is null) return false;
        if (value is JsonValue jv && jv.TryGetValue(out double d)) { n = d; return true; }
        var s = FormatActual(value);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n);
    }

    private static bool Contains(object? actual, object? expected)
    {
        var needle = expected?.ToString() ?? string.Empty;
        if (actual is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (DeepEqualsLoose(item, needle)) return true;
            }
            return false;
        }
        var haystack = FormatActual(actual);
        return haystack.Contains(needle, StringComparison.Ordinal);
    }

    private static string FormatActual(object? v)
    {
        if (v is null) return "";
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            return jv.ToJsonString();
        }
        if (v is JsonNode node) return node.ToJsonString();
        return v.ToString() ?? "";
    }

    private static string TypeOf(object? v)
    {
        if (v is null) return "null";
        if (v is JsonArray) return "array";
        if (v is JsonObject) return "object";
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<bool>(out _)) return "boolean";
            if (jv.TryGetValue<double>(out _)) return "number";
            if (jv.TryGetValue<string>(out _)) return "string";
        }
        // CLR type name is ASCII-only — emit it lowercased so it matches the
        // JS typeof contract used by the in-browser Tests tab.
#pragma warning disable CA1308 // Normalize strings to uppercase
        return v.GetType().Name.ToLowerInvariant();
#pragma warning restore CA1308
    }

    private static string SubstituteVars(string input, Dictionary<string, string> env)
    {
        if (string.IsNullOrEmpty(input) || input.IndexOf("${", StringComparison.Ordinal) < 0) return input;
        return System.Text.RegularExpressions.Regex.Replace(input, @"\$(\$?)\{([^}]+)\}", match =>
        {
            if (match.Groups[1].Length > 0) return "${" + match.Groups[2].Value + "}"; // $${} escape
            var key = match.Groups[2].Value.Trim();

            // System variables — same set as the JS layer
            if (key == "now") return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            if (key == "nowMs") return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            if (key == "timestamp") return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            if (key == "uuid") return Guid.NewGuid().ToString();
            if (key == "random") return System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue).ToString(CultureInfo.InvariantCulture);
            var nowMatch = System.Text.RegularExpressions.Regex.Match(key, @"^now([+-])(\d+)$");
            if (nowMatch.Success)
            {
                var baseSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var offset = long.Parse(nowMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                return (nowMatch.Groups[1].Value == "+" ? baseSec + offset : baseSec - offset).ToString(CultureInfo.InvariantCulture);
            }

            return env.TryGetValue(key, out var v) ? v : match.Value;
        });
    }

    private static Dictionary<string, string> MergeEnvironments(
        Dictionary<string, string>? collection, Dictionary<string, string>? test)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (collection is not null)
        {
            foreach (var (k, v) in collection) merged[k] = v;
        }
        if (test is not null)
        {
            foreach (var (k, v) in test) merged[k] = v;
        }
        return merged;
    }

    // ---- Console output ----

    private static async Task PrintTestResultAsync(TextWriter stdout, TestResult result)
    {
        var useColor = UseColor(stdout);
        var status = result.Error is null && result.Assertions.All(a => a.Passed) ? Green("PASS", useColor) : Red("FAIL", useColor);
        await stdout.WriteLineAsync($"  {status}  {Bold(result.Name, useColor)}   {Dim(result.Status + " · " + result.DurationMs + "ms", useColor)}").ConfigureAwait(false);

        if (result.Error is not null)
        {
            await stdout.WriteLineAsync($"        {Red("error: " + result.Error, useColor)}").ConfigureAwait(false);
            return;
        }

        foreach (var a in result.Assertions)
        {
            var icon = a.Passed ? Green("✓", useColor) : Red("✗", useColor);
            await stdout.WriteAsync($"        {icon} {Dim(a.Path + " " + a.Op + " " + Quote(a.Expected), useColor)}").ConfigureAwait(false);
            if (!a.Passed)
            {
                if (a.Error is not null)
                    await stdout.WriteAsync($"   {Red(a.Error, useColor)}").ConfigureAwait(false);
                else
                    await stdout.WriteAsync($"   {Red("actual: " + Quote(a.ActualText), useColor)}").ConfigureAwait(false);
            }
            await stdout.WriteLineAsync().ConfigureAwait(false);
        }
    }

    private static string Quote(string s)
    {
        if (s.Length > 60) s = string.Concat(s.AsSpan(0, 60), "…");
        return s;
    }

    private static Task WriteHeaderAsync(TextWriter stdout, string text)
    {
        var useColor = UseColor(stdout);
        return stdout.WriteLineAsync("  " + Bold(Cyan(text, useColor), useColor));
    }

    private static Task WriteErrorAsync(TextWriter stderr, string text)
    {
        return stderr.WriteLineAsync(Red("error: " + text, UseColor(stderr)));
    }

    private static string Cyan(string text, bool useColor)  => useColor ? $"\x1b[36m{text}\x1b[0m" : text;
    private static string Bold(string text, bool useColor)  => useColor ? $"\x1b[1m{text}\x1b[0m"  : text;
    private static string Dim(string text, bool useColor)   => useColor ? $"\x1b[2m{text}\x1b[0m"  : text;
    private static string Green(string text, bool useColor) => useColor ? $"\x1b[32m{text}\x1b[0m" : text;
    private static string Red(string text, bool useColor)   => useColor ? $"\x1b[31m{text}\x1b[0m" : text;
}

// ---- Test collection JSON shape ----

internal sealed class TestCollection
{
    public string? Name { get; set; }
    public string? ServerUrl { get; set; }
    public string? Protocol { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
    public List<TestEntry> Tests { get; set; } = [];
}

internal sealed class TestEntry
{
    public string? Name { get; set; }
    public string? Service { get; set; }
    public string? Method { get; set; }
    public string? ServerUrl { get; set; }
    public string? Protocol { get; set; }
    public List<string>? Messages { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
    public List<Assertion>? Assert { get; set; }
}

internal sealed class Assertion
{
    public string? Path { get; set; }
    public string? Op { get; set; }
    public string? Expected { get; set; }
}

// ---- Run report (used by HtmlReport too) ----

internal sealed class RunReport
{
    public string CollectionName { get; set; } = "";
    public string CollectionPath { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public long DurationMs { get; set; }
    public int TotalAssertions { get; set; }
    public int PassedAssertions { get; set; }
    public int FailedTests { get; set; }
    public List<TestResult> Tests { get; } = [];
}

internal sealed class TestResult
{
    public string Name { get; set; } = "";
    public string Service { get; set; } = "";
    public string Method { get; set; } = "";
    public long DurationMs { get; set; }
    public string? Status { get; set; }
    public string? Response { get; set; }
    public string? Error { get; set; }
    public List<AssertionResult> Assertions { get; } = [];
}

internal sealed class AssertionResult
{
    public string Path { get; set; } = "";
    public string Op { get; set; } = "";
    public string Expected { get; set; } = "";
    public string ActualText { get; set; } = "";
    public bool Passed { get; set; }
    public string? Error { get; set; }
}
