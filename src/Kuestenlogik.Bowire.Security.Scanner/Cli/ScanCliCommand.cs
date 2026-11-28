// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.Cli;
using Kuestenlogik.Bowire.Security.Templates.Nuclei;
using Microsoft.Extensions.Configuration;

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

    public Command Build() => Build(configuration: null);

    /// <inheritdoc />
    /// <remarks>
    /// Reads the scan's configuration-only settings — today the Nuclei
    /// limits under <c>Bowire:Security:Nuclei</c> (#881) — from the CLI's
    /// configuration (appsettings, <c>BOWIRE_</c> env vars).
    /// </remarks>
    public Command Build(IConfiguration? configuration)
    {
        var nucleiOptions = NucleiOptionsFrom(configuration);

        var scan = new Command("scan",
            BowireLocale.T("cli.scan.scan.command"));

        var targetOpt = new Option<string>("--target") { Description = BowireLocale.T("cli.scan.target"), Required = true };
        var templatesOpt = new Option<string>("--templates") { Description = BowireLocale.T("cli.scan.templates") };
        var templateOpt = new Option<string>("--template") { Description = BowireLocale.T("cli.scan.template") };
        var nucleiOpt = new Option<string>("--nuclei") { Description = BowireLocale.T("cli.scan.nuclei") };
        var cveDbOpt = new Option<string>("--cve-db") { Description = BowireLocale.T("cli.scan.cve-db") };
        var oastServerOpt = new Option<string>("--oast-server") { Description = BowireLocale.T("cli.scan.oast-server") };
        var oastWaitOpt = new Option<int>("--oast-wait") { Description = BowireLocale.T("cli.scan.oast-wait") };
        var oastTokenOpt = new Option<string>("--oast-token") { Description = BowireLocale.T("cli.scan.oast-token") };
        var authFlowOpt = new Option<string>("--auth-flow") { Description = BowireLocale.T("cli.scan.auth-flow") };
        var outOpt = new Option<string>("--out") { Description = BowireLocale.T("cli.scan.out") };
        var suiteOpt = new Option<string>("--suite") { Description = BowireLocale.T("cli.scan.suite") };
        var severityOpt = new Option<string>("--severity") { Description = BowireLocale.T("cli.scan.severity") };
        var timeoutOpt = new Option<int>("--timeout") { Description = BowireLocale.T("cli.scan.timeout") };
        var dnsResolverOpt = new Option<string>("--dns-resolver") { Description = BowireLocale.T("cli.scan.dns-resolver") };
        var allowCodeOpt = new Option<bool>("--allow-code-templates") { Description = BowireLocale.T("cli.scan.allow-code-templates") };
        var codeInterpretersOpt = new Option<string[]>("--code-template-interpreters")
        {
            AllowMultipleArgumentsPerToken = true,
            Description = BowireLocale.T("cli.scan.code-template-interpreters"),
        };
        var allowSelfSignedOpt = new Option<bool>("--allow-self-signed-certs") { Description = BowireLocale.T("cli.scan.allow-self-signed-certs") };
        var noBuiltinsOpt = new Option<bool>("--no-builtins") { Description = BowireLocale.T("cli.scan.no-builtins") };
        var scopeOpt = new Option<string[]>("--scope")
        {
            Description = BowireLocale.T("cli.scan.scope"),
            AllowMultipleArgumentsPerToken = true,
        };
        var authHeaderOpt = new Option<string[]>("--auth-header")
        {
            Description = BowireLocale.T("cli.scan.auth-header"),
            AllowMultipleArgumentsPerToken = false,
        };
        var authHeaderBOpt = new Option<string[]>("--auth-header-b")
        {
            Description = BowireLocale.T("cli.scan.auth-header-b"),
            AllowMultipleArgumentsPerToken = false,
        };
        // #1016 — the option the other commands name a schema file with.
        // A scan reads one kind: the compiled descriptor set.
        var schemaOpt = new Option<string[]>("--schema")
        {
            Description = BowireLocale.T("cli.scan.schema"),
            AllowMultipleArgumentsPerToken = true,
        };
        var activeOpt = new Option<bool>("--active") { Description = BowireLocale.T("cli.scan.active") };
        var activeDurationOpt = new Option<int>("--active-duration") { Description = BowireLocale.T("cli.scan.active-duration") };
        var activeConcurrencyOpt = new Option<int>("--active-concurrency") { Description = BowireLocale.T("cli.scan.active-concurrency") };
        var activeExpectedTopicOpt = new Option<string[]>("--active-expected-topic")
        {
            Description = BowireLocale.T("cli.scan.active-expected-topic"),
            AllowMultipleArgumentsPerToken = true,
        };

        scan.Add(targetOpt);
        scan.Add(templatesOpt);
        scan.Add(templateOpt);
        scan.Add(nucleiOpt);
        scan.Add(cveDbOpt);
        scan.Add(oastServerOpt);
        scan.Add(oastWaitOpt);
        scan.Add(oastTokenOpt);
        scan.Add(authFlowOpt);
        scan.Add(outOpt);
        scan.Add(suiteOpt);
        scan.Add(severityOpt);
        scan.Add(timeoutOpt);
        scan.Add(dnsResolverOpt);
        scan.Add(allowCodeOpt);
        scan.Add(codeInterpretersOpt);
        scan.Add(allowSelfSignedOpt);
        scan.Add(noBuiltinsOpt);
        scan.Add(scopeOpt);
        scan.Add(authHeaderOpt);
        scan.Add(authHeaderBOpt);
        scan.Add(schemaOpt);
        scan.Add(activeOpt);
        scan.Add(activeDurationOpt);
        scan.Add(activeConcurrencyOpt);
        scan.Add(activeExpectedTopicOpt);

        scan.SetAction(async (pr, ct) =>
        {
            // Read before anything is probed: a path that is not a
            // descriptor set, or a .proto or OpenAPI file this command would
            // do nothing with, is a usage error (exit 2) rather than a scan
            // that quietly ran without it.
            var (schemas, schemaCode) = await CliSchemaInputs.ReadAsync(
                pr.GetValue(schemaOpt), pr.InvocationConfiguration.Error,
                BowireProtocolRegistry.Discover(), CliSchemaInputs.Taken.Only("protoset")).ConfigureAwait(false);
            if (schemaCode != 0) return schemaCode;

            var options = new ScanOptions
            {
                Target = pr.GetValue(targetOpt) ?? "",
                Templates = pr.GetValue(templatesOpt),
                Template = pr.GetValue(templateOpt),
                Nuclei = pr.GetValue(nucleiOpt),
                NucleiOptions = nucleiOptions,
                CveDbPath = pr.GetValue(cveDbOpt),
                OastServer = pr.GetValue(oastServerOpt),
                OastToken = pr.GetValue(oastTokenOpt),
                OastWaitSeconds = pr.GetValue(oastWaitOpt) is int w and >= 0 ? w : 5,
                AuthFlowPath = pr.GetValue(authFlowOpt),
                OutSarif = pr.GetValue(outOpt),
                Suite = pr.GetValue(suiteOpt),
                MinSeverity = pr.GetValue(severityOpt),
                TimeoutSeconds = pr.GetValue(timeoutOpt) is int t and > 0 ? t : 30,
                DnsResolver = pr.GetValue(dnsResolverOpt),
                AllowCodeTemplates = pr.GetValue(allowCodeOpt),
                CodeTemplateInterpreters = pr.GetValue(codeInterpretersOpt) ?? Array.Empty<string>(),
                AllowSelfSignedCerts = pr.GetValue(allowSelfSignedOpt),
                RunBuiltins = !pr.GetValue(noBuiltinsOpt),
                Scope = pr.GetValue(scopeOpt) ?? Array.Empty<string>(),
                AuthHeaders = pr.GetValue(authHeaderOpt) ?? Array.Empty<string>(),
                AuthHeadersB = pr.GetValue(authHeaderBOpt) ?? Array.Empty<string>(),
                GrpcDescriptorSetPaths = schemas is null
                    ? []
                    : [.. (pr.GetValue(schemaOpt) ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath)],
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

    /// <summary>
    /// Bind <see cref="NucleiOptions"/> from
    /// <see cref="NucleiOptions.SectionName"/>; the defaults without a
    /// configuration or the section.
    /// </summary>
    internal static NucleiOptions NucleiOptionsFrom(IConfiguration? configuration)
    {
        var options = new NucleiOptions();
        configuration?.GetSection(NucleiOptions.SectionName).Bind(options);
        return options;
    }

    // `bowire scan mutate --type <kind>` (#175) — exercise the schema-aware
    // mutation engine for one field type; the reproducible building block.
    private static Command BuildMutate()
    {
        var mutate = new Command("mutate",
            BowireLocale.T("cli.scan.mutate.command"));

        var typeOpt = new Option<string>("--type") { Description = BowireLocale.T("cli.scan.type"), Required = true };
        var enumOpt = new Option<string>("--enum") { Description = BowireLocale.T("cli.scan.enum") };
        var requiredOpt = new Option<bool>("--required") { Description = BowireLocale.T("cli.scan.required") };
        var formatOpt = new Option<string>("--format") { Description = BowireLocale.T("cli.scan.format") };
        var seedOpt = new Option<int>("--seed") { Description = BowireLocale.T("cli.scan.seed") };
        var budgetOpt = new Option<int>("--budget") { Description = BowireLocale.T("cli.scan.budget") };

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
            BowireLocale.T("cli.scan.report.command"));

        var inOpt = new Option<string>("--in") { Description = BowireLocale.T("cli.scan.in"), Required = true };
        var baselineOpt = new Option<string>("--baseline") { Description = BowireLocale.T("cli.scan.baseline") };
        var outOpt = new Option<string>("--out") { Description = BowireLocale.T("cli.scan.out-2") };
        var targetOpt = new Option<string>("--target") { Description = BowireLocale.T("cli.scan.target-2") };

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
            BowireLocale.T("cli.scan.spider.command"));

        var urlOpt = new Option<string>("--url") { Description = BowireLocale.T("cli.scan.url"), Required = true };
        var scopeOpt = new Option<string[]>("--scope") { Description = BowireLocale.T("cli.scan.scope-2"), AllowMultipleArgumentsPerToken = true };
        var authHeaderOpt = new Option<string[]>("--auth-header") { Description = BowireLocale.T("cli.scan.auth-header-2"), AllowMultipleArgumentsPerToken = false };
        var timeoutOpt = new Option<int>("--timeout") { Description = BowireLocale.T("cli.scan.timeout-2") };
        var noRobotsOpt = new Option<bool>("--no-robots") { Description = BowireLocale.T("cli.scan.no-robots") };
        var maxOpt = new Option<int>("--max") { Description = BowireLocale.T("cli.scan.max") };
        var allowSelfSignedOpt = new Option<bool>("--allow-self-signed-certs") { Description = BowireLocale.T("cli.scan.allow-self-signed-certs-2") };
        var outOpt = new Option<string>("--out") { Description = BowireLocale.T("cli.scan.out-3") };

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
