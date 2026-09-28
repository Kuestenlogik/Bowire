// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Schemas;

namespace Kuestenlogik.Bowire.Linting;

/// <summary>
/// Breaking changes against a baseline, as lint findings (#583).
/// </summary>
/// <remarks>
/// <para>
/// <c>bowire diff --fail-on breaking</c> already answers "would a consumer
/// break?". This does not answer it a second time: it takes the same
/// <see cref="BowireSchemaDelta"/> and the same line <see cref="BowireSchemaDelta.HasBreakingChanges"/>
/// draws — a removed service, a removed method, a signature change — and
/// reports each one the way every other rule reports, so one lint run, one
/// report and one gate cover design smells and compatibility together.
/// Additions, deprecations and prose edits are not breaking and produce
/// nothing.
/// </para>
/// <para>
/// It is not an ordinary <see cref="IBowireLintRule"/>, because a rule sees
/// one service and this needs two surfaces. It still has a rule id, so
/// <c>.bowire/rules.json</c> can switch it off or re-grade it like any other,
/// and the reports that list rules (JUnit, SARIF) can name it.
/// </para>
/// </remarks>
public static class BowireBreakingChanges
{
    public const string RuleId = "BWR-LINT-BREAKING-CHANGE";

    /// <summary>The rule's identity for reports that list rules. It finds nothing on its own.</summary>
    public static IBowireLintRule Rule { get; } = Descriptor.Create();

    /// <summary>Diff <paramref name="baseline"/> → <paramref name="current"/> and report what breaks.</summary>
    public static IReadOnlyList<BowireLintFinding> Find(
        IReadOnlyList<BowireServiceInfo> baseline, IReadOnlyList<BowireServiceInfo> current,
        BowireLintConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        return ToFindings(BowireSchemaDiff.Compute(baseline, current), config);
    }

    /// <summary>The breaking part of a delta, as findings.</summary>
    public static IReadOnlyList<BowireLintFinding> ToFindings(BowireSchemaDelta delta, BowireLintConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (config is not null && !config.IsEnabled(RuleId)) return [];
        var severity = config?.SeverityOverride(RuleId) ?? Rule.Severity;

        var findings = new List<BowireLintFinding>();
        foreach (var service in delta.RemovedServices)
        {
            findings.Add(new(RuleId, severity, service, null, null,
                $"Service '{service}' was removed since the baseline. Every consumer of it breaks."));
        }
        foreach (var method in delta.RemovedMethods)
        {
            findings.Add(new(RuleId, severity, method.Service, method.Method, null,
                $"Method '{method.Method}' was removed since the baseline."));
        }
        foreach (var change in delta.ChangedMethods.Where(c => string.Equals(c.Kind, "signature", StringComparison.Ordinal)))
        {
            findings.Add(new(RuleId, severity, change.Service, change.Method, null,
                $"The signature of '{change.Method}' changed since the baseline: {change.Detail}."));
        }
        return findings;
    }

    private sealed class Descriptor : IBowireLintRule
    {
        // Private on purpose: BowireSchemaLinter.DiscoverRules instantiates every
        // rule type with a public parameterless constructor, and this one must
        // not run as an ordinary rule that — having no baseline — never fires.
        private Descriptor() { }

        public string Id => RuleId;
        public string Title => "Breaking change against the baseline";
        public BowireLintSeverity Severity => BowireLintSeverity.High;
        public IEnumerable<BowireLintFinding> Inspect(BowireServiceInfo service) => [];

        internal static Descriptor Create() => new();
    }
}
