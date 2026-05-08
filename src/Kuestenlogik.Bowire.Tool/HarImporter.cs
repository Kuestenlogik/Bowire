// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Maps HAR 1.2 documents to <see cref="BowireRecording"/> instances.
///
/// <para>
/// HAR is a well-specified browser-trace format produced by Chrome / Firefox
/// DevTools, Charles, Fiddler, Postman, and Playwright (<c>browser.newContext({
/// recordHar })</c>). Every <c>entries[]</c> element pairs one HTTP request
/// with its response — exactly what a Bowire REST recording step holds. The
/// mapping is one-to-one for unary REST traffic; streaming / WebSocket / gRPC
/// frames inside a HAR are out of scope (HAR has no native shape for them and
/// the Bowire native recorder covers those protocols directly).
/// </para>
///
/// <para>
/// Symmetric to <c>exportRecordingAsHar</c> in the workbench: a recording
/// exported as HAR and re-imported via this class round-trips the unary REST
/// steps. Lossy fields (<c>cache</c>, <c>timings</c>, page IDs, server IPs)
/// are intentionally dropped — the recording format only carries what the
/// mock replayer needs.
/// </para>
/// </summary>
internal static class HarImporter
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// Parse a HAR document and return a fresh <see cref="BowireRecording"/>.
    /// Throws <see cref="HarImportException"/> with a user-facing message on
    /// any structural problem (missing <c>log</c>, malformed entries, …).
    /// </summary>
    /// <param name="harJson">HAR 1.2 document content.</param>
    /// <param name="recordingName">
    /// Optional name for the resulting recording. Defaults to the HAR's
    /// <c>creator.name</c> — or the literal <c>"Imported HAR"</c> when the
    /// creator field is missing.
    /// </param>
    public static BowireRecording Convert(string harJson, string? recordingName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harJson);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(harJson);
        }
        catch (JsonException ex)
        {
            throw new HarImportException("Input is not valid JSON.", ex);
        }

        var log = root?["log"]
            ?? throw new HarImportException("HAR document is missing the top-level \"log\" object.");
        var entries = log["entries"] as JsonArray
            ?? throw new HarImportException("HAR document has no \"log.entries\" array.");

        var name = recordingName ?? log["creator"]?["name"]?.GetValue<string>() ?? "Imported HAR";

        var recording = new BowireRecording
        {
            Id = $"rec_har_{Guid.NewGuid():N}",
            Name = name,
            Description = $"Imported from HAR ({entries.Count} {(entries.Count == 1 ? "entry" : "entries")})",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RecordingFormatVersion = 2
        };

        var index = 0;
        foreach (var entry in entries)
        {
            if (entry is null) continue;
            var step = MapEntry(entry, index++);
            if (step is not null) recording.Steps.Add(step);
        }

        return recording;
    }

    /// <summary>
    /// Convenience wrapper that reads a HAR file from disk and writes the
    /// resulting recording as JSON to <paramref name="outPath"/>. Pass
    /// <c>"-"</c> as <paramref name="outPath"/> to stream to stdout (useful
    /// for piping into <c>bowire mock --recording -</c>).
    /// </summary>
    /// <returns>0 on success, non-zero on failure (CLI exit-code shape).</returns>
    public static async Task<int> ImportAsync(
        string harPath, string outPath, string? recordingName, TextWriter? stderr = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outPath);
        stderr ??= Console.Error;

        if (!File.Exists(harPath))
        {
            await stderr.WriteLineAsync($"HAR file not found: {harPath}").ConfigureAwait(false);
            return 1;
        }

        BowireRecording recording;
        try
        {
            var content = await File.ReadAllTextAsync(harPath).ConfigureAwait(false);
            recording = Convert(content, recordingName);
        }
        catch (HarImportException ex)
        {
            await stderr.WriteLineAsync($"HAR import failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        var json = JsonSerializer.Serialize(recording, IndentedJson);

        if (outPath == "-")
        {
            await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
        }
        else
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(outPath, json).ConfigureAwait(false);
            await Console.Out.WriteLineAsync(
                $"Imported {recording.Steps.Count} {(recording.Steps.Count == 1 ? "step" : "steps")} → {outPath}")
                .ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// Map one HAR entry to a Bowire recording step. Returns <c>null</c> for
    /// entries that don't carry enough fields to round-trip (no method, no
    /// URL) — those would replay as 404s anyway.
    /// </summary>
    private static BowireRecordingStep? MapEntry(JsonNode entry, int index)
    {
        var request = entry["request"];
        var response = entry["response"];
        if (request is null) return null;

        var method = request["method"]?.GetValue<string>();
        var url = request["url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(url)) return null;

        // Path + service derivation. The path goes verbatim into HttpPath
        // for the mock matcher. Service / Method names come from the path
        // segments — mock-server matching is wire-level (verb + path) so
        // these are mostly cosmetic, but the workbench uses them in the
        // sidebar tree.
        var (path, host) = SplitUrl(url);
        var (service, methodName) = DeriveServiceAndMethod(path, method);

        var step = new BowireRecordingStep
        {
            Id = $"step_har_{index:D4}",
            CapturedAt = ParseStartedDateTime(entry["startedDateTime"]),
            Protocol = "rest",
            Service = service,
            Method = methodName,
            MethodType = "Unary",
            ServerUrl = host,
            HttpVerb = method,
            HttpPath = path,
            Status = MapStatus(response),
            DurationMs = ExtractDurationMs(entry),
            Body = ExtractRequestBody(request),
            Response = ExtractResponseBody(response),
            Metadata = ExtractHeaders(request)
        };

        if (!string.IsNullOrEmpty(step.Body)) step.Messages.Add(step.Body);

        return step;
    }

    /// <summary>
    /// Pull the path + (optional) host out of a URL. Relative URLs in HAR are
    /// rare but legal — those land with <c>host</c> as <c>null</c> and the
    /// matcher only keys on the path.
    /// </summary>
    internal static (string Path, string? Host) SplitUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
        {
            var path = abs.PathAndQuery;
            if (string.IsNullOrEmpty(path)) path = "/";
            return (path, $"{abs.Scheme}://{abs.Authority}");
        }
        // Relative path — hand it back as-is so the matcher still has
        // something to key on. No host means the recording is portable
        // across base URLs.
        return (url.StartsWith('/') ? url : "/" + url, null);
    }

    /// <summary>
    /// Derive a (service, method) pair from a request path. Heuristics:
    /// last meaningful path segment becomes the method name; the segment
    /// before that becomes the service. <c>GET /users/42</c> →
    /// <c>(users, GET_42)</c> — but pure-numeric segments fall back to the
    /// verb-prefixed name to avoid ID-as-method noise. Empty paths use
    /// <c>"http"</c> as the catch-all service.
    /// </summary>
    internal static (string Service, string Method) DeriveServiceAndMethod(string path, string verb)
    {
        var pathOnly = path.Split('?', 2)[0].Trim('/');
        if (string.IsNullOrEmpty(pathOnly)) return ("http", verb.ToUpperInvariant());

        var segments = pathOnly.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return ("http", verb.ToUpperInvariant());

        var last = segments[^1];
        var service = segments.Length >= 2 ? segments[^2] : "http";

        // Numeric / GUID-shaped tail segment → use the parent segment as
        // the method (typical RESTful "get one by id" case). Keeps the
        // sidebar tidy when the same endpoint shows up with different ids.
        if (LooksLikeId(last))
        {
            var prev = segments.Length >= 2 ? segments[^2] : "http";
            var parent = segments.Length >= 3 ? segments[^3] : "http";
            return (parent, $"{verb.ToUpperInvariant()}_{prev}");
        }

        return (service, $"{verb.ToUpperInvariant()}_{last}");
    }

    /// <summary>
    /// Loose "looks like an id" check — purely-numeric or GUID-shaped
    /// segments stay out of the derived method name.
    /// </summary>
    private static bool LooksLikeId(string segment) =>
        segment.All(char.IsDigit) || Guid.TryParse(segment, out _);

    /// <summary>
    /// Pull the request body out of <c>postData.text</c>. Some HAR producers
    /// (Charles, Fiddler) drop the body when its size exceeds a threshold;
    /// in that case the field is absent and we return <c>null</c>.
    /// </summary>
    private static string? ExtractRequestBody(JsonNode request)
        => request["postData"]?["text"]?.GetValue<string>();

    /// <summary>
    /// Pull the response body out of <c>response.content.text</c>. HAR
    /// supports base64-encoded binary bodies via an <c>encoding</c> field,
    /// but practical HAR files only cover text bodies — base64 binary is
    /// out of scope for this iteration.
    /// </summary>
    private static string? ExtractResponseBody(JsonNode? response)
        => response?["content"]?["text"]?.GetValue<string>();

    /// <summary>
    /// Convert a HAR <c>request.headers</c> array into a string→string
    /// metadata dictionary, preserving the recording format's wire-level
    /// header convention. Cookies stay in <c>Cookie</c>/<c>Set-Cookie</c>
    /// headers — the dedicated HAR <c>cookies</c> array is redundant info.
    /// </summary>
    private static Dictionary<string, string>? ExtractHeaders(JsonNode request)
    {
        if (request["headers"] is not JsonArray headers || headers.Count == 0) return null;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            var name = header?["name"]?.GetValue<string>();
            var value = header?["value"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name) || value is null) continue;
            // Last-write wins for duplicate headers — matches the matcher's
            // header-substitution behaviour.
            dict[name] = value;
        }
        return dict.Count == 0 ? null : dict;
    }

    /// <summary>
    /// Translate the HAR response status into Bowire's status string. HTTP
    /// 2xx → "OK" so REST replay lands in the green-frame path the way
    /// native Bowire captures do; everything else gets the verbatim numeric
    /// code so mock mismatches stay visible.
    /// </summary>
    private static string MapStatus(JsonNode? response)
    {
        var status = response?["status"]?.GetValue<int?>() ?? 0;
        if (status >= 200 && status < 300) return "OK";
        return status.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Total wall-clock duration in milliseconds. HAR's top-level
    /// <c>entry.time</c> is the convenient sum of every phase; falls
    /// back to summing <c>timings.*</c> when <c>time</c> is missing /
    /// negative.
    /// </summary>
    private static long ExtractDurationMs(JsonNode entry)
    {
        var top = entry["time"]?.GetValue<double?>();
        if (top is double t && t >= 0) return (long)Math.Round(t);

        if (entry["timings"] is JsonObject timings)
        {
            double sum = 0;
            foreach (var key in new[] { "blocked", "dns", "connect", "send", "wait", "receive", "ssl" })
            {
                var v = timings[key]?.GetValue<double?>() ?? 0;
                if (v > 0) sum += v;
            }
            return (long)Math.Round(sum);
        }
        return 0;
    }

    /// <summary>
    /// Parse the HAR <c>startedDateTime</c> ISO-8601 string into a Unix
    /// millisecond timestamp. Falls back to <c>0</c> on parse failure so
    /// the mapper never aborts on a stray timestamp.
    /// </summary>
    private static long ParseStartedDateTime(JsonNode? node)
    {
        var raw = node?.GetValue<string>();
        if (string.IsNullOrEmpty(raw)) return 0;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dto)
            ? dto.ToUnixTimeMilliseconds()
            : 0;
    }
}

/// <summary>Thrown by <see cref="HarImporter.Convert"/> when the HAR document is malformed.</summary>
internal sealed class HarImportException : Exception
{
    public HarImportException() { }
    public HarImportException(string message) : base(message) { }
    public HarImportException(string message, Exception inner) : base(message, inner) { }
}
