// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Operator-authored refinement layer for a schema-generated mock (#558).
/// A schema mock synthesises plausible-but-generic responses from the
/// declared types; this configuration is the persisted sidecar that lets an
/// operator refine them without re-discovering: per-field response
/// overrides (applied here, in this slice), per-method conditional-response
/// rules, and an auth-requirement block.
/// </summary>
/// <remarks>
/// <para>
/// This is the shared, cross-cutting contract every mock-refinement slice
/// serializes into. Only <see cref="FieldOverrides"/> is <em>evaluated</em>
/// in the foundation slice (via <see cref="MockConfigApplier"/>);
/// <see cref="ConditionalRules"/> and <see cref="Auth"/> are model-only
/// here and are consumed by the sibling editor / auth slices (#561 / #562).
/// </para>
/// <para>
/// The store (<c>MockConfigStore</c>) persists this as a workspace artifact
/// at <c>workspaces/&lt;wsId&gt;/mocks/&lt;mockId&gt;.json</c>; the
/// <c>bowire mock --mock-config &lt;file&gt;</c> flag loads one at startup.
/// </para>
/// </remarks>
public sealed class MockConfiguration
{
    /// <summary>The envelope version this build writes. Bump on a breaking shape change.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// On-disk envelope version. Read version-tolerantly: an absent value
    /// defaults to <see cref="CurrentFormatVersion"/>; a newer value loads
    /// best-effort because unknown fields are ignored (see <see cref="Parse"/>).
    /// </summary>
    [JsonPropertyName("configFormatVersion")]
    public int ConfigFormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>Which schema source this configuration refines. Diagnostic; not required to apply.</summary>
    [JsonPropertyName("source")]
    public MockConfigSource? Source { get; set; }

    /// <summary>
    /// Per-field response overrides — the only arm evaluated in this slice.
    /// Never null after a successful <see cref="Parse"/> (an explicit
    /// <c>"fieldOverrides": null</c> token is rejected, not silently kept).
    /// </summary>
    [JsonPropertyName("fieldOverrides")]
    public IList<MockFieldOverride> FieldOverrides { get; init; } = new List<MockFieldOverride>();

    /// <summary>Per-method conditional-response rules. Model-only here; evaluated by #561.</summary>
    [JsonPropertyName("conditionalRules")]
    public IList<MockConditionalRule> ConditionalRules { get; init; } = new List<MockConditionalRule>();

    /// <summary>Auth-requirement block. Model-only here; enforced by #562.</summary>
    [JsonPropertyName("auth")]
    public MockAuthRequirement? Auth { get; set; }

    private static readonly JsonSerializerOptions SerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Parse a mock-configuration document. Version-tolerant by design:
    /// <list type="bullet">
    ///   <item>an empty / whitespace document yields a default (empty) configuration;</item>
    ///   <item>an absent <c>configFormatVersion</c> stays at <see cref="CurrentFormatVersion"/>
    ///   (backward tolerance); a non-positive one is normalised to it;</item>
    ///   <item>a <em>newer</em> version loads best-effort — System.Text.Json ignores
    ///   unknown properties, so a forward-compatible reader keeps working (forward tolerance).</item>
    /// </list>
    /// Throws <see cref="JsonException"/> when the payload is not valid JSON of
    /// the expected object shape, or when a collection arm is explicitly
    /// <c>null</c> (<c>"fieldOverrides": null</c>) — use <c>[]</c> or omit it;
    /// this keeps every consumer's collection non-null.
    /// </summary>
    public static MockConfiguration Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new MockConfiguration();
        var config = JsonSerializer.Deserialize<MockConfiguration>(json, SerOptions)
            ?? new MockConfiguration();
        if (config.ConfigFormatVersion <= 0) config.ConfigFormatVersion = CurrentFormatVersion;
        // System.Text.Json's setter runs for an explicit null token, defeating
        // the constructor initializer and leaving the collection null. Reject
        // that rather than return a landmined config — the store's Save
        // contract is to refuse anything not a clean configuration.
        if (config.FieldOverrides is null || config.ConditionalRules is null)
        {
            throw new JsonException(
                "A configuration collection (fieldOverrides / conditionalRules) was explicitly null; use [] or omit it.");
        }
        return config;
    }

    /// <summary>Serialize to the canonical on-disk / wire JSON shape.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerOptions);
}

/// <summary>
/// Which schema source a <see cref="MockConfiguration"/> refines — carried
/// so the workbench can show "config for &lt;kind&gt; mock at &lt;path&gt;"
/// and detect a mismatched attach. Diagnostic only; the applier does not
/// read it.
/// </summary>
/// <param name="Kind">Schema kind — <c>"openapi"</c> / <c>"protobuf"</c> / <c>"graphql"</c>.</param>
/// <param name="Path">Optional schema-source path or URL the config was authored against.</param>
public sealed record MockConfigSource(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string? Path = null);

/// <summary>
/// A per-field response override: for the response of <see cref="Service"/> /
/// <see cref="Method"/>, set the value at <see cref="JsonPath"/> to
/// <see cref="Value"/>. An absent / empty / <c>"*"</c> service or method is a
/// wildcard (matches every step). The path uses the same dotted / <c>$</c>-rooted
/// / <c>[index]</c> syntax as the mock body matchers — <c>"$.status"</c>,
/// <c>"items[0].sku"</c>, <c>"user.id"</c>.
/// </summary>
public sealed class MockFieldOverride
{
    /// <summary>Service to scope to (OpenAPI tag / gRPC service / …). Null/empty/<c>*</c> = any.</summary>
    [JsonPropertyName("service")]
    public string? Service { get; set; }

    /// <summary>Method to scope to (operationId / gRPC method / …). Null/empty/<c>*</c> = any.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    /// <summary>Path into the response body where the value is set.</summary>
    [JsonPropertyName("jsonPath")]
    public string JsonPath { get; set; } = "";

    /// <summary>
    /// The override value, as arbitrary JSON. A null / absent value is a
    /// no-op — the override is skipped. (System.Text.Json maps a JSON
    /// <c>null</c> to a CLR <c>null</c> here, so an absent field and an
    /// explicit <c>"value": null</c> are indistinguishable; neither
    /// overrides. Setting a field to JSON null / removing it is not
    /// expressed in the foundation slice.)
    /// </summary>
    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }
}

/// <summary>
/// Per-method conditional-response rule (#558 model; evaluated by #561).
/// When a request to <see cref="Service"/> / <see cref="Method"/> satisfies
/// <see cref="When"/>, the mock serves <see cref="Response"/> in place of the
/// default. Model-only in the foundation slice — the serve-time evaluator
/// ships with the conditional-rules editor.
/// </summary>
public sealed class MockConditionalRule
{
    /// <summary>Service to scope to. Null/empty/<c>*</c> = any.</summary>
    [JsonPropertyName("service")]
    public string? Service { get; set; }

    /// <summary>Method to scope to. Null/empty/<c>*</c> = any.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    /// <summary>Request predicate that arms the rule.</summary>
    [JsonPropertyName("when")]
    public MockRulePredicate? When { get; set; }

    /// <summary>Response body (as arbitrary JSON) to serve when the rule fires.</summary>
    [JsonPropertyName("response")]
    public JsonElement? Response { get; set; }
}

/// <summary>
/// A request-body predicate for a <see cref="MockConditionalRule"/> (#558
/// model). Mirrors the <c>BowireBodyMatcher</c> text-op subset so the
/// conditional-rules editor and the mock matcher agree on one predicate
/// shape.
/// </summary>
public sealed class MockRulePredicate
{
    /// <summary>Path into the request body to test. Null = test the raw body string.</summary>
    [JsonPropertyName("jsonPath")]
    public string? JsonPath { get; set; }

    /// <summary>Require a value exactly equal to this.</summary>
    [JsonPropertyName("equals")]
    public string? EqualTo { get; set; }

    /// <summary>Require a value containing this substring.</summary>
    [JsonPropertyName("contains")]
    public string? Contains { get; set; }

    /// <summary>Require a value matching this regex.</summary>
    [JsonPropertyName("matches")]
    public string? Matches { get; set; }
}

/// <summary>
/// Auth-requirement block (#558 model; enforced by #562). Declares that the
/// mock should require authentication and, optionally, which captured
/// #sec-04 auth recording supplies the accepted credential. Model-only in
/// the foundation slice — the 401 gate ships with the auth slice.
/// </summary>
public sealed class MockAuthRequirement
{
    /// <summary>When true, #562's gate rejects unauthenticated requests with 401.</summary>
    [JsonPropertyName("required")]
    public bool Required { get; set; }

    /// <summary>Expected credential scheme — <c>"bearer"</c> / <c>"apikey"</c> / <c>"basic"</c>.</summary>
    [JsonPropertyName("scheme")]
    public string? Scheme { get; set; }

    /// <summary>Recording id of a captured #sec-04 auth flow that sources the accepted credential.</summary>
    [JsonPropertyName("authRecordingId")]
    public string? AuthRecordingId { get; set; }

    /// <summary>Header the credential is carried in (default <c>Authorization</c> for bearer/basic).</summary>
    [JsonPropertyName("header")]
    public string? Header { get; set; }
}
