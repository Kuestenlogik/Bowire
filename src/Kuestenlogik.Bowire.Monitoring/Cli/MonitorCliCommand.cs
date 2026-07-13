// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.Cli;

namespace Kuestenlogik.Bowire.Monitoring.Cli;

/// <summary>
/// Discoverable CLI contribution for <c>bowire monitor</c> (#102). The Tool's
/// <c>BowireCli</c> picks this up via the <see cref="BowireCliCommandRegistry"/>
/// assembly scan and attaches it to the root — the command lives with its
/// feature package, not in the Tool. Zero-config (no ctor params) so the
/// registry can <c>Activator.CreateInstance</c> it.
/// </summary>
public sealed class MonitorCliCommand : IBowireCliCommand
{
    public string Id => "monitor";

    public Command Build()
    {
        var monitor = new Command("monitor",
            "Passive monitoring — run saved recordings as scheduled health probes, record every outcome, and alert on pass↔fail transitions (Postman Monitors / AWS Synthetics analog).");
        monitor.Add(BuildRun());
        return monitor;
    }

    private static Command BuildRun()
    {
        var run = new Command("run",
            "Load probe file(s) and run them on their schedule, writing every outcome to the ledger and signalling on transitions. Ctrl+C stops.");

        var filesArg = new Argument<string[]>("probe-files")
        {
            Description = "One or more probe definition files (JSON: { name, schedule, severity, assertions, recording }).",
            Arity = ArgumentArity.OneOrMore,
        };
        var ledgerOpt = new Option<string>("--ledger-root")
        {
            Description = "Directory for the append-only outcome ledgers. Default ~/.bowire/monitoring.",
        };
        var onceOpt = new Option<bool>("--once")
        {
            Description = "Run each probe exactly once and exit instead of looping — for CI / smoke tests. Exit code 2 when any probe fails or errors.",
        };

        run.Add(filesArg);
        run.Add(ledgerOpt);
        run.Add(onceOpt);

        run.SetAction(async (pr, ct) => await MonitorRunCommand.RunAsync(
            new MonitorRunOptions
            {
                ProbeFiles = pr.GetValue(filesArg) ?? [],
                LedgerRoot = pr.GetValue(ledgerOpt),
                Once = pr.GetValue(onceOpt),
            },
            pr.InvocationConfiguration.Output,
            pr.InvocationConfiguration.Error,
            ct).ConfigureAwait(false));

        return run;
    }
}
