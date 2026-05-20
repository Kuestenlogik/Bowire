// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Templates.Nuclei;

/// <summary>
/// Plain-old representation of a Nuclei YAML template, populated by
/// <see cref="NucleiTemplateReader"/>. Kept deliberately denormalised
/// (string-valued scalars, list-of-string sequences) so the
/// <see cref="NucleiTemplateConverter"/> can map onto Bowire's typed
/// recording / predicate shape without first reconstructing Nuclei's
/// own typed object model.
///
/// Field surface covers what Phase 2a needs: id, the <c>info</c> block,
/// the first <c>http</c> entry, its first matcher group. Multi-request
/// chains, OAST callbacks, payload matrices, and non-HTTP transports
/// (<c>dns</c>, <c>network</c>, <c>tcp</c>, …) arrive in later phases.
/// </summary>
public sealed class NucleiTemplate
{
    public string Id { get; set; } = "";
    public NucleiInfo Info { get; set; } = new();
    public List<NucleiHttpRequest> Http { get; init; } = [];
}

/// <summary>The <c>info:</c> block on a Nuclei template — author,
/// severity, references, tags, the human-readable name + description.</summary>
public sealed class NucleiInfo
{
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Reference { get; init; } = [];
    public List<string> Tags { get; init; } = [];
}

/// <summary>One entry in the <c>http:</c> array. Phase 2a captures the
/// flat single-request shape; multi-step / payload-matrix templates
/// surface in <see cref="Path"/> as the first listed path.</summary>
public sealed class NucleiHttpRequest
{
    /// <summary>HTTP verb (<c>GET</c> / <c>POST</c> / …). Default <c>GET</c>
    /// when the template omits it.</summary>
    public string Method { get; set; } = "GET";

    /// <summary>URL paths to probe. Nuclei uses <c>{{BaseURL}}</c>
    /// substitutions inside these — the converter resolves them when
    /// a target is bound.</summary>
    public List<string> Path { get; init; } = [];

    /// <summary>Optional inline body for POST/PUT/PATCH probes. Empty
    /// for verbs that don't carry a body.</summary>
    public string Body { get; set; } = "";

    /// <summary>How matchers compose: <c>and</c> (all-of) or <c>or</c>
    /// (any-of). Default Nuclei behaviour is <c>or</c>.</summary>
    public string MatchersCondition { get; set; } = "or";

    /// <summary>One matcher block describes how to recognise that the
    /// vulnerability fired in the response. Multiple matchers compose
    /// via <see cref="MatchersCondition"/>.</summary>
    public List<NucleiMatcher> Matchers { get; init; } = [];

    /// <summary>
    /// Per-variable payload value lists. Nuclei syntax:
    /// <c>payloads: { file: [robots.txt, .env, .git/config] }</c>.
    /// At probe time, every <c>{{file}}</c> placeholder gets the next
    /// value; when multiple variables exist, the converter expands
    /// the cross-product. Phase 2d covers single-variable + multi-
    /// variable cross-product; sniper / pitchfork / cluster-bomb
    /// attack-types arrive when corpus data demands them.
    /// </summary>
    public Dictionary<string, List<string>> Payloads { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A single matcher rule — one of Nuclei's ~14 matcher
/// types (status / word / regex / size / dsl / binary / …). Phase 2a
/// supports the three most common: status, word, regex.</summary>
public sealed class NucleiMatcher
{
    /// <summary>Matcher kind: <c>status</c>, <c>word</c>, <c>regex</c>, …</summary>
    public string Type { get; set; } = "";

    /// <summary>For <c>status</c> matchers: list of acceptable
    /// HTTP status codes. Empty for non-status types.</summary>
    public List<int> Status { get; init; } = [];

    /// <summary>For <c>word</c> matchers: list of strings the response
    /// part must contain (or any-of, depending on <see cref="Condition"/>).</summary>
    public List<string> Words { get; init; } = [];

    /// <summary>For <c>regex</c> matchers: list of regex patterns.</summary>
    public List<string> Regex { get; init; } = [];

    /// <summary>Within a single matcher, how its values compose:
    /// <c>and</c> = all must match, <c>or</c> = any matches. Default
    /// Nuclei convention is <c>or</c>.</summary>
    public string Condition { get; set; } = "or";

    /// <summary>Which part of the response the matcher inspects:
    /// <c>body</c> (default), <c>header</c>, <c>all</c>.</summary>
    public string Part { get; set; } = "body";

    /// <summary>True flips the matcher's polarity — the predicate
    /// fires when the values DON'T match. Nuclei: <c>negative: true</c>.</summary>
    public bool Negative { get; set; }
}
