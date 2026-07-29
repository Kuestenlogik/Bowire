// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Recordings.Correlation;

/// <summary>
/// Callback shape <see cref="RecordingCorrelationScanner"/> hands every
/// scalar JSON leaf to. Both spellings are supplied because the two
/// consumers need different ones: matching compares the normalised
/// form, suggestion displays the original.
/// </summary>
/// <param name="name">The owning property's name as written.</param>
/// <param name="normalizedName">Lower-cased, alphanumerics only.</param>
/// <param name="value">The leaf value as invariant text.</param>
internal delegate void CorrelationLeafVisitor(string name, string normalizedName, string value);

/// <summary>
/// The shared primitives <see cref="RecordingCorrelationAnalyzer"/> is
/// built from (#539) — name normalisation, JSON leaf walking, and the
/// correlation-header lookup. Kept separate so both halves of the
/// analyzer (candidate suggestion and per-step matching) walk payloads
/// through exactly one implementation.
/// </summary>
internal static class RecordingCorrelationScanner
{
    /// <summary>
    /// Correlation headers we recognise, in precedence order. The list is
    /// deliberately short and conventional: these are the names an HTTP
    /// or messaging middleware actually stamps, and a longer list would
    /// start guessing.
    /// </summary>
    private static readonly string[] s_headerNames =
    [
        "traceparent",
        "x-correlation-id",
        "correlation-id",
        "x-request-id",
        "request-id",
        "x-trace-id",
        "trace-id",
    ];

    /// <summary>
    /// Lower-case and strip every non-alphanumeric character, so
    /// <c>X-Correlation-Id</c>, <c>x_correlation_id</c> and
    /// <c>correlationId</c> all collapse onto the same token. This is
    /// what makes suffix matching (<c>onShipId</c> ends with
    /// <c>shipid</c>) work across protocols that disagree about casing
    /// and separators.
    /// </summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Walk a JSON document and hand every scalar leaf to
    /// <paramref name="onLeaf"/>. Non-JSON text is skipped silently — step
    /// bodies legitimately carry raw GraphQL SDL, form encodings and
    /// empty strings, and none of those are an error here.
    /// </summary>
    public static void ScanLeaves(string? json, CorrelationLeafVisitor onLeaf)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        // Cheap pre-filter: JsonDocument.Parse on a non-JSON string is a
        // thrown exception per step per surface, and a recording has
        // many of both.
        var first = json.AsSpan().TrimStart();
        if (first.Length == 0 || (first[0] != '{' && first[0] != '[')) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return; }
        using (doc) { ScanElement(doc.RootElement, string.Empty, onLeaf, 0); }
    }

    /// <summary>
    /// Walk an already-parsed element (the shape streaming frames arrive
    /// in — <c>BowireRecordingFrame.Data</c> is <c>object?</c>, which
    /// System.Text.Json materialises as a <see cref="JsonElement"/>).
    /// </summary>
    public static void ScanValue(object? value, CorrelationLeafVisitor onLeaf)
    {
        switch (value)
        {
            case null:
                return;
            case JsonElement element:
                ScanElement(element, string.Empty, onLeaf, 0);
                return;
            case string text:
                ScanLeaves(text, onLeaf);
                return;
            default:
                // Anything else came from an in-process producer rather
                // than the deserialiser; round-trip it so the same
                // walker applies.
                ScanLeaves(JsonSerializer.Serialize(value), onLeaf);
                return;
        }
    }

    /// <summary>
    /// Every JSON-bearing surface of one step, in a stable order:
    /// request body, each request message, the response, each sent
    /// frame's body and data, and each received frame's data.
    /// </summary>
    public static void ScanStep(BowireRecordingStep step, CorrelationLeafVisitor onLeaf)
    {
        if (step is null) return;
        ScanLeaves(step.Body, onLeaf);
        foreach (var message in step.Messages) ScanLeaves(message, onLeaf);
        ScanLeaves(step.Response, onLeaf);
        if (step.SentMessages is not null)
        {
            foreach (var frame in step.SentMessages) ScanFrame(frame, onLeaf);
        }
        if (step.ReceivedMessages is not null)
        {
            foreach (var frame in step.ReceivedMessages) ScanFrame(frame, onLeaf);
        }
    }

    /// <summary>One streaming frame's payload surfaces.</summary>
    public static void ScanFrame(BowireRecordingFrame? frame, CorrelationLeafVisitor onLeaf)
    {
        if (frame is null) return;
        ScanLeaves(frame.Body, onLeaf);
        ScanValue(frame.Data, onLeaf);
    }

    /// <summary>
    /// Look up the first recognised correlation header on a step's
    /// metadata. For <c>traceparent</c> the trace-id (field 2 of
    /// <c>00-&lt;32hex&gt;-&lt;16hex&gt;-&lt;flags&gt;</c>) is returned
    /// rather than the whole header, because the span-id changes per hop
    /// and would never correlate.
    /// </summary>
    /// <returns><see langword="true"/> when a header was found.</returns>
    public static bool TryReadCorrelationHeader(
        IDictionary<string, string>? metadata,
        out string name,
        out string value)
    {
        name = string.Empty;
        value = string.Empty;
        if (metadata is null || metadata.Count == 0) return false;

        // Metadata dictionaries arrive from JSON with whatever casing the
        // capture used; normalise both sides rather than assuming an
        // OrdinalIgnoreCase comparer is in play.
        foreach (var wanted in s_headerNames)
        {
            var wantedNorm = NormalizeName(wanted);
            foreach (var kv in metadata)
            {
                if (!string.Equals(NormalizeName(kv.Key), wantedNorm, StringComparison.Ordinal)) continue;
                var raw = kv.Value;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                name = kv.Key;
                value = string.Equals(wantedNorm, "traceparent", StringComparison.Ordinal)
                    ? ExtractTraceId(raw)
                    : raw.Trim();
                return !string.IsNullOrEmpty(value);
            }
        }
        return false;
    }

    /// <summary>
    /// Pull the trace-id out of a W3C <c>traceparent</c>. A value that
    /// does not have the four dash-separated fields is handed back
    /// verbatim — a non-standard middleware still correlates, it just
    /// correlates on whatever it wrote.
    /// </summary>
    private static string ExtractTraceId(string traceparent)
    {
        var parts = traceparent.Trim().Split('-');
        return parts.Length >= 3 && parts[1].Length > 0 ? parts[1] : traceparent.Trim();
    }

    private static void ScanElement(JsonElement element, string name, CorrelationLeafVisitor onLeaf, int depth)
    {
        // Depth guard: a hostile / pathological payload should cost a
        // truncated scan, not a stack overflow. 64 is far past any real
        // API response shape.
        if (depth > 64) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    ScanElement(property.Value, property.Name, onLeaf, depth + 1);
                }
                return;
            case JsonValueKind.Array:
                // Array items inherit the owning property's name — a
                // `containers: [{ onShipId: 101 }]` leaf must still read
                // as `onShipId`, and a bare `[101]` as its parent name.
                foreach (var item in element.EnumerateArray())
                {
                    ScanElement(item, name, onLeaf, depth + 1);
                }
                return;
            case JsonValueKind.String:
                Emit(name, element.GetString(), onLeaf);
                return;
            case JsonValueKind.Number:
                Emit(name, element.GetRawText(), onLeaf);
                return;
            case JsonValueKind.True:
                Emit(name, "true", onLeaf);
                return;
            case JsonValueKind.False:
                Emit(name, "false", onLeaf);
                return;
            default:
                return;
        }
    }

    private static void Emit(string name, string? value, CorrelationLeafVisitor onLeaf)
    {
        if (string.IsNullOrEmpty(value)) return;
        var normalized = NormalizeName(name);
        if (normalized.Length == 0) return;
        onLeaf(name, normalized, value.Trim());
    }

    /// <summary>
    /// Culture-invariant text form used when a value has to be compared
    /// against a caller-supplied key. Numbers arrive as raw JSON text
    /// already, so this only trims.
    /// </summary>
    public static string NormalizeValue(string? value)
        => (value ?? string.Empty).Trim();

    /// <summary>Invariant formatting for the few numeric values we surface in messages.</summary>
    public static string Format(long value)
        => value.ToString(CultureInfo.InvariantCulture);
}
