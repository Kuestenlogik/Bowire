// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Security;

/// <summary>
/// Predicate-tree node for the vulnerability-template DSL. Each instance
/// is either a <em>leaf</em> (one of the response-property checks set on
/// this node) or a <em>composite</em> (one of <see cref="AllOf"/> /
/// <see cref="AnyOf"/> / <see cref="Not"/> set on this node).
/// </summary>
/// <remarks>
/// <para>
/// The DSL is intentionally flat: a single class with every operator as
/// an optional property, validated at evaluation time. JSON
/// serialization shape matches the ADR
/// (<c>docs/architecture/security-testing.md</c> — Predicate operators
/// section) byte-for-byte so templates round-trip cleanly between disk,
/// the wire, and the in-memory model.
/// </para>
/// <para>
/// A node may set ONE composite operator and any number of leaf operators
/// — leaf operators on the same node are implicit-AND-combined. Mixing
/// composites at one node (e.g. <c>allOf</c> + <c>anyOf</c> on the same
/// object) is undefined and the evaluator processes them in declaration
/// order via implicit AND.
/// </para>
/// </remarks>
public sealed class AttackPredicate
{
    // ---- Leaf operators on the response status ----

    /// <summary>HTTP status code equals.</summary>
    [JsonPropertyName("status")]
    public int? Status { get; set; }

    /// <summary>HTTP status code is one of these values.</summary>
    [JsonPropertyName("statusIn")]
    public IList<int>? StatusIn { get; init; }

    // ---- Leaf operators on the response body ----

    /// <summary>Response body (as UTF-8 text) contains the literal substring.</summary>
    [JsonPropertyName("bodyContains")]
    public string? BodyContains { get; set; }

    /// <summary>Response body matches the regex (RE2-style; .NET regex by default).</summary>
    [JsonPropertyName("bodyMatches")]
    public string? BodyMatches { get; set; }

    /// <summary>
    /// JSONPath-shaped clause on the parsed response body — at least one
    /// match must exist / equal / regex-match per the operator inside the
    /// clause. See <see cref="AttackJsonPathClause"/>.
    /// </summary>
    [JsonPropertyName("bodyJsonPath")]
    public AttackJsonPathClause? BodyJsonPath { get; set; }

    // ---- Leaf operators on the response headers ----

    /// <summary>Named headers exist with the given value. Header-name match is case-insensitive.</summary>
    [JsonPropertyName("headerEquals")]
    public IDictionary<string, string>? HeaderEquals { get; init; }

    /// <summary>Named headers are present (any value). Case-insensitive.</summary>
    [JsonPropertyName("headerExists")]
    public IList<string>? HeaderExists { get; init; }

    /// <summary>Named headers are NOT present in the response. Case-insensitive. Useful for missing-security-header checks.</summary>
    [JsonPropertyName("headerMissing")]
    public IList<string>? HeaderMissing { get; init; }

    // ---- Leaf operator on out-of-band callbacks (#35 Phase 2f) ----

    /// <summary>
    /// An out-of-band callback was attributed to this probe. The only way to
    /// prove a <em>blind</em> finding: the response looks innocuous, and the
    /// evidence is that the target contacted a host the probe planted in it.
    /// <para>
    /// Requires an interaction server (<c>--oast-server</c>). Without one no
    /// interactions are ever collected, so this clause simply never
    /// matches — it fails closed rather than reporting unproven findings.
    /// </para>
    /// </summary>
    [JsonPropertyName("oastInteraction")]
    public OastInteractionClause? OastInteraction { get; init; }

    // ---- Leaf operator on response timing ----

    /// <summary>
    /// Round-trip latency is at least N milliseconds. Used for blind-SQL /
    /// timing-oracle templates that probe a deliberately-slow injection
    /// (`'; SELECT pg_sleep(5); --`) and assert the response is delayed.
    /// </summary>
    [JsonPropertyName("latencyMsAtLeast")]
    public int? LatencyMsAtLeast { get; set; }

    // ---- Composite operators ----

    /// <summary>All sub-predicates must match.</summary>
    [JsonPropertyName("allOf")]
    public IList<AttackPredicate>? AllOf { get; init; }

    /// <summary>At least one sub-predicate must match.</summary>
    [JsonPropertyName("anyOf")]
    public IList<AttackPredicate>? AnyOf { get; init; }

    /// <summary>The sub-predicate must NOT match.</summary>
    [JsonPropertyName("not")]
    public AttackPredicate? Not { get; set; }
}

/// <summary>
/// Operator-bag for the <see cref="AttackPredicate.BodyJsonPath"/> leaf.
/// <see cref="Path"/> selects values; one of <see cref="Exists"/>,
/// <see cref="EqualsValue"/>, <see cref="Matches"/>, or
/// <see cref="AnyValueMatches"/> specifies what counts as a match.
/// </summary>
/// <remarks>
/// <para>
/// Supported JSONPath subset (mirrors the JS-side <c>bowireResolveJsonPath</c>
/// in the workbench):
/// </para>
/// <list type="bullet">
///   <item><description><c>$</c> — root</description></item>
///   <item><description><c>$.foo</c> — object property</description></item>
///   <item><description><c>$.foo.bar</c> — nested property</description></item>
///   <item><description><c>$.foo[0]</c> — array index</description></item>
///   <item><description><c>$.foo[*]</c> — array wildcard (returns every element)</description></item>
///   <item><description><c>$.foo[*].name</c> — wildcard + further nav</description></item>
/// </list>
/// </remarks>
public sealed class AttackJsonPathClause
{
    /// <summary>JSONPath expression — see remarks for the supported subset.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "$";

    /// <summary>When true, the clause matches if at least one path-result exists. When false, matches if none exist.</summary>
    [JsonPropertyName("exists")]
    public bool? Exists { get; set; }

    /// <summary>At least one path-result equals this value (stringified).</summary>
    [JsonPropertyName("equals")]
    public string? EqualsValue { get; set; }

    /// <summary>The combined path-result (stringified, joined by newline if multiple) matches this regex.</summary>
    [JsonPropertyName("matches")]
    public string? Matches { get; set; }

    /// <summary>At least one individual path-result matches this regex.</summary>
    [JsonPropertyName("anyValueMatches")]
    public string? AnyValueMatches { get; set; }
}

/// <summary>
/// Inner clause of <see cref="AttackPredicate.OastInteraction"/> — which
/// out-of-band callback counts as proof (#35 Phase 2f). An empty clause means
/// "any callback at all", which is already the finding for most blind
/// templates: the target contacting a host it was fed is the vulnerability,
/// regardless of transport.
/// </summary>
public sealed class OastInteractionClause
{
    /// <summary>
    /// Only count callbacks that arrived on this transport (<c>dns</c>,
    /// <c>http</c>, <c>smtp</c>, …), case-insensitively. Maps Nuclei's
    /// <c>part: interactsh_protocol</c> matcher. Null = any transport.
    /// <para>
    /// Worth pinning: a DNS-only callback proves the target *resolved* the
    /// host, an HTTP one proves it actually fetched it — a materially stronger
    /// claim that some templates require.
    /// </para>
    /// </summary>
    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    /// <summary>
    /// Only count callbacks whose raw request contains this substring
    /// (case-insensitive). Maps Nuclei's <c>part: interactsh_request</c>
    /// matcher — e.g. asserting exfiltrated content came back in the callback.
    /// Null = don't inspect the request.
    /// </summary>
    [JsonPropertyName("requestContains")]
    public string? RequestContains { get; set; }
}
