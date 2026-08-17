// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Linting.Rules;
using Kuestenlogik.Bowire.Models;

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
    ];

    /// <summary>A linter loaded with the built-in <see cref="DefaultRules"/>.</summary>
    public static BowireSchemaLinter CreateDefault() => new(DefaultRules);

    /// <summary>The ids of the rules this linter runs.</summary>
    public IReadOnlyList<string> RuleIds => [.. _rules.Select(r => r.Id)];

    /// <summary>
    /// Lint every service with every rule. Findings come back grouped by
    /// service in input order, then by rule in registration order — a stable
    /// order so a report or a diff of two runs reads the same each time.
    /// </summary>
    public IReadOnlyList<BowireLintFinding> Lint(IReadOnlyList<BowireServiceInfo> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var findings = new List<BowireLintFinding>();
        foreach (var service in services)
        {
            foreach (var rule in _rules)
            {
                findings.AddRange(rule.Inspect(service));
            }
        }

        return findings;
    }
}
