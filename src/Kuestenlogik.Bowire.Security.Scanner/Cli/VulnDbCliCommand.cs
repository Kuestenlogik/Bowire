// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.Cli;

namespace Kuestenlogik.Bowire.Security.Scanner.Cli;

/// <summary>
/// Discoverable CLI contribution for <c>bowire vulndb</c> — manage the local
/// vulnerability-template cache (<c>~/.bowire/vulndb</c>) the scanner reads by
/// default. Two subcommands: <c>update</c> (fetch/refresh the curated
/// <c>Kuestenlogik/Bowire.VulnDb</c> template set) and <c>list</c> (show
/// what's cached). Picked up by the Tool's assembly scan via
/// <see cref="BowireCliCommandRegistry"/>; zero-config ctor by contract.
/// </summary>
public sealed class VulnDbCliCommand : IBowireCliCommand
{
    public string Id => "vulndb";

    public Command Build()
    {
        var vulndb = new Command("vulndb",
            BowireLocale.T("cli.vuln-db.vulndb.command"));
        vulndb.Add(BuildUpdate());
        vulndb.Add(BuildList());
        return vulndb;
    }

    private static Command BuildUpdate()
    {
        var update = new Command("update",
            BowireLocale.T("cli.vuln-db.update.command"));

        var sourceOpt = new Option<string>("--source") { Description = BowireLocale.T("cli.vuln-db.source") };
        var destOpt = new Option<string>("--dest") { Description = BowireLocale.T("cli.vuln-db.dest") };
        var refOpt = new Option<string>("--ref") { Description = BowireLocale.T("cli.vuln-db.ref") };

        update.Add(sourceOpt);
        update.Add(destOpt);
        update.Add(refOpt);

        update.SetAction(async (pr, ct) =>
        {
            var options = new VulnDbUpdateOptions
            {
                Source = pr.GetValue(sourceOpt),
                Dest = pr.GetValue(destOpt),
                Ref = pr.GetValue(refOpt),
            };
            return await VulnDbUpdateCommand.RunAsync(
                options, ct, pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false);
        });

        return update;
    }

    private static Command BuildList()
    {
        var list = new Command("list",
            BowireLocale.T("cli.vuln-db.list.command"));

        var destOpt = new Option<string>("--dest") { Description = BowireLocale.T("cli.vuln-db.dest-2") };
        var protocolOpt = new Option<string>("--protocol") { Description = BowireLocale.T("cli.vuln-db.protocol") };

        list.Add(destOpt);
        list.Add(protocolOpt);

        list.SetAction(async (pr, ct) =>
        {
            var options = new VulnDbListOptions
            {
                Dest = pr.GetValue(destOpt),
                Protocol = pr.GetValue(protocolOpt),
            };
            return await VulnDbListCommand.RunAsync(
                options, ct, pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false);
        });

        return list;
    }
}
