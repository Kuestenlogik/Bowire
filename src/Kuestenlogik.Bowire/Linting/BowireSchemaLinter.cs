// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Linting.Rules;
using Kuestenlogik.Bowire.Models;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Linting;

/// <summary>
/// Runs a set of <see cref="IBowireLintRule"/> over a discovered API surface
/// and collects their <see cref="BowireLintFinding"/>. The typed rule engine
/// behind design-time API validation (#189): the same discovery snapshot the
/// workbench, `bowire diff`, and the PR bot use is checked here for design
/// smells before a single request is sent.
/// </summary>
public sealed class BowireSchemaLinter
{
    private readonly IReadOnlyList<IBowireLintRule> _rules;

    /// <summary>Create a linter over an explicit rule set.</summary>
    public BowireSchemaLinter(IEnumerable<IBowireLintRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = [.. rules];
    }

    /// <summary>The built-in rules, in declaration order.</summary>
    public static IReadOnlyList<IBowireLintRule> DefaultRules { get; } =
    [
        new SensitiveResponseFieldRule(),
        new MissingPaginationRule(),
        new MissingVersioningRule(),
        new PiiResponseFieldRule(),
        new StringTimestampFieldRule(),
    ];

    /// <summary>A linter loaded with the built-in <see cref="DefaultRules"/>.</summary>
    public static BowireSchemaLinter CreateDefault() => new(DefaultRules);

    /// <summary>
    /// Build the rule set from the built-in <see cref="DefaultRules"/> plus every
    /// <see cref="IBowireLintRule"/> with a public parameterless constructor found
    /// in a loaded <c>Kuestenlogik.Bowire*</c> assembly — the plugin SPI (#189):
    /// a sibling package or host drops in a rule type and it shows up in
    /// <c>bowire lint</c>, the workbench Lint rail, and the <c>bowire.lint</c> MCP
    /// tool alike. Duplicate rule ids are dropped (first wins), so the built-ins
    /// are never double-counted when Core is scanned. Defensive — a single bad
    /// assembly or rule type is skipped, not fatal.
    /// </summary>
    public static IReadOnlyList<IBowireLintRule> DiscoverRules(ILogger? logger = null)
    {
        var rules = new List<IBowireLintRule>(DefaultRules);
        var seen = new HashSet<string>(rules.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.Contains("Bowire", StringComparison.Ordinal) == true))
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                logger?.LogWarning(ex, "Skipped assembly during lint-rule scan: {Assembly}", assembly.FullName);
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(IBowireLintRule).IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) is null) continue;

                try
                {
                    if (Activator.CreateInstance(type) is IBowireLintRule rule && seen.Add(rule.Id))
                        rules.Add(rule);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    logger?.LogWarning(ex, "Failed to instantiate lint rule {Type}", type.FullName);
                }
            }
        }

        return rules;
    }

    /// <summary>
    /// A linter loaded with the built-ins plus any plugin rules discovered via
    /// <see cref="DiscoverRules"/>. The CLI / endpoint / MCP surfaces use this so
    /// a custom rule lights up on every surface at once.
    /// </summary>
    public static BowireSchemaLinter CreateWithDiscoveredRules(ILogger? logger = null)
        => new(DiscoverRules(logger));

    /// <summary>The ids of the rules this linter runs.</summary>
    public IReadOnlyList<string> RuleIds => [.. _rules.Select(r => r.Id)];

    /// <summary>
    /// Lint every service with every rule. Findings come back grouped by
    /// service in input order, then by rule in registration order — a stable
    /// order so a report or a diff of two runs reads the same each time.
    /// <para>
    /// An optional <paramref name="config"/> (from <c>.bowire/rules.json</c>)
    /// skips rules turned <c>enabled: false</c> and remaps a finding's severity
    /// when the rule carries a severity override. A null config runs every rule
    /// at its built-in severity.
    /// </para>
    /// </summary>
    public IReadOnlyList<BowireLintFinding> Lint(
        IReadOnlyList<BowireServiceInfo> services, BowireLintConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var findings = new List<BowireLintFinding>();
        foreach (var service in services)
        {
            foreach (var rule in _rules)
            {
                if (config is not null && !config.IsEnabled(rule.Id)) continue;

                var overrideSeverity = config?.SeverityOverride(rule.Id);
                foreach (var finding in rule.Inspect(service))
                {
                    findings.Add(overrideSeverity is { } severity && severity != finding.Severity
                        ? finding with { Severity = severity }
                        : finding);
                }
            }
        }

        return findings;
    }
}
