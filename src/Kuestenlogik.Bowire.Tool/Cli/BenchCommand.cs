// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
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
            "Load-test a method and gate on latency budgets. `run` measures p50/p95/p99, error rate and throughput and can fail the build on a threshold; `schedule` manages recurring runs.");
        bench.Add(BuildRunCommand());
        bench.Add(BuildScheduleCommand());
        return bench;
    }

    // -------------------- schedule --------------------

    /// <summary>
    /// #232 — the CLI half of scheduled runs. Authoring lives here rather
    /// than in the workbench because a schedule carries a target the server
    /// then calls unattended; the browser reads and pauses.
    /// </summary>
    private static Command BuildScheduleCommand()
    {
        var schedule = new Command("schedule",
            "Manage recurring benchmark runs. Entries are stored under .bowire/benchmark-schedules and fire from the running workbench / host.");
        schedule.Add(BuildScheduleListCommand());
        schedule.Add(BuildScheduleAddCommand());
        schedule.Add(BuildSchedulePauseCommand(pause: true));
        schedule.Add(BuildSchedulePauseCommand(pause: false));
        schedule.Add(BuildScheduleRemoveCommand());
        return schedule;
    }

    private static Command BuildScheduleListCommand()
    {
        var cmd = new Command("list", "List stored schedules with their next firing time and last result.");
        var jsonOpt = new Option<bool>("--json") { Description = "Emit JSON instead of a table." };
        cmd.Add(jsonOpt);
        cmd.SetAction(async (pr, ct) =>
        {
            var io = pr.InvocationConfiguration;
            var store = new BowireBenchmarkScheduleStore();
            var schedules = await store.LoadAllAsync(ct).ConfigureAwait(false);
            if (schedules.Count == 0)
            {
                await io.Output.WriteLineAsync(
                    $"  No schedules in {store.Directory}. Add one with `bowire bench schedule add`.").ConfigureAwait(false);
                return ExitOk;
            }

            var now = DateTime.UtcNow;
            if (pr.GetValue(jsonOpt))
            {
                var rows = new List<object>(schedules.Count);
                foreach (var s in schedules)
                {
                    var runs = await store.LoadRunsAsync(s.Id, ct).ConfigureAwait(false);
                    rows.Add(BowireBenchmarkScheduleEndpoints.ToPayload(s, runs, now));
                }
                await io.Output.WriteLineAsync(JsonSerializer.Serialize(rows, ScheduleJsonOpts)).ConfigureAwait(false);
                return ExitOk;
            }

            await io.Output.WriteLineAsync().ConfigureAwait(false);
            foreach (var s in schedules)
            {
                var runs = await store.LoadRunsAsync(s.Id, ct).ConfigureAwait(false);
                var next = s.NextOccurrenceUtc(now);
                // A paused or unparseable schedule has no next time; say which
                // rather than printing a blank column.
                var nextText = !s.Enabled ? "paused"
                    : next is null ? "invalid cron"
                    : next.Value.ToString("u", CultureInfo.InvariantCulture);
                var last = runs.Count > 0 ? runs[0] : null;
                var lastText = last is null
                    ? "never run"
                    : $"{(last.Passed ? "PASS" : "FAIL")} p95 {Ms(last.P95)} · {last.StartedAt:u} ({last.TriggeredBy})";

                await io.Output.WriteLineAsync($"  {s.Id}   {s.Name}").ConfigureAwait(false);
                await io.Output.WriteLineAsync($"      {s.Cron} [{(string.IsNullOrWhiteSpace(s.Timezone) ? "UTC" : s.Timezone)}]   {s.Service}/{s.Method} @ {s.ServerUrl}").ConfigureAwait(false);
                await io.Output.WriteLineAsync($"      next: {nextText}   last: {lastText}").ConfigureAwait(false);
                await io.Output.WriteLineAsync().ConfigureAwait(false);
            }
            return ExitOk;
        });
        return cmd;
    }

    private static Command BuildScheduleAddCommand()
    {
        var cmd = new Command("add", "Store a recurring benchmark run.");
        var idArg = new Argument<string>("id") { Description = "Schedule id — the handle for pause / remove." };
        var cronOpt = new Option<string>("--cron") { Description = "Cron expression (5 fields), e.g. \"0 3 * * *\".", Required = true };
        var tzOpt = new Option<string?>("--timezone") { Description = "IANA timezone the cron is read in (e.g. Europe/Berlin). Default UTC." };
        var nameOpt = new Option<string?>("--name") { Description = "Display name." };
        var targetOpt = new Option<string>("--target") { Description = "service/method to call.", Required = true };
        var urlOpt = new Option<string>("-url", "--url") { Description = "Target server URL (protocol@url form accepted).", Required = true };
        var protocolOpt = new Option<string?>("--protocol") { Description = "Protocol plugin id." };
        var dataOpt = new Option<string?>("-d", "--data") { Description = "JSON request body (or @filename)." };
        var iterationsOpt = new Option<int>("-n", "--iterations") { Description = "Calls per run.", DefaultValueFactory = _ => 50 };
        var concurrencyOpt = new Option<int>("-c", "--concurrency") { Description = "Calls in flight at once.", DefaultValueFactory = _ => 1 };
        var warmupOpt = new Option<int>("--warmup") { Description = "Discarded calls before measuring.", DefaultValueFactory = _ => 0 };
        var thresholdOpt = new Option<string[]>("--threshold") { Description = "Budget checked after each run. Repeatable.", AllowMultipleArgumentsPerToken = false };

        cmd.Add(idArg); cmd.Add(cronOpt); cmd.Add(tzOpt); cmd.Add(nameOpt); cmd.Add(targetOpt);
        cmd.Add(urlOpt); cmd.Add(protocolOpt); cmd.Add(dataOpt);
        cmd.Add(iterationsOpt); cmd.Add(concurrencyOpt); cmd.Add(warmupOpt); cmd.Add(thresholdOpt);

        cmd.SetAction(async (pr, ct) =>
        {
            var io = pr.InvocationConfiguration;
            var target = pr.GetValue(targetOpt) ?? "";
            var slash = target.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || slash == target.Length - 1)
            {
                await io.Error.WriteLineAsync("bowire bench schedule add: --target must be service/method.").ConfigureAwait(false);
                return ExitUsage;
            }

            var (url, protocolId) = SplitProtocolHint(pr.GetValue(urlOpt) ?? "", pr.GetValue(protocolOpt));
            if (string.IsNullOrWhiteSpace(protocolId))
            {
                await io.Error.WriteLineAsync("bowire bench schedule add: --protocol is required (or use the protocol@url form).").ConfigureAwait(false);
                return ExitUsage;
            }

            var schedule = new BowireBenchmarkSchedule
            {
                Id = pr.GetValue(idArg) ?? "",
                Name = pr.GetValue(nameOpt) ?? pr.GetValue(idArg) ?? "",
                Cron = pr.GetValue(cronOpt) ?? "",
                Timezone = pr.GetValue(tzOpt) ?? "",
                ServerUrl = url,
                Protocol = protocolId!,
                Service = target[..slash],
                Method = target[(slash + 1)..],
                Body = await ReadBodyAsync(pr.GetValue(dataOpt), ct).ConfigureAwait(false),
                Iterations = pr.GetValue(iterationsOpt),
                Concurrency = pr.GetValue(concurrencyOpt),
                Warmup = pr.GetValue(warmupOpt),
            };
            schedule.Thresholds.AddRange(pr.GetValue(thresholdOpt) ?? []);

            // Validate before storing: a schedule that can never fire is
            // worse than a rejected one, because nothing reports it later.
            if (!schedule.TryGetCronExpression(out _, out var cronError))
            {
                await io.Error.WriteLineAsync($"bowire bench schedule add: bad --cron: {cronError}").ConfigureAwait(false);
                return ExitUsage;
            }
            foreach (var spec in schedule.Thresholds)
            {
                if (!BowireBenchmarkThreshold.TryParse(spec, out _, out var thresholdError))
                {
                    await io.Error.WriteLineAsync($"bowire bench schedule add: bad --threshold {thresholdError}.").ConfigureAwait(false);
                    return ExitUsage;
                }
            }

            var store = new BowireBenchmarkScheduleStore();
            var path = await store.SaveAsync(schedule, ct).ConfigureAwait(false);
            var next = schedule.NextOccurrenceUtc(DateTime.UtcNow);
            await io.Output.WriteLineAsync($"  Stored {schedule.Id} at {path}").ConfigureAwait(false);
            await io.Output.WriteLineAsync(
                $"  Next run: {(next is null ? "—" : next.Value.ToString("u", CultureInfo.InvariantCulture))}").ConfigureAwait(false);
            return ExitOk;
        });
        return cmd;
    }

    private static Command BuildSchedulePauseCommand(bool pause)
    {
        var cmd = new Command(pause ? "pause" : "resume",
            pause ? "Stop a schedule from firing (the entry is kept)." : "Let a paused schedule fire again.");
        var idArg = new Argument<string>("id") { Description = "Schedule id." };
        cmd.Add(idArg);
        cmd.SetAction(async (pr, ct) =>
        {
            var io = pr.InvocationConfiguration;
            var id = pr.GetValue(idArg) ?? "";
            var store = new BowireBenchmarkScheduleStore();
            var schedule = await store.LoadAsync(id, ct).ConfigureAwait(false);
            if (schedule is null)
            {
                await io.Error.WriteLineAsync($"bowire bench schedule: '{id}' is not stored.").ConfigureAwait(false);
                return ExitUsage;
            }
            schedule.Enabled = !pause;
            await store.SaveAsync(schedule, ct).ConfigureAwait(false);
            await io.Output.WriteLineAsync($"  {id} is now {(pause ? "paused" : "active")}.").ConfigureAwait(false);
            return ExitOk;
        });
        return cmd;
    }

    private static Command BuildScheduleRemoveCommand()
    {
        var cmd = new Command("remove", "Delete a schedule and its run history.");
        var idArg = new Argument<string>("id") { Description = "Schedule id." };
        cmd.Add(idArg);
        cmd.SetAction(async (pr, _) =>
        {
            var io = pr.InvocationConfiguration;
            var id = pr.GetValue(idArg) ?? "";
            var removed = new BowireBenchmarkScheduleStore().Delete(id);
            await io.Output.WriteLineAsync(
                removed ? $"  Removed {id}." : $"  '{id}' is not stored.").ConfigureAwait(false);
            return removed ? ExitOk : ExitUsage;
        });
        return cmd;
    }

    private static readonly JsonSerializerOptions ScheduleJsonOpts = new() { WriteIndented = true };

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

    internal static string Ms(double value)
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

    internal static async Task<string?> ReadBodyAsync(string? data, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(data)) return null;
        if (!data.StartsWith('@')) return data;
        var path = data[1..];
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : data;
    }

    internal static Dictionary<string, string>? ParseHeaders(string[] headers)
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
