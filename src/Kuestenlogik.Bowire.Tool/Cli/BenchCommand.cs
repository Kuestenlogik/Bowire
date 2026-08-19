// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Globalization;
using Kuestenlogik.Bowire.Benchmarking;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire bench</c> — load a method and gate on latency budgets (#360).
/// <para>
/// The workbench has had a benchmark rail for a while, but its results
/// lived in the browser: a pipeline could not fail on a latency regression,
/// which is the one thing k6 is reached for. <c>bench run</c> puts the same
/// measurement on the command line and adds thresholds
/// (<c>--threshold "p95 &lt; 200"</c>) plus
/// <c>--fail-on-threshold</c> so CI can gate on them.
/// </para>
/// </summary>
internal static class BenchCommand
{
    private const int ExitOk = 0;
    private const int ExitFail = 1;
    private const int ExitUsage = 64;

    public static Command Build()
    {
        var bench = new Command("bench",
            "Load-test a method and gate on latency budgets. `run` measures p50/p95/p99, error rate and throughput, and can fail the build on a threshold.");
        bench.Add(BuildRunCommand());
        return bench;
    }

    private static Command BuildRunCommand()
    {
        var cmd = new Command("run", "Call a method repeatedly and report latency percentiles, error rate and throughput.");

        var targetArg = new Argument<string>("target")
        {
            Description = "service/method to call, e.g. Weather/getCurrent.",
        };
        var urlOpt = new Option<string>("-url", "--url")
        {
            Description = "Target server URL. Accepts the `protocol@url` hint form (e.g. rest@http://localhost:6000).",
            Required = true,
        };
        var protocolOpt = new Option<string?>("--protocol")
        {
            Description = "Protocol plugin id (rest, grpc, graphql, …). Overrides a `protocol@url` prefix.",
        };
        var dataOpt = new Option<string?>("-d", "--data")
        {
            Description = "JSON request body (or @filename).",
        };
        var iterationsOpt = new Option<int>("-n", "--iterations")
        {
            Description = "How many calls to make.",
            DefaultValueFactory = _ => 50,
        };
        var concurrencyOpt = new Option<int>("-c", "--concurrency")
        {
            Description = "How many calls may be in flight at once.",
            DefaultValueFactory = _ => 1,
        };
        var warmupOpt = new Option<int>("--warmup")
        {
            Description = "Calls made and discarded before measurement, to shake out JIT / connection setup.",
            DefaultValueFactory = _ => 0,
        };
        var headerOpt = new Option<string[]>("-H")
        {
            Description = "Metadata header \"key: value\". Repeatable.",
            AllowMultipleArgumentsPerToken = false,
        };
        var thresholdOpt = new Option<string[]>("--threshold")
        {
            Description = "Budget the run must satisfy, e.g. \"p95 < 200\", \"error-rate < 0.01\", \"throughput >= 50\". Repeatable. Metrics: p50/p90/p95/p99/avg/min/max/error-rate/throughput.",
            AllowMultipleArgumentsPerToken = false,
        };
        var failOnThresholdOpt = new Option<bool>("--fail-on-threshold")
        {
            Description = "Exit non-zero when any threshold is breached (CI gate).",
        };
        var k6Opt = new Option<string?>("--k6-summary")
        {
            Description = "Write the run as k6-summary JSON to this path — same shape the workbench exports, thresholds included.",
        };

        cmd.Add(targetArg); cmd.Add(urlOpt); cmd.Add(protocolOpt); cmd.Add(dataOpt);
        cmd.Add(iterationsOpt); cmd.Add(concurrencyOpt); cmd.Add(warmupOpt);
        cmd.Add(headerOpt); cmd.Add(thresholdOpt); cmd.Add(failOnThresholdOpt); cmd.Add(k6Opt);

        cmd.SetAction(async (pr, ct) =>
        {
            var io = pr.InvocationConfiguration;
            return await RunAsync(
                pr.GetValue(targetArg) ?? "",
                pr.GetValue(urlOpt) ?? "",
                pr.GetValue(protocolOpt),
                pr.GetValue(dataOpt),
                pr.GetValue(iterationsOpt),
                pr.GetValue(concurrencyOpt),
                pr.GetValue(warmupOpt),
                pr.GetValue(headerOpt) ?? [],
                pr.GetValue(thresholdOpt) ?? [],
                pr.GetValue(failOnThresholdOpt),
                pr.GetValue(k6Opt),
                io.Output, io.Error, ct).ConfigureAwait(false);
        });
        return cmd;
    }

    private static async Task<int> RunAsync(
        string target, string url, string? protocolId, string? data,
        int iterations, int concurrency, int warmup,
        string[] headers, string[] thresholdSpecs, bool failOnThreshold, string? k6Path,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var slash = target.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == target.Length - 1)
        {
            await stderr.WriteLineAsync("bowire bench run: target must be service/method, e.g. Weather/getCurrent.").ConfigureAwait(false);
            return ExitUsage;
        }
        var service = target[..slash];
        var method = target[(slash + 1)..];

        // Parse every threshold up front: a typo in the last of five
        // budgets should not surface after a two-minute load run.
        var thresholds = new List<BowireBenchmarkThreshold>();
        foreach (var spec in thresholdSpecs)
        {
            if (!BowireBenchmarkThreshold.TryParse(spec, out var threshold, out var error))
            {
                await stderr.WriteLineAsync($"bowire bench run: bad --threshold {error}.").ConfigureAwait(false);
                return ExitUsage;
            }
            thresholds.Add(threshold!);
        }

        var (resolvedUrl, resolvedProtocolId) = SplitProtocolHint(url, protocolId);
        if (string.IsNullOrWhiteSpace(resolvedProtocolId))
        {
            await stderr.WriteLineAsync("bowire bench run: --protocol is required (or use the protocol@url form).").ConfigureAwait(false);
            return ExitUsage;
        }

        var protocol = ResolveProtocol(resolvedProtocolId!);
        if (protocol is null)
        {
            await stderr.WriteLineAsync($"bowire bench run: no protocol plugin '{resolvedProtocolId}' is loaded.").ConfigureAwait(false);
            return ExitUsage;
        }

        var body = await ReadBodyAsync(data, ct).ConfigureAwait(false);
        var metadata = ParseHeaders(headers);

        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync(
            $"  Benchmark   {service}/{method}   {iterations} calls, concurrency {concurrency}"
            + (warmup > 0 ? $", {warmup} warm-up" : "")).ConfigureAwait(false);

        var run = await BowireBenchmarkRunner.RunAsync(protocol, new BowireBenchmarkRequest
        {
            ServerUrl = resolvedUrl,
            Service = service,
            Method = method,
            Body = body,
            Iterations = iterations,
            Concurrency = concurrency,
            Warmup = warmup,
            Metadata = metadata,
        }, ct).ConfigureAwait(false);

        await PrintSummaryAsync(stdout, run).ConfigureAwait(false);

        var results = thresholds.Select(t => t.Evaluate(run.Stats)).ToList();
        if (results.Count > 0) await PrintThresholdsAsync(stdout, results).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(k6Path))
        {
            try
            {
                await File.WriteAllTextAsync(k6Path!, BowireK6Summary.Render(run.Stats, results), ct).ConfigureAwait(false);
                await stdout.WriteLineAsync($"  k6 summary written to {k6Path}").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
            {
                await stderr.WriteLineAsync($"  could not write the k6 summary: {ex.Message}").ConfigureAwait(false);
            }
        }

        var breached = results.Count(r => !r.Ok);
        if (breached > 0 && failOnThreshold)
        {
            await stderr.WriteLineAsync($"bowire bench run: {breached} threshold(s) breached.").ConfigureAwait(false);
            return ExitFail;
        }
        return ExitOk;
    }

    private static async Task PrintSummaryAsync(TextWriter stdout, BowireBenchmarkRun run)
    {
        var s = run.Stats;
        await stdout.WriteLineAsync().ConfigureAwait(false);
        // Throughput is calls per second — not a duration, so it must not go
        // through the millisecond formatter.
        var rps = s.Throughput.ToString("0.#", CultureInfo.InvariantCulture);
        await stdout.WriteLineAsync(
            $"  {s.Count} ok · {s.Errors} failed · {rps} req/s · {run.ElapsedMs} ms total").ConfigureAwait(false);
        await stdout.WriteLineAsync(
            $"  min {Ms(s.Min)}   p50 {Ms(s.P50)}   p90 {Ms(s.P90)}   p95 {Ms(s.P95)}   p99 {Ms(s.P99)}   max {Ms(s.Max)}   avg {Ms(s.Avg)}").ConfigureAwait(false);
        if (s.Errors > 0 && !string.IsNullOrEmpty(run.FirstError))
        {
            await stdout.WriteLineAsync($"  first error: {run.FirstError}").ConfigureAwait(false);
        }
        await stdout.WriteLineAsync().ConfigureAwait(false);
    }

    private static async Task PrintThresholdsAsync(TextWriter stdout, IReadOnlyList<BowireThresholdResult> results)
    {
        await stdout.WriteLineAsync("  Thresholds").ConfigureAwait(false);
        foreach (var r in results)
        {
            // The breaching metric is marked and shows what it measured, so
            // the operator sees WHY it broke without re-reading the summary.
            var mark = r.Ok ? "PASS" : "FAIL";
            var actual = BowireBenchmarkThreshold.IsLatency(r.Threshold.Metric)
                ? Ms(r.Actual)
                : r.Actual.ToString("0.###", CultureInfo.InvariantCulture);
            await stdout.WriteLineAsync($"    {mark}  {r.Threshold}   (actual {actual})").ConfigureAwait(false);
        }
        await stdout.WriteLineAsync().ConfigureAwait(false);
    }

    private static string Ms(double value)
        => value >= 100
            ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + "ms"
            : value.ToString("0.##", CultureInfo.InvariantCulture) + "ms";

    /// <summary>
    /// Split the <c>protocol@url</c> hint form, honouring an explicit
    /// <c>--protocol</c>. The '@' only counts as a hint when it precedes the
    /// scheme separator: in <c>http://user@host</c> the '@' is userinfo, and
    /// treating "http://user" as a plugin id would mangle the URL.
    /// </summary>
    internal static (string Url, string? ProtocolId) SplitProtocolHint(string url, string? explicitId)
    {
        var at = url.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0) return (url, explicitId);

        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0 && at > scheme) return (url, explicitId);

        return (url[(at + 1)..], explicitId ?? url[..at]);
    }

    private static async Task<string?> ReadBodyAsync(string? data, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(data)) return null;
        if (!data.StartsWith('@')) return data;
        var path = data[1..];
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : data;
    }

    private static Dictionary<string, string>? ParseHeaders(string[] headers)
    {
        if (headers.Length == 0) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            var colon = header.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            map[header[..colon].Trim()] = header[(colon + 1)..].Trim();
        }
        return map.Count > 0 ? map : null;
    }

    private static IBowireProtocol? ResolveProtocol(string id)
    {
        var registry = BowireProtocolRegistry.Discover();
        return registry.Protocols.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
