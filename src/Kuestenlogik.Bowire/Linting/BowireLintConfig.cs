// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Linting;

/// <summary>
/// Per-workspace lint configuration (#189), the shape of <c>.bowire/rules.json</c>:
/// which rules run and at what severity. A rule is on unless a setting turns it
/// <c>enabled: false</c>; a setting may also override the rule's severity. Rules
/// with no entry keep their built-in defaults, so an empty (or absent) config
/// means "run everything as shipped".
/// </summary>
/// <remarks>
/// Example:
/// <code>
/// {
///   "rules": {
///     "BWR-LINT-MISSING-VERSIONING": { "enabled": false },
///     "BWR-LINT-PII-RESPONSE": { "severity": "high" }
///   }
/// }
/// </code>
/// </remarks>
public sealed class BowireLintConfig
{
    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Per-rule settings keyed by rule id (matched case-insensitively).</summary>
    [JsonPropertyName("rules")]
    public Dictionary<string, BowireLintRuleSetting> Rules { get; init; } = [];

    /// <summary>Parse a <c>.bowire/rules.json</c> document. Throws on malformed JSON.</summary>
    public static BowireLintConfig Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<BowireLintConfig>(json, ParseOptions) ?? new BowireLintConfig();
    }

    /// <summary>True unless a setting explicitly turns the rule off.</summary>
    public bool IsEnabled(string ruleId) => Find(ruleId)?.Enabled != false;

    /// <summary>The configured severity override for a rule, or null to keep its default.</summary>
    public BowireLintSeverity? SeverityOverride(string ruleId) => Find(ruleId)?.Severity;

    private BowireLintRuleSetting? Find(string ruleId)
    {
        foreach (var (id, setting) in Rules)
        {
            if (string.Equals(id, ruleId, StringComparison.OrdinalIgnoreCase)) return setting;
        }

        return null;
    }
}

/// <summary>One rule's entry in <see cref="BowireLintConfig"/>.</summary>
public sealed class BowireLintRuleSetting
{
    /// <summary>Set to <c>false</c> to turn the rule off. Null/absent leaves it on.</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    /// <summary>Override the rule's severity (e.g. raise a Low to High). Null keeps the default.</summary>
    [JsonPropertyName("severity")]
    public BowireLintSeverity? Severity { get; init; }
}
