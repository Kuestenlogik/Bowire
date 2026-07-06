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
        var templatesOpt = new Option<string>("--templates") { Description = "Directory of *.json vulnerability templates to run (Bowire format)." };
        var templateOpt = new Option<string>("--template") { Description = "Single template *.json file to run (combinable with --templates)." };
        var nucleiOpt = new Option<string>("--nuclei") { Description = "Directory of *.yaml Nuclei templates (projectdiscovery/nuclei-templates). Read alongside --templates; resolved against --target so {{BaseURL}}/{{Hostname}} etc. land on real probes." };
        var outOpt = new Option<string>("--out") { Description = "Write findings as SARIF 2.1.0 JSON to this path (for CI dashboards: GitHub Code Scanning, GitLab, Azure DevOps)." };
        var suiteOpt = new Option<string>("--suite") { Description = "Run a named suite view after the scan. `owasp-api` rolls findings up against the OWASP API Security Top 10 (2023) and prints a per-entry covered / clean / vulnerable table." };
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
        var authHeaderBOpt = new Option<string[]>("--auth-header-b")
        {
            Description = "A SECOND identity's auth header(s), same shape as --auth-header. Enables the OWASP API1 BOLA check: the object at --target is read as identity A (--auth-header) and then as identity B; if B can read A's object while anonymous access is blocked, object-level authorization is missing. Repeatable.",
            AllowMultipleArgumentsPerToken = false,
        };

        scan.Add(targetOpt);
        scan.Add(templatesOpt);
        scan.Add(templateOpt);
        scan.Add(nucleiOpt);
        scan.Add(outOpt);
        scan.Add(suiteOpt);
        scan.Add(severityOpt);
        scan.Add(timeoutOpt);
        scan.Add(allowSelfSignedOpt);
        scan.Add(noBuiltinsOpt);
        scan.Add(scopeOpt);
        scan.Add(authHeaderOpt);
        scan.Add(authHeaderBOpt);

        scan.SetAction(async (pr, ct) =>
        {
            var options = new ScanOptions
            {
                Target = pr.GetValue(targetOpt) ?? "",
                Templates = pr.GetValue(templatesOpt),
                Template = pr.GetValue(templateOpt),
                Nuclei = pr.GetValue(nucleiOpt),
                OutSarif = pr.GetValue(outOpt),
                Suite = pr.GetValue(suiteOpt),
                MinSeverity = pr.GetValue(severityOpt),
                TimeoutSeconds = pr.GetValue(timeoutOpt) is int t and > 0 ? t : 30,
                AllowSelfSignedCerts = pr.GetValue(allowSelfSignedOpt),
                RunBuiltins = !pr.GetValue(noBuiltinsOpt),
                Scope = pr.GetValue(scopeOpt) ?? Array.Empty<string>(),
                AuthHeaders = pr.GetValue(authHeaderOpt) ?? Array.Empty<string>(),
                AuthHeadersB = pr.GetValue(authHeaderBOpt) ?? Array.Empty<string>(),
            };
            // Thread the InvocationConfiguration's Output / Error
            // writers through so production keeps writing to the real
            // Console (the framework defaults wire them up to
            // Console.Out / Console.Error) while tests + embedded
            // callers can pin their own TextWriter for capture without
            // touching process-global Console state.
            return await ScanCommand.RunAsync(
                options,
                ct,
                pr.InvocationConfiguration.Output,
                pr.InvocationConfiguration.Error).ConfigureAwait(false);
        });

        scan.Add(BuildSpider());
        return scan;
    }

    // `bowire scan spider --url <base>` (#176) — endpoint discovery. A
    // subcommand of `scan` so the security surface stays under one verb.
    private static Command BuildSpider()
    {
        var spider = new Command("spider",
            "Discover candidate endpoints from a base URL (robots.txt, sitemap.xml, an OpenAPI/Swagger document, a common-path HEAD sweep, and same-origin page links). Conservative: same-host/--scope only, honours robots.txt, never authenticates beyond --auth-header.");

        var urlOpt = new Option<string>("--url") { Description = "Base URL to crawl (e.g. https://api.example.com).", Required = true };
        var scopeOpt = new Option<string[]>("--scope") { Description = "In-scope hostname or `*.`-glob. Repeat / comma-separate. Defaults to the base URL's host.", AllowMultipleArgumentsPerToken = true };
        var authHeaderOpt = new Option<string[]>("--auth-header") { Description = "Header applied to every request, e.g. `Authorization: Bearer <token>`. Repeatable.", AllowMultipleArgumentsPerToken = false };
        var timeoutOpt = new Option<int>("--timeout") { Description = "Per-request HTTP timeout in seconds. Default 30." };
        var noRobotsOpt = new Option<bool>("--no-robots") { Description = "Ignore robots.txt Disallow rules (default: respected)." };
        var maxOpt = new Option<int>("--max") { Description = "Maximum candidates to collect. Default 500." };
        var allowSelfSignedOpt = new Option<bool>("--allow-self-signed-certs") { Description = "Accept self-signed / untrusted TLS on the target." };
        var outOpt = new Option<string>("--out") { Description = "Write the candidate list as JSON to this path." };

        spider.Add(urlOpt);
        spider.Add(scopeOpt);
        spider.Add(authHeaderOpt);
        spider.Add(timeoutOpt);
        spider.Add(noRobotsOpt);
        spider.Add(maxOpt);
        spider.Add(allowSelfSignedOpt);
        spider.Add(outOpt);

        spider.SetAction(async (pr, ct) =>
        {
            var options = new SpiderOptions
            {
                Url = pr.GetValue(urlOpt) ?? "",
                Scope = pr.GetValue(scopeOpt) ?? Array.Empty<string>(),
                AuthHeaders = pr.GetValue(authHeaderOpt) ?? Array.Empty<string>(),
                TimeoutSeconds = pr.GetValue(timeoutOpt) is int t and > 0 ? t : 30,
                RespectRobots = !pr.GetValue(noRobotsOpt),
                MaxCandidates = pr.GetValue(maxOpt) is int m and > 0 ? m : 500,
                AllowSelfSignedCerts = pr.GetValue(allowSelfSignedOpt),
                OutJson = pr.GetValue(outOpt),
            };
            return await SpiderCommand.RunAsync(
                options, ct, pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false);
        });

        return spider;
    }
}
