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
        var cveDbOpt = new Option<string>("--cve-db") { Description = "Path to a CVE / VulnDb JSON file for the banner CVE-lookup (Server / X-Powered-By version → known CVEs). Runs with the built-in passive checks; defaults to a small built-in seed when omitted." };
        var oastServerOpt = new Option<string>("--oast-server") { Description = "URL of an interactsh-compatible interaction server for out-of-band detection, e.g. https://oast.example.com. Enables templates that prove a BLIND finding (SSRF / RCE / XXE) by planting a callback host and checking whether the target contacted it — the response itself shows nothing. Points at any instance: your own (`bowire oast serve` / interactsh-server) or a third party's. OFF by default: without it the scanner makes no outbound call beyond the target, and out-of-band templates are skipped rather than reported as clean." };
        var oastWaitOpt = new Option<int>("--oast-wait") { Description = "Seconds to wait after the probes before polling for callbacks. A target's DNS lookup / fetch arrives asynchronously, so polling immediately would miss it and call the target clean. Default 5." };
        var authFlowOpt = new Option<string>("--auth-flow") { Description = "Path to a headless auth-flow JSON file (login → token chain). Runs once before the scan; the captured token is injected as an auth header into every probe (refresh on expiry). Secrets are read from {{env.NAME}}, never inlined. Covers scriptable grants (client-credentials, password, refresh) — browser grants (OAuth auth-code/device) are not yet supported." };
        var outOpt = new Option<string>("--out") { Description = "Write findings as SARIF 2.1.0 JSON to this path (for CI dashboards: GitHub Code Scanning, GitLab, Azure DevOps)." };
        var suiteOpt = new Option<string>("--suite") { Description = "Run a named suite after the scan. `owasp-api` = OWASP API Top 10 rollup (HTTP + protocol probes) with a per-entry coverage table; `protocol` = only the protocol-specific probes (gRPC/GraphQL/WS/MQTT/SSE/MCP) + the table — use for non-HTTP targets like mqtt:// or ws://; `all` = everything (HTTP OWASP + protocol probes) + the table." };
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
        var activeOpt = new Option<bool>("--active") { Description = "Enable the ACTIVE (mutating / aggressive) probe tier (#395–#400) — OFF by default. Active probes may PUBLISH to the target (MQTT retained-message poisoning), hold connections open, or open many streams. Each namespaces + cleans up its side effects, but this deliberately mutates the target: use only against systems you are authorised to test." };
        var activeDurationOpt = new Option<int>("--active-duration") { Description = "Wall-clock budget in seconds for time-based active probes (slow-loris / slow-consumption). Default 15." };
        var activeConcurrencyOpt = new Option<int>("--active-concurrency") { Description = "Concurrency budget (N) for fan-out active probes (gRPC concurrent-stream). The verdict is honest about the number reached. Default 100." };
        var activeExpectedTopicOpt = new Option<string[]>("--active-expected-topic")
        {
            Description = "Expected-topic scope for the MQTT wildcard-subscribe active probe (#396) — the topics an authenticated client is meant to reach. Delivered traffic outside this set is flagged. Repeatable / comma-separated.",
            AllowMultipleArgumentsPerToken = true,
        };

        scan.Add(targetOpt);
        scan.Add(templatesOpt);
        scan.Add(templateOpt);
        scan.Add(nucleiOpt);
        scan.Add(cveDbOpt);
        scan.Add(oastServerOpt);
        scan.Add(oastWaitOpt);
        scan.Add(authFlowOpt);
        scan.Add(outOpt);
        scan.Add(suiteOpt);
        scan.Add(severityOpt);
        scan.Add(timeoutOpt);
        scan.Add(allowSelfSignedOpt);
        scan.Add(noBuiltinsOpt);
        scan.Add(scopeOpt);
        scan.Add(authHeaderOpt);
        scan.Add(authHeaderBOpt);
        scan.Add(activeOpt);
        scan.Add(activeDurationOpt);
        scan.Add(activeConcurrencyOpt);
        scan.Add(activeExpectedTopicOpt);

        scan.SetAction(async (pr, ct) =>
        {
            var options = new ScanOptions
            {
                Target = pr.GetValue(targetOpt) ?? "",
                Templates = pr.GetValue(templatesOpt),
                Template = pr.GetValue(templateOpt),
                Nuclei = pr.GetValue(nucleiOpt),
                CveDbPath = pr.GetValue(cveDbOpt),
                OastServer = pr.GetValue(oastServerOpt),
                OastWaitSeconds = pr.GetValue(oastWaitOpt) is int w and >= 0 ? w : 5,
                AuthFlowPath = pr.GetValue(authFlowOpt),
                OutSarif = pr.GetValue(outOpt),
                Suite = pr.GetValue(suiteOpt),
                MinSeverity = pr.GetValue(severityOpt),
                TimeoutSeconds = pr.GetValue(timeoutOpt) is int t and > 0 ? t : 30,
                AllowSelfSignedCerts = pr.GetValue(allowSelfSignedOpt),
                RunBuiltins = !pr.GetValue(noBuiltinsOpt),
                Scope = pr.GetValue(scopeOpt) ?? Array.Empty<string>(),
                AuthHeaders = pr.GetValue(authHeaderOpt) ?? Array.Empty<string>(),
                AuthHeadersB = pr.GetValue(authHeaderBOpt) ?? Array.Empty<string>(),
                Active = pr.GetValue(activeOpt),
                ActiveDurationSeconds = pr.GetValue(activeDurationOpt) is int d and > 0 ? d : 15,
                ActiveConcurrency = pr.GetValue(activeConcurrencyOpt) is int c and > 0 ? c : 100,
                ActiveExpectedTopics = pr.GetValue(activeExpectedTopicOpt) ?? Array.Empty<string>(),
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
        scan.Add(BuildMutate());
        scan.Add(BuildReport());
        return scan;
    }

    // `bowire scan mutate --type <kind>` (#175) — exercise the schema-aware
    // mutation engine for one field type; the reproducible building block.
    private static Command BuildMutate()
    {
        var mutate = new Command("mutate",
            "Print the schema-aware mutations (targeted invalid inputs) the engine produces for a field type — type-confusion, boundary, encoding, enum-bypass, structural. Seeded + budgeted for reproducibility.");

        var typeOpt = new Option<string>("--type") { Description = "Field type: integer / number / string / boolean / enum / array / object.", Required = true };
        var enumOpt = new Option<string>("--enum") { Description = "Comma-separated allowed values (for --type enum) — adds a case-variant-bypass mutation." };
        var requiredOpt = new Option<bool>("--required") { Description = "Treat the field as required — adds an omitted/null violation." };
        var formatOpt = new Option<string>("--format") { Description = "Format hint (email / uuid / date / uri / ipv4) — adds a format-violation mutation." };
        var seedOpt = new Option<int>("--seed") { Description = "Seed for reproducible mutation selection under a budget. Default 0." };
        var budgetOpt = new Option<int>("--budget") { Description = "Max mutations to emit for the field (0 = all). Keeps scans bounded." };

        mutate.Add(typeOpt);
        mutate.Add(enumOpt);
        mutate.Add(requiredOpt);
        mutate.Add(formatOpt);
        mutate.Add(seedOpt);
        mutate.Add(budgetOpt);

        mutate.SetAction(async (pr, ct) =>
        {
            var options = new MutateOptions
            {
                Type = pr.GetValue(typeOpt),
                Enum = pr.GetValue(enumOpt),
                Required = pr.GetValue(requiredOpt),
                Format = pr.GetValue(formatOpt),
                Seed = pr.GetValue(seedOpt),
                Budget = pr.GetValue(budgetOpt),
            };
            return await MutateCommand.RunAsync(
                options, ct, pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false);
        });

        return mutate;
    }

    // `bowire scan report --in <sarif>` (#107) — turn a scan's SARIF artifact
    // into a deterministic markdown report, optionally diffed against a baseline.
    private static Command BuildReport()
    {
        var report = new Command("report",
            "Turn a scan SARIF artifact (bowire scan --out) into a markdown report — findings grouped by severity + OWASP, optionally diffed vs a baseline SARIF. Deterministic; the AI executive-summary layer is POST /api/ai/security-report.");

        var inOpt = new Option<string>("--in") { Description = "SARIF file to report on (from `bowire scan --out`).", Required = true };
        var baselineOpt = new Option<string>("--baseline") { Description = "A previous run's SARIF to diff against — the report adds new / fixed / still-open sections." };
        var outOpt = new Option<string>("--out") { Description = "Write the markdown report to this path (default: print to stdout)." };
        var targetOpt = new Option<string>("--target") { Description = "Target name to title the report with (e.g. api.example.com)." };

        report.Add(inOpt);
        report.Add(baselineOpt);
        report.Add(outOpt);
        report.Add(targetOpt);
        report.SetAction(async (pr, ct) =>
            await ReportCommand.RunAsync(
                pr.GetValue(inOpt) ?? "", pr.GetValue(baselineOpt), pr.GetValue(outOpt), pr.GetValue(targetOpt),
                ct, pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false));

        return report;
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
