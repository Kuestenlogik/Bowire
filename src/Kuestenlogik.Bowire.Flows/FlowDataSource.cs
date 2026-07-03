// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Flows;

/// <summary>
/// Data-driven parameterisation for a Flow step — #174. Exactly one of
/// <see cref="Inline"/> / <see cref="Csv"/> / <see cref="Generator"/> is
/// set; the runner expands the step into one execution per row, with the
/// row's columns joining the <c>{{var}}</c> resolver scope (row wins over
/// <c>--env</c>).
/// </summary>
/// <remarks>
/// Wire shape on the step:
/// <code>
/// { "data": { "inline": [ { "userId": "1" }, { "userId": "2" } ] } }
/// { "data": { "csv": "fixtures/users.csv" } }
/// { "data": { "generator": { "kind": "range", "var": "i", "from": 1, "to": 10 } } }
/// </code>
/// </remarks>
public sealed class FlowDataSource
{
    /// <summary>Inline rows — each object is one row, values stringified for substitution.</summary>
    [JsonPropertyName("inline")]
    public IReadOnlyList<JsonObject>? Inline { get; set; }

    /// <summary>CSV file path, resolved relative to the flow file. First row is the header.</summary>
    [JsonPropertyName("csv")]
    public string? Csv { get; set; }

    /// <summary>Synthetic rows from a deterministic generator.</summary>
    [JsonPropertyName("generator")]
    public FlowDataGenerator? Generator { get; set; }

    /// <summary>
    /// Optional column whose value labels the row in reports (defaults to
    /// the zero-based row index).
    /// </summary>
    [JsonPropertyName("labelColumn")]
    public string? LabelColumn { get; set; }
}

/// <summary>Row generator config: <c>range</c> (from..to inclusive) or <c>random</c> (seeded, reproducible).</summary>
public sealed class FlowDataGenerator
{
    /// <summary><c>range</c> or <c>random</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "range";

    /// <summary>Variable name each generated value binds to.</summary>
    [JsonPropertyName("var")]
    public string Var { get; set; } = "value";

    /// <summary>Range start, inclusive (<c>range</c>).</summary>
    [JsonPropertyName("from")]
    public long From { get; set; }

    /// <summary>Range end, inclusive (<c>range</c>).</summary>
    [JsonPropertyName("to")]
    public long To { get; set; }

    /// <summary>Row count (<c>random</c>).</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>PRNG seed (<c>random</c>) — same seed, same rows, so CI reruns are reproducible.</summary>
    [JsonPropertyName("seed")]
    public int Seed { get; set; }

    /// <summary>Minimum generated value, inclusive (<c>random</c>).</summary>
    [JsonPropertyName("min")]
    public long Min { get; set; }

    /// <summary>Maximum generated value, inclusive (<c>random</c>).</summary>
    [JsonPropertyName("max")]
    public long Max { get; set; } = 100;
}
