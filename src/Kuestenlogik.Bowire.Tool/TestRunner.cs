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

    private static bool UseColor => !Console.IsOutputRedirected;

    public static async Task<int> RunAsync(TestCliOptions cli)
    {
        ArgumentNullException.ThrowIfNull(cli);

        if (string.IsNullOrEmpty(cli.CollectionPath))
        {
            WriteError("Usage: bowire test <collection.json> [--report path.html] [--junit path.xml]");
            return 2;
        }

        if (!File.Exists(cli.CollectionPath))
        {
            WriteError($"Collection file not found: {cli.CollectionPath}");
            return 2;
        }

        TestCollection? collection;
        try
        {
            var json = await File.ReadAllTextAsync(cli.CollectionPath);
            collection = JsonSerializer.Deserialize<TestCollection>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            WriteError($"Failed to parse collection: {ex.Message}");
            return 2;
        }

        if (collection is null || collection.Tests is null || collection.Tests.Count == 0)
        {
            WriteError("Collection has no tests.");
            return 2;
        }

        // Initialize the protocol registry once and reuse for every test
        var registry = BowireProtocolRegistry.Discover();
        // Don't initialize protocols with a service provider — standalone mode

        Console.WriteLine();
        WriteHeader($"Bowire Test Runner   collection: {Path.GetFileName(cli.CollectionPath)}");
        Console.WriteLine();

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

            PrintTestResult(result);
        }

        sw.Stop();
        report.DurationMs = sw.ElapsedMilliseconds;
        report.PassedAssertions = passedAssertions;
        report.TotalAssertions = totalAssertions;
        report.FailedTests = failedTests;

        Console.WriteLine();
        var summaryText = $"  {report.Tests.Count - failedTests}/{report.Tests.Count} tests passed   "
            + $"{passedAssertions}/{totalAssertions} assertions   "
            + $"in {sw.ElapsedMilliseconds} ms";
        Console.WriteLine(failedTests > 0 ? Red(summaryText) : Green(summaryText));
        Console.WriteLine();

        if (!string.IsNullOrEmpty(cli.ReportPath))
        {
            try
            {
                await File.WriteAllTextAsync(cli.ReportPath, HtmlReport.Render(report));
                Console.WriteLine($"  HTML report written to {cli.ReportPath}");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                WriteError($"Failed to write report: {ex.Message}");
            }
        }

        if (!string.IsNullOrEmpty(cli.JUnitPath))
        {
            try
            {
                await File.WriteAllTextAsync(cli.JUnitPath, JUnitReport.Render(report));
                Console.WriteLine($"  JUnit XML written to {cli.JUnitPath}");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                WriteError($"Failed to write JUnit report: {ex.Message}");
            }
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
        try
        {
            await protocol.DiscoverAsync(serverUrl, showInternalServices: false);
        }
        catch (Exception ex)
        {
            result.Error = $"Discovery failed: {ex.Message}";
            return result;
        }

        // Invoke
        var sw = Stopwatch.StartNew();
        InvokeResult? invocation;
        try
        {
            invocation = await protocol.InvokeAsync(serverUrl, test.Service, test.Method, messages, false, metadata);
        }
        catch (Exception ex)
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
        catch (Exception ex)
        {
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

        // Numeric coercion
        if (TryNumber(actual, out var a) && TryNumber(expected, out var e)) return a == e;

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

    private static void PrintTestResult(TestResult result)
    {
        var status = result.Error is null && result.Assertions.All(a => a.Passed) ? Green("PASS") : Red("FAIL");
        Console.WriteLine($"  {status}  {Bold(result.Name)}   {Dim(result.Status + " · " + result.DurationMs + "ms")}");

        if (result.Error is not null)
        {
            Console.WriteLine($"        {Red("error: " + result.Error)}");
            return;
        }

        foreach (var a in result.Assertions)
        {
            var icon = a.Passed ? Green("✓") : Red("✗");
            Console.Write($"        {icon} {Dim(a.Path + " " + a.Op + " " + Quote(a.Expected))}");
            if (!a.Passed)
            {
                if (a.Error is not null)
                    Console.Write($"   {Red(a.Error)}");
                else
                    Console.Write($"   {Red("actual: " + Quote(a.ActualText))}");
            }
            Console.WriteLine();
        }
    }

    private static string Quote(string s)
    {
        if (s.Length > 60) s = string.Concat(s.AsSpan(0, 60), "…");
        return s;
    }

    private static void WriteHeader(string text)
    {
        Console.WriteLine("  " + Bold(Cyan(text)));
    }

    private static void WriteError(string text)
    {
        Console.Error.WriteLine(Red("error: " + text));
    }

    private static string Cyan(string text)  => UseColor ? $"\x1b[36m{text}\x1b[0m" : text;
    private static string Bold(string text)  => UseColor ? $"\x1b[1m{text}\x1b[0m"  : text;
    private static string Dim(string text)   => UseColor ? $"\x1b[2m{text}\x1b[0m"  : text;
    private static string Green(string text) => UseColor ? $"\x1b[32m{text}\x1b[0m" : text;
    private static string Red(string text)   => UseColor ? $"\x1b[31m{text}\x1b[0m" : text;
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
