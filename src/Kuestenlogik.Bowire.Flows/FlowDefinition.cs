// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Flows.Expectations;

namespace Kuestenlogik.Bowire.Flows;

/// <summary>
/// Server-side projection of the workbench's Flow document — the v2.1
/// in-browser editor persists the canonical shape into
/// <c>localStorage[bowire_flows]</c>; this record mirrors only the fields
/// the v2.2 CLI runner (T2) and the C# expectation evaluator need.
/// Streaming / Foreach / Variable / Condition / Loop nodes are
/// represented but their bodies stay opaque (raw <see cref="System.Text.Json.Nodes.JsonNode"/>)
/// because T2 only EVALUATES — replaying them is the CLI runner's job.
/// </summary>
public sealed class FlowDefinition
{
    /// <summary>Stable flow id ("flow_…").</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable flow name shown in the workbench sidebar.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Top-level step list in declaration order.</summary>
    [JsonPropertyName("nodes")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "System.Text.Json populates the collection via the setter at deserialisation time.")]
    public List<FlowStep> Nodes { get; set; } = new();
}

/// <summary>
/// One step inside a Flow. Each step type uses different fields; common
/// ones are first, type-specific ones tail off into <see cref="Body"/> /
/// <see cref="Service"/> / etc. Lives separately from <c>CapturedFlow</c>
/// (Proxy capture record) — the names collide intentionally, the domains
/// don't.
/// </summary>
public sealed class FlowStep
{
    /// <summary>Stable step id ("node_…") assigned when the workbench created it.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>request / condition / delay / variable / loop.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "request";

    /// <summary>Protocol id ("rest" / "grpc" / …) — request steps only.</summary>
    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    /// <summary>Service name (request steps).</summary>
    [JsonPropertyName("service")]
    public string? Service { get; set; }

    /// <summary>Method name (request steps).</summary>
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    /// <summary>Target server URL override (request steps).</summary>
    [JsonPropertyName("serverUrl")]
    public string? ServerUrl { get; set; }

    /// <summary>Request body (request steps); usually JSON text.</summary>
    [JsonPropertyName("body")]
    public string? Body { get; set; }

    /// <summary>
    /// Snapshot baseline config (#171). Null → no snapshotting for this
    /// step; presence opts the step into capture-once / diff-on-change.
    /// </summary>
    [JsonPropertyName("snapshot")]
    public FlowSnapshotConfig? Snapshot { get; set; }

    /// <summary>
    /// Data-driven parameterisation (#174). Null → the step runs once;
    /// otherwise once per row, with the row's columns joining the
    /// variable-resolver scope.
    /// </summary>
    [JsonPropertyName("data")]
    public FlowDataSource? Data { get; set; }

    /// <summary>
    /// Expectations to evaluate after the request returns. v2.2 schema.
    /// Optional + defaults to empty so v2.1 flows load unchanged.
    /// </summary>
    [JsonPropertyName("expectations")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "System.Text.Json populates the collection via the setter at deserialisation time.")]
    public List<FlowExpectation> Expectations { get; set; } = new();

    /// <summary>
    /// Legacy v2.1 assertion tuples ({path, op, value}). Round-tripped
    /// through <see cref="FlowExpectation.FromLegacyTuple"/> when the
    /// runner builds the live expectation list — covered by
    /// <see cref="EffectiveExpectations"/>.
    /// </summary>
    [JsonPropertyName("assertions")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "System.Text.Json populates the collection via the setter at deserialisation time.")]
    public List<LegacyAssertionTuple>? Assertions { get; set; }

    /// <summary>
    /// Resolved expectation list: v2.2 <see cref="Expectations"/> verbatim
    /// plus any v2.1 <see cref="Assertions"/> projected through the legacy
    /// adapter. The CLI runner consumes this, not the raw lists.
    /// </summary>
    public IReadOnlyList<FlowExpectation> EffectiveExpectations()
    {
        if ((Assertions is null || Assertions.Count == 0) && Expectations.Count == 0)
        {
            return Array.Empty<FlowExpectation>();
        }
        var merged = new List<FlowExpectation>(Expectations);
        if (Assertions is not null)
        {
            foreach (var legacy in Assertions)
            {
                merged.Add(FlowExpectation.FromLegacyTuple(legacy.Path, legacy.Op, legacy.Value));
            }
        }
        return merged;
    }
}

/// <summary>
/// Legacy v2.1 assertion tuple — the in-browser editor wrote
/// <c>{path, op, value}</c> before the v2.2 schema landed. Kept on the
/// step so a saved flow round-trips cleanly through the new pipeline
/// without forcing a one-shot migration.
/// </summary>
public sealed class LegacyAssertionTuple
{
    /// <summary>Path expression — "status", "$.x.y", "durationMs", …</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>v2.1 operator string ("eq", "neq", "contains", …).</summary>
    [JsonPropertyName("op")]
    public string? Op { get; set; }

    /// <summary>Right-hand-side, stringly typed as in the legacy schema.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}
