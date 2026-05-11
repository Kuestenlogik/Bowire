// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Semantics;

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Phase-5 helper that resolves the effective frame-semantics annotations
/// for a single <c>(service, method)</c> pair against a captured frame and
/// produces the list of <see cref="RecordedInterpretation"/> entries that
/// land on the recording step. Lives in the <see cref="Mocking"/> namespace
/// because the recording-file shape is owned here, but the logic is
/// disk-I/O-free — pure transformation against an in-memory
/// <see cref="IAnnotationStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// The builder's contract mirrors the workbench's existing extension
/// router (<c>extensions.js → mountWidgetsForMethod</c>): walk the
/// effective annotations, group paired kinds at the same parent JSONPath,
/// and resolve the typed payload from the frame for each pairing. The
/// recorded payload is the same shape the JS-side widget consumes, so the
/// replay loop reads it byte-for-byte off disk without re-running detection
/// or pairing.
/// </para>
/// <para>
/// The builder is built around the kinds that ship in v1.3 — WGS84
/// coordinates (paired latitude + longitude), GeoJSON points,
/// <c>image.bytes</c> (with optional <c>image.mime-type</c>),
/// <c>audio.bytes</c> (with optional <c>audio.sample-rate</c>), and
/// standalone scalars covered by <c>timeseries.*</c>. Unknown / unhandled
/// semantic kinds are silently skipped — they round-trip through the
/// recording file as effective-schema entries (via
/// <see cref="BowireRecordingSchemaSnapshot"/>) but don't emit per-frame
/// interpretations until a future phase wires their payload extraction.
/// </para>
/// <para>
/// Pure-function shape: no logging, no exceptions, no static state — every
/// failure mode (missing field, type mismatch, malformed base64) returns
/// "no interpretation" rather than throwing. The recording capture path
/// is a side-channel; one rogue frame can't break the recorder.
/// </para>
/// </remarks>
public static class RecordingInterpretationBuilder
{
    /// <summary>
    /// Resolve every interpretation visible for the given
    /// <c>(serviceId, methodId, messageType, frame)</c> tuple. Returns an
    /// empty list when no annotations match — never <c>null</c>, never
    /// throws.
    /// </summary>
    /// <param name="store">
    /// Annotation store whose effective entries the builder walks. Typically
    /// the singleton <see cref="LayeredAnnotationStore"/> wired into the
    /// host's DI container.
    /// </param>
    /// <param name="serviceId">Service identifier of the recorded step.</param>
    /// <param name="methodId">Method identifier of the recorded step.</param>
    /// <param name="messageType">
    /// Discriminator value for this frame — <see cref="AnnotationKey.Wildcard"/>
    /// (<c>"*"</c>) for single-type methods.
    /// </param>
    /// <param name="frame">Decoded JSON payload rooted at the message body.</param>
    public static IReadOnlyList<RecordedInterpretation> Build(
        IAnnotationStore store,
        string serviceId,
        string methodId,
        string messageType,
        JsonElement frame)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(methodId);
        ArgumentNullException.ThrowIfNull(messageType);

        // Bucket effective annotations by parent JSONPath. The pairing
        // logic in the framework groups annotations under the same parent
        // (e.g. $.position.lat + $.position.lon both group under
        // $.position) — we mirror that grouping so the recorded interpretations
        // line up with what mountWidgetsForMethod produces at runtime.
        var byParent = new Dictionary<string, List<(string LeafPath, SemanticTag Tag)>>(StringComparer.Ordinal);
        foreach (var annotation in store.EnumerateEffective())
        {
            if (!string.Equals(annotation.Key.ServiceId, serviceId, StringComparison.Ordinal)) continue;
            if (!string.Equals(annotation.Key.MethodId, methodId, StringComparison.Ordinal)) continue;

            // Annotations under the wildcard apply to every concrete
            // discriminator; concrete entries apply only to the exact
            // discriminator value the frame carries.
            var typeMatch =
                string.Equals(annotation.Key.MessageType, messageType, StringComparison.Ordinal)
                || string.Equals(annotation.Key.MessageType, AnnotationKey.Wildcard, StringComparison.Ordinal);
            if (!typeMatch) continue;

            // None-tag is explicit suppression — the user said this is
            // NOT a coordinate / image / .... Drop it before pairing.
            if (annotation.Semantic.IsNone) continue;

            var parent = ParentPathOf(annotation.Key.JsonPath);
            if (!byParent.TryGetValue(parent, out var list))
            {
                list = [];
                byParent[parent] = list;
            }
            list.Add((annotation.Key.JsonPath, annotation.Semantic));
        }

        var results = new List<RecordedInterpretation>();
        foreach (var (parent, entries) in byParent)
        {
            // WGS84 coordinate pairing (lat + lon at same parent).
            if (TryBuildWgs84(parent, entries, frame, out var coord))
            {
                results.Add(coord);
            }

            // Image bytes — optional mime-type companion at same parent.
            foreach (var bytes in BuildImageInterpretations(parent, entries, frame))
            {
                results.Add(bytes);
            }

            // Audio bytes — optional sample-rate companion at same parent.
            foreach (var audio in BuildAudioInterpretations(parent, entries, frame))
            {
                results.Add(audio);
            }
        }

        return results;
    }

    /// <summary>
    /// Parent JSONPath of a leaf — strip the last segment. Mirrors what
    /// the JS-side pairing logic does to group siblings under the same
    /// parent object.
    /// </summary>
    /// <remarks>
    /// Bracket-indexed segments (<c>$.items[3].lat</c>) and
    /// dot-delimited segments (<c>$.position.lat</c>) are both handled —
    /// we pick the last delimiter and slice up to it. Root-level leaves
    /// (<c>$.lat</c>) return <c>$</c>.
    /// </remarks>
    internal static string ParentPathOf(string jsonPath)
    {
        if (string.IsNullOrEmpty(jsonPath)) return "$";
        var lastDot = jsonPath.LastIndexOf('.');
        var lastBracket = jsonPath.LastIndexOf('[');
        var split = Math.Max(lastDot, lastBracket);
        if (split < 0) return "$";
        return split == 0 ? "$" : jsonPath[..split];
    }

    private static bool TryBuildWgs84(
        string parent,
        List<(string LeafPath, SemanticTag Tag)> entries,
        JsonElement frame,
        out RecordedInterpretation result)
    {
        string? latPath = null;
        string? lonPath = null;
        foreach (var (path, tag) in entries)
        {
            if (string.Equals(tag.Kind, BuiltInSemanticTags.CoordinateLatitude.Kind, StringComparison.Ordinal))
            {
                latPath = path;
            }
            else if (string.Equals(tag.Kind, BuiltInSemanticTags.CoordinateLongitude.Kind, StringComparison.Ordinal))
            {
                lonPath = path;
            }
        }

        if (latPath is null || lonPath is null)
        {
            result = default!;
            return false;
        }

        if (!TryResolveDouble(frame, latPath, out var lat) ||
            !TryResolveDouble(frame, lonPath, out var lon))
        {
            result = default!;
            return false;
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            lat,
            lon,
            latPath,
            lonPath,
        });
        result = new RecordedInterpretation(
            Kind: "coordinate.wgs84",
            Path: parent,
            Payload: payload);
        return true;
    }

    private static IEnumerable<RecordedInterpretation> BuildImageInterpretations(
        string parent,
        List<(string LeafPath, SemanticTag Tag)> entries,
        JsonElement frame)
    {
        string? bytesPath = null;
        string? mimePath = null;
        foreach (var (path, tag) in entries)
        {
            if (string.Equals(tag.Kind, BuiltInSemanticTags.ImageBytes.Kind, StringComparison.Ordinal))
            {
                bytesPath = path;
            }
            else if (string.Equals(tag.Kind, BuiltInSemanticTags.ImageMimeType.Kind, StringComparison.Ordinal))
            {
                mimePath = path;
            }
        }

        if (bytesPath is null) yield break;
        if (!TryResolveString(frame, bytesPath, out var data)) yield break;

        string? mime = null;
        if (mimePath is not null) TryResolveString(frame, mimePath, out mime);

        var payload = mime is null
            ? JsonSerializer.SerializeToElement(new { data, bytesPath })
            : JsonSerializer.SerializeToElement(new { data, mimeType = mime, bytesPath, mimePath });

        yield return new RecordedInterpretation(
            Kind: BuiltInSemanticTags.ImageBytes.Kind,
            Path: parent,
            Payload: payload);
    }

    private static IEnumerable<RecordedInterpretation> BuildAudioInterpretations(
        string parent,
        List<(string LeafPath, SemanticTag Tag)> entries,
        JsonElement frame)
    {
        string? bytesPath = null;
        string? ratePath = null;
        foreach (var (path, tag) in entries)
        {
            if (string.Equals(tag.Kind, BuiltInSemanticTags.AudioBytes.Kind, StringComparison.Ordinal))
            {
                bytesPath = path;
            }
            else if (string.Equals(tag.Kind, BuiltInSemanticTags.AudioSampleRate.Kind, StringComparison.Ordinal))
            {
                ratePath = path;
            }
        }

        if (bytesPath is null) yield break;
        if (!TryResolveString(frame, bytesPath, out var data)) yield break;

        double? rate = null;
        if (ratePath is not null && TryResolveDouble(frame, ratePath, out var r)) rate = r;

        var payload = rate is null
            ? JsonSerializer.SerializeToElement(new { data, bytesPath })
            : JsonSerializer.SerializeToElement(new { data, sampleRate = rate.Value, bytesPath, ratePath });

        yield return new RecordedInterpretation(
            Kind: BuiltInSemanticTags.AudioBytes.Kind,
            Path: parent,
            Payload: payload);
    }

    /// <summary>
    /// Resolve a JSONPath against the frame and return the value as a
    /// <see cref="double"/> when the leaf is a number. <c>false</c> on
    /// any failure (missing leaf, wrong type, non-numeric string).
    /// </summary>
    internal static bool TryResolveDouble(JsonElement frame, string jsonPath, out double value)
    {
        value = 0;
        if (!TryResolve(frame, jsonPath, out var el)) return false;
        if (el.ValueKind != JsonValueKind.Number) return false;
        return el.TryGetDouble(out value);
    }

    /// <summary>
    /// Resolve a JSONPath and return the leaf string when present.
    /// </summary>
    internal static bool TryResolveString(JsonElement frame, string jsonPath, out string? value)
    {
        value = null;
        if (!TryResolve(frame, jsonPath, out var el)) return false;
        if (el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString();
        return value is not null;
    }

    /// <summary>
    /// Minimal JSONPath resolver — supports the dotted + bracket-index
    /// shapes the framework's existing detectors produce
    /// (<c>$.position.lat</c>, <c>$.items[3].name</c>). Anything more
    /// complex (filter expressions, slices, recursive descent) is
    /// outside the framework's scope and returns <c>false</c>.
    /// </summary>
    internal static bool TryResolve(JsonElement frame, string jsonPath, out JsonElement leaf)
    {
        leaf = default;
        if (string.IsNullOrEmpty(jsonPath)) return false;
        if (jsonPath[0] != '$') return false;
        if (jsonPath.Length == 1)
        {
            leaf = frame;
            return true;
        }

        var current = frame;
        var i = 1;
        while (i < jsonPath.Length)
        {
            var c = jsonPath[i];
            if (c == '.')
            {
                // Dotted segment — read the property name up to next . or [.
                i++;
                var start = i;
                while (i < jsonPath.Length && jsonPath[i] != '.' && jsonPath[i] != '[') i++;
                var name = jsonPath[start..i];
                if (name.Length == 0) return false;
                if (current.ValueKind != JsonValueKind.Object) return false;
                if (!current.TryGetProperty(name, out var next)) return false;
                current = next;
            }
            else if (c == '[')
            {
                // Bracket segment — only support [<integer>] for now.
                var end = jsonPath.IndexOf(']', i);
                if (end < 0) return false;
                var inner = jsonPath[(i + 1)..end];
                if (!int.TryParse(inner, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var index))
                {
                    return false;
                }
                if (current.ValueKind != JsonValueKind.Array) return false;
                if (index < 0 || index >= current.GetArrayLength()) return false;
                current = current[index];
                i = end + 1;
            }
            else
            {
                return false;
            }
        }

        leaf = current;
        return true;
    }
}
