// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.Cli;

namespace Kuestenlogik.Bowire.Security.Scanner.Cli;

/// <summary>
/// Discoverable CLI contribution for <c>bowire scan</c>. The Tool
/// project's <c>BowireCli</c> picks this up via
/// <see cref="BowireCliCommandRegistry"/>'s assembly scan and attaches
/// the built command to its root — no hard ProjectReference / no
/// manual registration in the Tool wiring required.
///
/// Public so the assembly-scan registry can <c>Activator.CreateInstance</c>
/// it. Has no constructor parameters by design — CLI-plugin contracts
/// stay zero-config to keep the registry mechanism trivial.
/// </summary>
public sealed class ScanCliCommand : IBowireCliCommand
{
    public string Id => "scan";

    public Command Build()
    {
        var scan = new Command("scan",
            "Run vulnerability templates against a target URL. The Tier-1 anchor of the security-testing lane (see docs/architecture/security-testing.md).");

        var targetOpt = new Option<string>("--target") { Description = "Target base URL (e.g. https://api.example.com).", Required = true };
        var templatesOpt = new Option<string>("--templates") { Description = "Directory of *.json vulnerability templates to run." };
        var templateOpt = new Option<string>("--template") { Description = "Single template *.json file to run (combinable with --templates)." };
        var outOpt = new Option<string>("--out") { Description = "Write findings as SARIF 2.1.0 JSON to this path (for CI dashboards: GitHub Code Scanning, GitLab, Azure DevOps)." };
        var severityOpt = new Option<string>("--severity") { Description = "Minimum severity to report: low / medium / high / critical. Lower-severity templates still load but are reported as skipped." };
        var timeoutOpt = new Option<int>("--timeout") { Description = "Per-probe HTTP timeout in seconds. Default 30." };
        var allowSelfSignedOpt = new Option<bool>("--allow-self-signed-certs") { Description = "Accept self-signed / untrusted TLS certs on the target. Off by default — use only when probing a known dev/staging cert." };
        var noBuiltinsOpt = new Option<bool>("--no-builtins") { Description = "Skip the built-in passive checks (TLS-version enumeration, version-disclosing headers, verbose-error detection). Built-ins run by default." };
        var scopeOpt = new Option<string[]>("--scope")
        {
            Description = "In-scope hostname or glob (e.g. `api.example.com` or `*.example.com` — the leading `*.` matches sub-domains but NOT the apex). Repeat or comma-separate. Defaults to the target's own host, so accidental cross-host probes are blocked unless explicitly widened.",
            AllowMultipleArgumentsPerToken = true,
        };
        var authHeaderOpt = new Option<string[]>("--auth-header")
        {
            Description = "Add an HTTP header to every probe — typically `Authorization: Bearer <token>` or `X-Api-Key: <key>`. Repeatable for multiple headers (cookies, multi-header auth schemes). Without this flag, scans of authenticated APIs land on the login wall and the scanner reports misleading 'endpoint missing' findings.",
            AllowMultipleArgumentsPerToken = false,
        };

        scan.Add(targetOpt);
        scan.Add(templatesOpt);
        scan.Add(templateOpt);
        scan.Add(outOpt);
        scan.Add(severityOpt);
        scan.Add(timeoutOpt);
        scan.Add(allowSelfSignedOpt);
        scan.Add(noBuiltinsOpt);
        scan.Add(scopeOpt);
        scan.Add(authHeaderOpt);

        scan.SetAction(async (pr, ct) =>
        {
            var options = new ScanOptions
            {
                Target = pr.GetValue(targetOpt) ?? "",
                Templates = pr.GetValue(templatesOpt),
                Template = pr.GetValue(templateOpt),
                OutSarif = pr.GetValue(outOpt),
                MinSeverity = pr.GetValue(severityOpt),
                TimeoutSeconds = pr.GetValue(timeoutOpt) is int t and > 0 ? t : 30,
                AllowSelfSignedCerts = pr.GetValue(allowSelfSignedOpt),
                RunBuiltins = !pr.GetValue(noBuiltinsOpt),
                Scope = pr.GetValue(scopeOpt) ?? Array.Empty<string>(),
                AuthHeaders = pr.GetValue(authHeaderOpt) ?? Array.Empty<string>(),
            };
            return await ScanCommand.RunAsync(options, ct).ConfigureAwait(false);
        });

        return scan;
    }
}
