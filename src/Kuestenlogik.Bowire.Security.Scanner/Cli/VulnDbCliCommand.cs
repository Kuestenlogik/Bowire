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
            "Manage the local vulnerability-template cache (~/.bowire/vulndb) that `bowire scan` reads by default. See docs/architecture/security-testing.md.");
        vulndb.Add(BuildUpdate());
        vulndb.Add(BuildList());
        return vulndb;
    }

    private static Command BuildUpdate()
    {
        var update = new Command("update",
            "Fetch / refresh the curated Bowire.VulnDb template set into ~/.bowire/vulndb. With no --source this pulls the latest GitHub release (the only outbound call, and only because you asked). Point --source at a repo checkout, a .tar.gz, or a URL for air-gapped / pinned installs.");

        var sourceOpt = new Option<string>("--source") { Description = "Template source: a directory (repo checkout / mirror), a .tar.gz file, or an http(s) URL to a tarball. Omit to fetch the latest Kuestenlogik/Bowire.VulnDb GitHub release." };
        var destOpt = new Option<string>("--dest") { Description = "Cache directory to write into. Default: ~/.bowire/vulndb." };
        var refOpt = new Option<string>("--ref") { Description = "Release tag to pin when fetching from GitHub (e.g. v0.1.0). Ignored for --source directory/file/URL. Default: the latest release." };

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
            "List the templates in the local cache (~/.bowire/vulndb) — protocol, severity, id, and name, plus a total. Reads the templates-index.json sidecar when present, else walks the tree.");

        var destOpt = new Option<string>("--dest") { Description = "Cache directory to read. Default: ~/.bowire/vulndb." };
        var protocolOpt = new Option<string>("--protocol") { Description = "Only list templates under this protocol folder (grpc / graphql / rest / odata / …)." };

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
