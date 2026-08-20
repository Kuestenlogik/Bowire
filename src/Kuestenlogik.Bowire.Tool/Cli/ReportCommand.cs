// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Kuestenlogik.Bowire.Reporting;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire report</c> — read the artefacts Bowire already writes and answer
/// the portfolio question (#587).
/// <para>
/// Per-service findings are useful to the service team; the rolled-up view is
/// what a platform team needs. Everything it aggregates is already on disk —
/// lint findings, contract results, benchmark runs, scan SARIF, test JUnit —
/// so this reads rather than re-runs.
/// </para>
/// </summary>
internal static class ReportCommand
{
    private const int ExitOk = 0;
    private const int ExitFail = 1;
    private const int ExitUsage = 64;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Command Build()
    {
        var report = new Command("report",
            "Roll the reports Bowire writes up into one view across services.");
        report.Add(BuildRollupCommand());
        return report;
    }

    private static Command BuildRollupCommand()
    {
        var cmd = new Command("rollup",
            "Read lint / contract / benchmark / scan / test reports under the given paths and print one row per service.");

        var fromOpt = new Option<string[]>("--from")
        {
            Description = "File or directory to read. Repeatable. Directories are walked recursively.",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var jsonOpt = new Option<bool>("--json")
        {
            Description = "Emit the rollup as JSON instead of a table.",
        };
        var failOnOpt = new Option<string>("--fail-on")
        {
            Description = "Exit non-zero when a service is at or above this severity: 'none' (default), 'info', 'low', 'medium' or 'high'.",
            DefaultValueFactory = _ => "none",
        };
        var serviceOpt = new Option<string?>("--service")
        {
            Description = "Attribute every report to this service, instead of inferring it from the report and its path.",
        };

        cmd.Add(fromOpt); cmd.Add(jsonOpt); cmd.Add(failOnOpt); cmd.Add(serviceOpt);
        cmd.SetAction(async (pr, ct) =>
        {
            var io = pr.InvocationConfiguration;
            var roots = pr.GetValue(fromOpt) ?? [];
            if (roots.Length == 0)
            {
                await io.Error.WriteLineAsync("bowire report rollup: --from is required.").ConfigureAwait(false);
                return ExitUsage;
            }

            // Validate the gate before doing the work: a typo'd severity that
            // silently degrades to "never fail" is worse than no gate at all.
            if (!BowireRollupPayload.TryParseGate(pr.GetValue(failOnOpt), out var gate, out var gateError))
            {
                await io.Error.WriteLineAsync($"bowire report rollup: bad --fail-on — {gateError}.").ConfigureAwait(false);
                return ExitUsage;
            }

            var rollup = await BowireReportReader.ReadAsync(roots, pr.GetValue(serviceOpt), ct).ConfigureAwait(false);

            if (pr.GetValue(jsonOpt))
            {
                await io.Output.WriteLineAsync(
                    JsonSerializer.Serialize(BowireRollupPayload.ToWirePayload(rollup), JsonOpts)).ConfigureAwait(false);
            }
            else
            {
                await PrintTableAsync(io.Output, rollup).ConfigureAwait(false);
            }

            if (gate is not { } level) return ExitOk;
            var breaching = BowireRollupPayload.CountAtOrAbove(rollup, level);
            if (breaching == 0) return ExitOk;

            await io.Error.WriteLineAsync(
                $"bowire report rollup: {breaching} service(s) at or above {BowireRollupPayload.SeverityText(level)}.").ConfigureAwait(false);
            return ExitFail;
        });
        return cmd;
    }

    private static async Task PrintTableAsync(TextWriter stdout, BowireRollup rollup)
    {
        await stdout.WriteLineAsync().ConfigureAwait(false);

        if (rollup.Services.Count == 0)
        {
            await stdout.WriteLineAsync("  No Bowire reports found under the given paths.").ConfigureAwait(false);
            if (rollup.Skipped.Count > 0)
            {
                await stdout.WriteLineAsync($"  ({rollup.Skipped.Count} file(s) read but not recognised.)").ConfigureAwait(false);
            }
            return;
        }

        var nameWidth = Math.Max(7, rollup.Services.Max(s => s.Service.Length));
        await stdout.WriteLineAsync(
            $"  {"SERVICE".PadRight(nameWidth)}  {"WORST",-7}  {"LINT (H/M/L)",-13}  {"CONTRACTS",-10}  {"TESTS",-10}  {"P95",-8}  LAST").ConfigureAwait(false);

        foreach (var s in rollup.Services)
        {
            var worst = BowireRollupPayload.SeverityText(s.Worst)?.ToUpperInvariant() ?? "ok";
            var lint = s.LintHigh is null && s.LintMedium is null && s.LintLow is null
                ? "—"
                : $"{s.LintHigh ?? 0}/{s.LintMedium ?? 0}/{s.LintLow ?? 0}";
            var last = s.LastReportAt is { } stamp
                ? stamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "—";

            await stdout.WriteLineAsync(
                $"  {s.Service.PadRight(nameWidth)}  {worst,-7}  {lint,-13}  "
                + $"{BowireRollupPayload.Cell(s.ContractsPassed, s.ContractsTotal),-10}  "
                + $"{BowireRollupPayload.Cell(s.TestsPassed, s.TestsTotal),-10}  "
                + $"{BowireRollupPayload.Latency(s.P95Ms),-8}  {last}").ConfigureAwait(false);

            if (s.ScanErrors > 0)
            {
                await stdout.WriteLineAsync(
                    $"  {new string(' ', nameWidth)}  scan: {s.ScanErrors} finding(s) at error level").ConfigureAwait(false);
            }
        }

        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync(
            $"  {rollup.Services.Count} service(s) · {rollup.ServicesAtHigh} at high · {rollup.ServicesClean} clean"
            + (rollup.Skipped.Count > 0 ? $" · {rollup.Skipped.Count} file(s) skipped" : "")).ConfigureAwait(false);
        await stdout.WriteLineAsync().ConfigureAwait(false);
    }
}
