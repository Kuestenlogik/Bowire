// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Flows.Expectations;

/// <summary>
/// Snapshot ("golden baseline") configuration on a Flow step — #171.
/// First run captures the response body into a snapshot file; subsequent
/// runs diff actual against the baseline and fail on drift. Complements
/// the structural <see cref="FlowExpectation"/> checks: expectations say
/// "these fields must hold", the snapshot says "nothing ELSE changed
/// either".
/// </summary>
/// <remarks>
/// Wire shape on the step:
/// <code>
/// { "snapshot": { "mode": "exact", "ignore": ["$.updatedAt", "$.items.*.id"] } }
/// </code>
/// </remarks>
public sealed class FlowSnapshotConfig
{
    /// <summary>
    /// Toggle without deleting the config block. Default true — the
    /// presence of a <c>snapshot</c> object opts the step in.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Comparison strictness.</summary>
    [JsonPropertyName("mode")]
    public FlowSnapshotMode Mode { get; set; } = FlowSnapshotMode.Exact;

    /// <summary>
    /// Dotted paths whose VALUES are exempt from comparison (timestamps,
    /// UUIDs, request ids). Shape still holds — a marked field must still
    /// exist with the same JSON kind. <c>*</c> matches any one segment
    /// (array index or object key). Optional <c>$.</c> prefix.
    /// </summary>
    [JsonPropertyName("ignore")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "System.Text.Json populates the collection via the setter at deserialisation time.")]
    public List<string> Ignore { get; set; } = new();
}

/// <summary>How strictly a snapshot baseline is compared.</summary>
public enum FlowSnapshotMode
{
    /// <summary>Normalised-JSON equality (non-JSON bodies: ordinal string equality).</summary>
    Exact,
    /// <summary>Type-shape equality — object keys + JSON kinds must hold, leaf values may vary.</summary>
    Structural,
}

/// <summary>
/// Pure-function snapshot comparison. Lives beside
/// <see cref="FlowExpectationEvaluator"/> so the CLI runner and the
/// workbench's approve-snapshot surface share one diff implementation.
/// </summary>
public static class FlowSnapshotComparer
{
    /// <summary>Cap on reported diffs — beyond this the comparison is unambiguous anyway.</summary>
    private const int MaxDiffs = 10;

    /// <summary>
    /// Diff <paramref name="actual"/> against <paramref name="baseline"/>.
    /// Returns an empty list when the snapshot holds; otherwise
    /// human-readable per-path diff lines (capped at 10, with a rollup
    /// line when more were found).
    /// </summary>
    public static IReadOnlyList<string> Compare(
        string? baseline, string? actual, FlowSnapshotMode mode, IReadOnlyCollection<string>? ignorePaths = null)
    {
        var ignore = NormaliseIgnore(ignorePaths);
        var baselineNode = TryParse(baseline);
        var actualNode = TryParse(actual);

        // Non-JSON bodies: exact → ordinal compare; structural → both
        // sides must at least agree on "is JSON at all".
        if (baselineNode is null && actualNode is null)
        {
            if (mode == FlowSnapshotMode.Structural) return Array.Empty<string>();
            return string.Equals(baseline ?? "", actual ?? "", StringComparison.Ordinal)
                ? Array.Empty<string>()
                : new[] { "$: response text differs from snapshot" };
        }
        if (baselineNode is null || actualNode is null)
        {
            return new[] { "$: one side is JSON, the other is not" };
        }

        var diffs = new List<string>();
        var total = 0;
        Walk(baselineNode, actualNode, "$", mode, ignore, diffs, ref total);
        if (total > diffs.Count)
        {
            diffs.Add($"… and {total - diffs.Count} more difference(s)");
        }
        return diffs;
    }

    private static JsonNode? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static List<string[]> NormaliseIgnore(IReadOnlyCollection<string>? paths)
    {
        var result = new List<string[]>();
        if (paths is null) return result;
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var trimmed = p.Trim();
            if (trimmed.StartsWith("$.", StringComparison.Ordinal)) trimmed = trimmed[2..];
            else if (trimmed.StartsWith('$')) trimmed = trimmed[1..].TrimStart('.');
            result.Add(trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries));
        }
        return result;
    }

    private static bool IsIgnored(string path, List<string[]> ignore)
    {
        // path is "$.a.b.0"; compare segment-wise against each pattern,
        // "*" matching any single segment.
        var segments = path.Length > 1 ? path[2..].Split('.') : Array.Empty<string>();
        foreach (var pattern in ignore)
        {
            if (pattern.Length != segments.Length) continue;
            var match = true;
            for (var i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] == "*") continue;
                if (!string.Equals(pattern[i], segments[i], StringComparison.Ordinal)) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    private static void Walk(
        JsonNode? baseline, JsonNode? actual, string path,
        FlowSnapshotMode mode, List<string[]> ignore, List<string> diffs, ref int total)
    {
        var baseKind = KindOf(baseline);
        var actKind = KindOf(actual);
        if (baseKind != actKind)
        {
            // Kind drift is a shape change — reported even on ignored
            // paths ("ignore-value, keep-shape").
            AddDiff(diffs, ref total, $"{path}: kind changed — snapshot {baseKind}, actual {actKind}");
            return;
        }

        switch (baseline)
        {
            case JsonObject baseObj:
                var actObj = (JsonObject)actual!;
                foreach (var (key, baseVal) in baseObj)
                {
                    var childPath = $"{path}.{key}";
                    if (!actObj.TryGetPropertyValue(key, out var actVal))
                    {
                        AddDiff(diffs, ref total, $"{childPath}: missing (present in snapshot)");
                        continue;
                    }
                    Walk(baseVal, actVal, childPath, mode, ignore, diffs, ref total);
                }
                foreach (var (key, _) in actObj)
                {
                    if (!baseObj.ContainsKey(key))
                    {
                        AddDiff(diffs, ref total, $"{path}.{key}: unexpected (absent from snapshot)");
                    }
                }
                break;

            case JsonArray baseArr:
                var actArr = (JsonArray)actual!;
                // Exact mode: lengths must match. Structural mode: lists
                // may legitimately grow/shrink (paged endpoints) — only
                // the element shapes are compared, pairwise up to the
                // shorter length.
                if (mode == FlowSnapshotMode.Exact && baseArr.Count != actArr.Count)
                {
                    AddDiff(diffs, ref total, $"{path}: array length changed — snapshot {baseArr.Count}, actual {actArr.Count}");
                }
                var n = Math.Min(baseArr.Count, actArr.Count);
                for (var i = 0; i < n; i++)
                {
                    Walk(baseArr[i], actArr[i], $"{path}.{i}", mode, ignore, diffs, ref total);
                }
                break;

            default:
                // Leaf. Structural mode only checks the kind (done above);
                // exact mode compares values unless the path is ignored.
                if (mode == FlowSnapshotMode.Exact && !IsIgnored(path, ignore))
                {
                    var baseText = baseline?.ToJsonString() ?? "null";
                    var actText = actual?.ToJsonString() ?? "null";
                    if (!string.Equals(baseText, actText, StringComparison.Ordinal))
                    {
                        AddDiff(diffs, ref total, $"{path}: snapshot {Truncate(baseText)}, actual {Truncate(actText)}");
                    }
                }
                break;
        }
    }

    private static string KindOf(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v when v.TryGetValue<bool>(out _) => "boolean",
        JsonValue v when v.TryGetValue<double>(out _) => "number",
        JsonValue v when v.TryGetValue<string>(out _) => "string",
        _ => "value",
    };

    private static void AddDiff(List<string> diffs, ref int total, string message)
    {
        total++;
        if (diffs.Count < MaxDiffs) diffs.Add(message);
    }

    private static string Truncate(string s)
        => s.Length <= 60 ? s : string.Concat(s.AsSpan(0, 60), "…");
}
