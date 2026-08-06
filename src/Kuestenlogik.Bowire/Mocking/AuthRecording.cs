// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// A captured authentication credential, addressable by id (#563). A schema
/// mock's <see cref="MockAuthRequirement.AuthRecordingId"/> references one of
/// these; the mock's config-apply path resolves it into the #562 gate's
/// accepted credential, so an operator picks a recording instead of pasting a
/// token.
/// </summary>
/// <remarks>
/// This slice stores a STATIC captured credential — the low-friction fit for
/// #562's exact-match gate. Re-running an <c>AuthFlowDefinition</c> on resolve
/// (an outbound call, opt-in) and the interactive capture UI are the remaining
/// #sec-04 / #190 follow-ups. Persisted per workspace at
/// <c>workspaces/&lt;wsId&gt;/auth-recordings/&lt;id&gt;.json</c> by
/// <c>AuthRecordingStore</c> as plaintext JSON — the same at-rest posture as
/// the mock-config sidecar's own <see cref="MockAuthRequirement.Credential"/>.
/// </remarks>
public sealed class AuthRecording
{
    /// <summary>Stable id the mock config references via <c>authRecordingId</c>; also the on-disk filename.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Human label shown in the picker. Falls back to the id when absent.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Credential scheme — <c>bearer</c> / <c>apikey</c> / <c>basic</c> (default bearer). Populates the gate's scheme.</summary>
    [JsonPropertyName("scheme")]
    public string? Scheme { get; set; }

    /// <summary>Header the credential is presented in (default <c>Authorization</c>). Populates the gate's header.</summary>
    [JsonPropertyName("header")]
    public string? Header { get; set; }

    /// <summary>The captured credential value the gate accepts (bearer/basic token or api-key).</summary>
    [JsonPropertyName("credential")]
    public string Credential { get; set; } = string.Empty;

    /// <summary>Unix-ms capture time, for a staleness hint in the UI. 0 when unknown.</summary>
    [JsonPropertyName("capturedAt")]
    public long CapturedAt { get; set; }

    private static readonly JsonSerializerOptions SerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Parse a recording document. An empty / whitespace document yields a
    /// default (empty) recording; throws <see cref="JsonException"/> when the
    /// payload is not a JSON object of the expected shape.
    /// </summary>
    public static AuthRecording Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AuthRecording();
        return JsonSerializer.Deserialize<AuthRecording>(json, SerOptions) ?? new AuthRecording();
    }

    /// <summary>Serialize to the canonical on-disk / wire JSON shape.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerOptions);
}

/// <summary>Listing projection of an <see cref="AuthRecording"/> for the picker — no credential value.</summary>
/// <param name="Id">The recording id (referenced by <c>authRecordingId</c>).</param>
/// <param name="Name">Human label, or the id when none was set.</param>
/// <param name="Scheme">Credential scheme, for a hint in the UI.</param>
/// <param name="CapturedAt">Unix-ms capture time, or 0 when unknown.</param>
public sealed record AuthRecordingSummary(string Id, string Name, string? Scheme, long CapturedAt);
