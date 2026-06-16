// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuestenlogik.Bowire.Workspace.Git;

/// <summary>
/// Merges <c>&lt;env&gt;.secrets.json</c> overlays on top of the
/// committed <c>&lt;env&gt;.json</c> at load time. Implements the
/// secret-separation convention shipped in Phase 1: the non-secret
/// half is the reviewable, git-tracked half; <c>.secrets.json</c>
/// siblings carry tokens and are <c>.gitignore</c>'d at workspace
/// init.
/// </summary>
/// <remarks>
/// <para>
/// Both documents are JSON objects with the per-env shape the
/// workbench writes — typically <c>{ id, name, variables: { … } }</c>
/// — but the overlay only needs to carry the keys it overrides. The
/// merger walks the object's top-level properties; when a property
/// is itself an object (e.g. <c>variables</c>), the merge recurses
/// one level so a partial secrets file with just one new variable
/// adds rather than wholly replaces the base map.
/// </para>
/// <para>
/// Override semantics:
/// <list type="bullet">
/// <item>Scalar property in overlay replaces scalar / array / object
/// in base.</item>
/// <item>Object property in overlay merges with object property in
/// base (recursive one level — flat maps inside <c>variables</c>).</item>
/// <item>Array property in overlay replaces array in base (no
/// concat).</item>
/// </list>
/// Keeps the rule predictable: a secrets file never deletes a base
/// entry, only adds / overrides it.
/// </para>
/// </remarks>
public static class SecretOverlayMerger
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Merge <paramref name="overlayJson"/> on top of
    /// <paramref name="baseJson"/> and return the serialised result.
    /// Either input may be <c>null</c>; a null overlay returns the
    /// base verbatim, a null base falls back to the overlay alone.
    /// </summary>
    /// <exception cref="JsonException">
    /// Either input is not a valid JSON object.
    /// </exception>
    public static string? Merge(string? baseJson, string? overlayJson)
    {
        if (string.IsNullOrWhiteSpace(overlayJson)) return baseJson;
        if (string.IsNullOrWhiteSpace(baseJson))
        {
            // Re-serialise the overlay through JsonNode so the
            // returned string matches the indented camelCase shape
            // the rest of the store emits.
            var overlayOnly = JsonNode.Parse(overlayJson) as JsonObject
                ?? throw new JsonException("Secrets overlay must be a JSON object.");
            return overlayOnly.ToJsonString(s_jsonOpts);
        }

        var baseObj = JsonNode.Parse(baseJson) as JsonObject
            ?? throw new JsonException("Base document must be a JSON object.");
        var overlayObj = JsonNode.Parse(overlayJson) as JsonObject
            ?? throw new JsonException("Secrets overlay must be a JSON object.");

        MergeInPlace(baseObj, overlayObj);
        return baseObj.ToJsonString(s_jsonOpts);
    }

    private static void MergeInPlace(JsonObject target, JsonObject overlay)
    {
        foreach (var kvp in overlay)
        {
            var overlayValue = kvp.Value;
            if (overlayValue is JsonObject overlaySub
                && target[kvp.Key] is JsonObject targetSub)
            {
                // Recurse one level — covers the variables / globals
                // shape without unbounded depth.
                MergeInPlace(targetSub, overlaySub);
                continue;
            }
            // Scalar / array / null / object-replacing-non-object —
            // deep-clone so the source overlay tree is preserved if
            // the caller wants to merge it onto something else.
            target[kvp.Key] = overlayValue?.DeepClone();
        }
    }
}
