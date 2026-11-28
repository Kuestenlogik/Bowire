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
            BowireLocale.T("cli.monitor.monitor.command"));
        monitor.Add(BuildRun());
        return monitor;
    }

    private static Command BuildRun()
    {
        var run = new Command("run",
            BowireLocale.T("cli.monitor.run.command"));

        var filesArg = new Argument<string[]>("probe-files")
        {
            Description = BowireLocale.T("cli.monitor.probe-files"),
            Arity = ArgumentArity.OneOrMore,
        };
        var ledgerOpt = new Option<string>("--ledger-root")
        {
            Description = BowireLocale.T("cli.monitor.ledger-root"),
        };
        var onceOpt = new Option<bool>("--once")
        {
            Description = BowireLocale.T("cli.monitor.once"),
        };
        var signalOpt = new Option<string[]>("--signal")
        {
            Description = BowireLocale.T("cli.monitor.signal"),
            AllowMultipleArgumentsPerToken = false,
        };

        run.Add(filesArg);
        run.Add(ledgerOpt);
        run.Add(onceOpt);
        run.Add(signalOpt);

        run.SetAction(async (pr, ct) => await MonitorRunCommand.RunAsync(
            new MonitorRunOptions
            {
                ProbeFiles = pr.GetValue(filesArg) ?? [],
                LedgerRoot = pr.GetValue(ledgerOpt),
                Once = pr.GetValue(onceOpt),
                Signals = pr.GetValue(signalOpt) ?? [],
            },
            pr.InvocationConfiguration.Output,
            pr.InvocationConfiguration.Error,
            ct).ConfigureAwait(false));

        return run;
    }
}
