// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Applies a <see cref="MockConfiguration"/>'s per-field overrides onto a
/// (schema-synthesised or recorded) <see cref="BowireRecording"/> at
/// generation time (#558). For every step whose <c>service</c>/<c>method</c>
/// an override targets, the step's JSON response is parsed, the value at the
/// override's path is set, and the response is written back.
/// </summary>
/// <remarks>
/// Only <see cref="MockConfiguration.FieldOverrides"/> is evaluated here.
/// <see cref="MockConfiguration.ConditionalRules"/> and
/// <see cref="MockConfiguration.Auth"/> are model-only in the foundation
/// slice and are consumed by the sibling editor / auth slices. The applier
/// lives in the shared <c>Mocking</c> namespace so both the standalone
/// <c>MockServer</c> and the embedded workbench mock host reuse one copy.
/// </remarks>
public static class MockConfigApplier
{
    /// <summary>
    /// Return <paramref name="recording"/> with every applicable per-field
    /// override applied to its steps' responses. Mutates the passed
    /// recording in place (and returns it) so callers can inline the call.
    /// A null / override-free config, an unparseable step response, or a
    /// path that does not resolve leaves the step untouched.
    /// </summary>
    public static BowireRecording Apply(BowireRecording recording, MockConfiguration? config)
    {
        ArgumentNullException.ThrowIfNull(recording);
        // Guard null config, and a null-or-empty override list — a
        // MockConfiguration deserialised outside Parse (or a
        // `"fieldOverrides": null` token) can carry a null collection.
        if (config?.FieldOverrides is not { Count: > 0 }) return recording;

        foreach (var step in recording.Steps)
        {
            if (string.IsNullOrEmpty(step.Response)) continue;

            var applicable = config.FieldOverrides
                .Where(o => Matches(o, step))
                .ToList();
            if (applicable.Count == 0) continue;

            JsonNode? root;
            try { root = JsonNode.Parse(step.Response); }
            catch (JsonException) { continue; }
            if (root is null) continue;

            var changed = false;
            foreach (var ov in applicable)
            {
                // A null / absent / undefined value is a no-op: JSON `null`
                // round-trips to a CLR null through JsonElement?, so absent and
                // "value": null are indistinguishable — both mean "no override".
                if (ov.Value is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value
                    || string.IsNullOrWhiteSpace(ov.JsonPath))
                {
                    continue;
                }
                if (SetAtPath(root, ov.JsonPath, value)) changed = true;
            }

            if (changed) step.Response = root.ToJsonString();
        }

        return recording;
    }

    private static bool Matches(MockFieldOverride ov, BowireRecordingStep step)
        => WildcardEquals(ov.Service, step.Service) && WildcardEquals(ov.Method, step.Method);

    // Null / empty / "*" is a wildcard; otherwise a case-insensitive equal.
    private static bool WildcardEquals(string? rule, string actual)
        => string.IsNullOrEmpty(rule)
           || rule == "*"
           || string.Equals(rule, actual, StringComparison.OrdinalIgnoreCase);

    // Walk the path segment-by-segment, creating missing intermediate
    // objects, and set the leaf. Missing array indices are NOT grown (a
    // surprising side effect); an out-of-range index leaves the node
    // untouched and returns false.
    private static bool SetAtPath(JsonNode root, string jsonPath, JsonElement value)
    {
        var segments = NormalizeJsonPath(jsonPath);
        if (segments.Length == 0) return false;

        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            if (current is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(seg, out var next) || next is null)
                {
                    var created = new JsonObject();
                    obj[seg] = created;
                    current = created;
                }
                else
                {
                    current = next;
                }
            }
            else if (current is JsonArray arr && TryIndex(seg, arr.Count, out var idx) && arr[idx] is { } elem)
            {
                current = elem;
            }
            else
            {
                return false;
            }
        }

        var leaf = segments[^1];
        // Convert the JsonElement (object / array / scalar / null) to a fresh
        // JsonNode tree. A JSON-null value serializes to a null node, which
        // assigns as a JSON null leaf.
        var node = JsonSerializer.SerializeToNode(value);
        if (current is JsonObject leafObj)
        {
            leafObj[leaf] = node;
            return true;
        }
        if (current is JsonArray leafArr && TryIndex(leaf, leafArr.Count, out var li))
        {
            leafArr[li] = node;
            return true;
        }
        return false;
    }

    private static bool TryIndex(string segment, int count, out int index)
        => int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
           && index >= 0 && index < count;

    // Mirrors MockMatchPredicates.NormalizeJsonPath so override paths use the
    // exact syntax the mock body matchers already accept: split
    // "$.user.items[0].id" (or "user.items.0.id") into ["user","items","0","id"].
    private static string[] NormalizeJsonPath(string path)
    {
        var p = path;
        if (p.StartsWith('$')) p = p[1..];
        p = p.Replace("[", ".", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal);
        return p.Split('.', StringSplitOptions.RemoveEmptyEntries);
    }
}
