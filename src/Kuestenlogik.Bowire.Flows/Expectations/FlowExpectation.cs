// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Flows.Expectations;

/// <summary>
/// Declarative assertion attached to a Flow step. Each expectation pairs a
/// <see cref="Kind"/> (what to look at), an <see cref="Operator"/> (how to
/// compare), an optional <see cref="Target"/> selector (header name, JSON
/// path, …), and an <see cref="Expected"/> right-hand-side value. Lives as
/// a public type so the v2.2 CI runner (T2: <c>bowire test &lt;flow&gt;</c>)
/// can deserialise the same shape the workbench writes.
/// </summary>
/// <remarks>
/// Wire shape — exactly what the workbench persists and what the CLI reads:
/// <code>
/// { "id":"exp_…", "kind":"body-path", "operator":"equals",
///   "target":"$.user.id", "expected":"42" }
/// </code>
/// The legacy <c>{path, op, value}</c> tuple the in-browser runner used
/// before v2.2 round-trips through this type via
/// <see cref="FromLegacyTuple"/>; T2 can therefore consume a mixed flow file
/// without a migration pass.
/// </remarks>
public sealed class FlowExpectation
{
    /// <summary>Stable id assigned when the workbench creates the row; T2 echoes it back into JUnit so individual rows are addressable.</summary>
    public string? Id { get; set; }

    /// <summary>What part of the request envelope to look at.</summary>
    public FlowExpectationKind Kind { get; set; } = FlowExpectationKind.BodyPath;

    /// <summary>How to compare actual against <see cref="Expected"/>.</summary>
    public FlowExpectationOperator Operator { get; set; } = FlowExpectationOperator.Equals;

    /// <summary>
    /// Selector inside the kind: header name for <see cref="FlowExpectationKind.Header"/>,
    /// JSON path for <see cref="FlowExpectationKind.BodyPath"/>, ignored for
    /// <see cref="FlowExpectationKind.Status"/> / <see cref="FlowExpectationKind.Latency"/> /
    /// <see cref="FlowExpectationKind.BodyText"/>.
    /// </summary>
    public string? Target { get; set; }

    /// <summary>
    /// Right-hand-side value. Stored as a string because the wire shape is
    /// string-typed; the evaluator coerces to numbers / booleans / regex as
    /// the chosen <see cref="Operator"/> demands. Null for
    /// <see cref="FlowExpectationOperator.Exists"/> /
    /// <see cref="FlowExpectationOperator.NotExists"/>.
    /// </summary>
    public string? Expected { get; set; }

    /// <summary>
    /// Bridge from the v2.1 in-browser tuple. <paramref name="path"/>
    /// maps onto Kind+Target heuristically:
    /// <c>status</c> → <see cref="FlowExpectationKind.Status"/>,
    /// <c>durationMs</c> → <see cref="FlowExpectationKind.Latency"/>,
    /// everything else lands on <see cref="FlowExpectationKind.BodyPath"/>.
    /// Used by the workbench's storage migration so existing saved flows
    /// keep validating after the v2.2 upgrade.
    /// </summary>
    public static FlowExpectation FromLegacyTuple(string? path, string? op, string? value)
    {
        var trimmed = (path ?? string.Empty).Trim();
        FlowExpectationKind kind;
        string? target = null;
        if (string.Equals(trimmed, "status", StringComparison.Ordinal))
        {
            kind = FlowExpectationKind.Status;
        }
        else if (string.Equals(trimmed, "durationMs", StringComparison.Ordinal))
        {
            kind = FlowExpectationKind.Latency;
        }
        else if (string.Equals(trimmed, "response", StringComparison.Ordinal) || string.IsNullOrEmpty(trimmed))
        {
            kind = FlowExpectationKind.BodyText;
        }
        else
        {
            kind = FlowExpectationKind.BodyPath;
            target = trimmed;
        }

        return new FlowExpectation
        {
            Kind = kind,
            Operator = MapLegacyOperator(op),
            Target = target,
            Expected = value,
        };
    }

    private static FlowExpectationOperator MapLegacyOperator(string? op) => (op ?? "eq") switch
    {
        "eq" => FlowExpectationOperator.Equals,
        "neq" or "ne" => FlowExpectationOperator.NotEquals,
        "gt" => FlowExpectationOperator.GreaterThan,
        "gte" => FlowExpectationOperator.GreaterThanOrEquals,
        "lt" => FlowExpectationOperator.LessThan,
        "lte" => FlowExpectationOperator.LessThanOrEquals,
        "contains" => FlowExpectationOperator.Contains,
        "exists" => FlowExpectationOperator.Exists,
        "missing" or "notexists" or "not-exists" => FlowExpectationOperator.NotExists,
        "regex" or "matches" => FlowExpectationOperator.Regex,
        _ => FlowExpectationOperator.Equals,
    };
}

/// <summary>What slice of the request envelope an expectation reads.</summary>
public enum FlowExpectationKind
{
    /// <summary>The HTTP status / protocol-equivalent status string ("OK", "200", ...).</summary>
    Status,
    /// <summary>A response header by name (case-insensitive).</summary>
    Header,
    /// <summary>A value resolved from the parsed JSON response body via a dotted / $-anchored path.</summary>
    BodyPath,
    /// <summary>The raw response body as a single string blob (for contains / regex sweeps).</summary>
    BodyText,
    /// <summary>The measured request latency in milliseconds.</summary>
    Latency,
}

/// <summary>How an expectation compares actual to expected.</summary>
public enum FlowExpectationOperator
{
    /// <summary>Loose string-equality (numeric coercion when both sides parse).</summary>
    Equals,
    /// <summary>Inverse of <see cref="Equals"/>.</summary>
    NotEquals,
    /// <summary>Substring match (string) or element-match (JSON array).</summary>
    Contains,
    /// <summary>Numeric &lt;.</summary>
    LessThan,
    /// <summary>Numeric &lt;=.</summary>
    LessThanOrEquals,
    /// <summary>Numeric &gt;.</summary>
    GreaterThan,
    /// <summary>Numeric &gt;=.</summary>
    GreaterThanOrEquals,
    /// <summary>Actual is non-null / non-empty.</summary>
    Exists,
    /// <summary>Actual is null / missing.</summary>
    NotExists,
    /// <summary>Actual matches <c>Expected</c> as a regular expression.</summary>
    Regex,
}
