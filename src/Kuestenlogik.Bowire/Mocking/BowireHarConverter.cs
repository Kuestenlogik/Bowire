// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuestenlogik.Bowire.Mocking;

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
///
/// <para>
/// Lives in <c>Kuestenlogik.Bowire</c> (core) so both the CLI (<c>bowire
/// import har</c>) and the MCP tool (<c>bowire.har.import</c>) can call it
/// without depending on the CLI host project. The Tool's <c>HarImporter</c>
/// wraps this with file-IO + exit-code shape for the CLI.
/// </para>
/// </summary>
public static class BowireHarConverter
{
    private static readonly string[] TimingPhaseKeys =
        ["blocked", "dns", "connect", "send", "wait", "receive", "ssl"];

    /// <summary>
    /// Header names that carry credentials / session material. HAR traces
    /// straight out of DevTools routinely contain live bearer tokens and
    /// session cookies; these drive both the redaction pass
    /// (<see cref="Convert(string, string?, bool)"/> with <c>redactSecrets</c>)
    /// and the auth-context surfacing (<see cref="DetectAuthHeaders"/>, #190).
    /// </summary>
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
        "X-Api-Key", "X-Api-Token", "X-Auth-Token", "X-Access-Token",
        "X-Csrf-Token", "X-Xsrf-Token", "Api-Key", "Authentication",
    };

    /// <summary>Placeholder written in place of a redacted header value.</summary>
    public const string RedactedPlaceholder = "***redacted***";

    /// <summary>
    /// Parse a HAR document and return a fresh <see cref="BowireRecording"/>.
    /// Throws <see cref="BowireHarImportException"/> with a user-facing
    /// message on any structural problem (missing <c>log</c>, malformed
    /// entries, …).
    /// </summary>
    /// <param name="harJson">HAR 1.2 document content.</param>
    /// <param name="recordingName">
    /// Optional name for the resulting recording. Defaults to the HAR's
    /// <c>creator.name</c> — or the literal <c>"Imported HAR"</c> when the
    /// creator field is missing.
    /// </param>
    public static BowireRecording Convert(string harJson, string? recordingName = null)
        => Convert(harJson, recordingName, redactSecrets: false);

    /// <summary>
    /// Parse a HAR document into a <see cref="BowireRecording"/>, optionally
    /// stripping credential-bearing headers (#186).
    /// </summary>
    /// <param name="harJson">HAR 1.2 document content.</param>
    /// <param name="recordingName">See the other overload.</param>
    /// <param name="redactSecrets">
    /// When <c>true</c>, header values in <see cref="SensitiveHeaders"/>
    /// (Authorization, Cookie, X-Api-Key, …) are replaced with
    /// <see cref="RedactedPlaceholder"/> on both request + response steps —
    /// so a HAR captured against production can be imported without persisting
    /// live tokens / session cookies into a recording file.
    /// </param>
    public static BowireRecording Convert(string harJson, string? recordingName, bool redactSecrets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harJson);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(harJson);
        }
        catch (JsonException ex)
        {
            throw new BowireHarImportException("Input is not valid JSON.", ex);
        }

        var log = root?["log"]
            ?? throw new BowireHarImportException("HAR document is missing the top-level \"log\" object.");
        var entries = log["entries"] as JsonArray
            ?? throw new BowireHarImportException("HAR document has no \"log.entries\" array.");

        var name = recordingName ?? log["creator"]?["name"]?.GetValue<string>() ?? "Imported HAR";

        var recording = new BowireRecording
        {
            // Deterministic id derived from the HAR content + name, so
            // re-importing the same trace yields the same recording id — the
            // building block a workspace/collection store keys on to dedupe
            // instead of stacking a fresh copy on every import (#186 idempotency).
            Id = DeriveRecordingId(harJson, name),
            Name = name,
            Description = $"Imported from HAR ({entries.Count} {(entries.Count == 1 ? "entry" : "entries")})",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RecordingFormatVersion = 2
        };

        var index = 0;
        foreach (var entry in entries.Where(e => e is not null))
        {
            var step = MapEntry(entry!, index++, redactSecrets);
            if (step is not null) recording.Steps.Add(step);
        }

        return recording;
    }

    private static readonly JsonSerializerOptions s_exportJson = new() { WriteIndented = true };

    /// <summary>
    /// Export a <see cref="BowireRecording"/> back to a HAR 1.2 document — the
    /// inverse of <see cref="Convert(string, string?)"/> for the unary REST/
    /// gRPC-Web steps it round-trips (#39). Deterministic: headers are sorted by
    /// name and timestamps derive from the step's captured time, so
    /// <c>ToHar(Convert(har))</c> is stable and golden-testable. Lossy fields the
    /// import already drops (cache / timings detail / page IDs) are emitted as
    /// their conventional empty shapes.
    /// </summary>
    public static string ToHar(BowireRecording recording, string? creatorName = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var entries = recording.Steps.Select(ToEntry).ToArray();
        var doc = new
        {
            log = new
            {
                version = "1.2",
                creator = new { name = creatorName ?? (string.IsNullOrEmpty(recording.Name) ? "Bowire" : recording.Name), version = "1.0" },
                entries,
            },
        };
        return JsonSerializer.Serialize(doc, s_exportJson);
    }

    private static object ToEntry(BowireRecordingStep step)
    {
        var url = (step.ServerUrl ?? "") + (step.HttpPath ?? "/");
        return new
        {
            startedDateTime = DateTimeOffset.FromUnixTimeMilliseconds(step.CapturedAt)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            time = (double)step.DurationMs,
            request = new
            {
                method = string.IsNullOrEmpty(step.HttpVerb) ? "GET" : step.HttpVerb,
                url,
                httpVersion = "HTTP/1.1",
                cookies = Array.Empty<object>(),
                headers = ToHeaderArray(step.Metadata),
                queryString = Array.Empty<object>(),
                postData = step.Body is null ? null : new { mimeType = "application/json", text = step.Body },
                headersSize = -1,
                bodySize = step.Body is null ? 0 : Encoding.UTF8.GetByteCount(step.Body),
            },
            response = new
            {
                status = StatusToCode(step.Status),
                statusText = step.Status,
                httpVersion = "HTTP/1.1",
                cookies = Array.Empty<object>(),
                headers = ToHeaderArray(step.ResponseHeaders),
                content = new
                {
                    size = step.Response is null ? 0 : Encoding.UTF8.GetByteCount(step.Response),
                    mimeType = "application/json",
                    text = step.Response ?? "",
                },
                redirectURL = "",
                headersSize = -1,
                bodySize = step.Response is null ? 0 : Encoding.UTF8.GetByteCount(step.Response),
            },
            cache = new { },
            timings = new { send = 0.0, wait = (double)step.DurationMs, receive = 0.0 },
        };
    }

    private static object[] ToHeaderArray(IDictionary<string, string>? headers)
        => headers is null
            ? []
            : headers.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (object)new { name = kv.Key, value = kv.Value }).ToArray();

    // Inverse of MapStatus: the recorder stores "OK" for 2xx and the numeric
    // code otherwise, so map back to a concrete response status.
    private static int StatusToCode(string status)
        => string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) ? 200
            : int.TryParse(status, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : 0;

    /// <summary>
    /// Scan a HAR document and return the distinct credential-bearing header
    /// names it contains (canonical casing from <see cref="SensitiveHeaders"/>),
    /// sorted. Lets the importer surface "this trace carries an Authorization /
    /// Cookie header — redact it, or feed it into auth-recording (#190)" without
    /// the operator eyeballing raw JSON. Best-effort: returns an empty list for
    /// malformed input rather than throwing.
    /// </summary>
    public static IReadOnlyList<string> DetectAuthHeaders(string harJson)
    {
        if (string.IsNullOrWhiteSpace(harJson)) return [];
        JsonArray? entries;
        try
        {
            entries = JsonNode.Parse(harJson)?["log"]?["entries"] as JsonArray;
        }
        catch (JsonException)
        {
            return [];
        }
        if (entries is null) return [];

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(e => e is not null))
        {
            CollectSensitiveHeaderNames(entry!["request"]?["headers"], found);
            CollectSensitiveHeaderNames(entry!["response"]?["headers"], found);
        }
        return found.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void CollectSensitiveHeaderNames(JsonNode? headersNode, HashSet<string> into)
    {
        if (headersNode is not JsonArray headers) return;
        foreach (var header in headers)
        {
            var name = header?["name"]?.GetValue<string>();
            if (name is not null && SensitiveHeaders.TryGetValue(name, out var canonical))
                into.Add(canonical);
        }
    }

    private static string DeriveRecordingId(string harJson, string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name + "\0" + harJson));
        return "rec_har_" + System.Convert.ToHexStringLower(hash)[..32];
    }

    /// <summary>
    /// Map one HAR entry to a Bowire recording step. Returns <c>null</c> for
    /// entries that don't carry enough fields to round-trip (no method, no
    /// URL) — those would replay as 404s anyway.
    /// </summary>
    private static BowireRecordingStep? MapEntry(JsonNode entry, int index, bool redactSecrets)
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

        // gRPC-Web rides on top of HTTP with an application/grpc-web*
        // content-type and a `/package.Service/Method` path. Classifying
        // those entries as `grpc` steps (rather than `rest`) puts them in
        // the right sidebar branch and lets the gRPC replayer own them.
        var isGrpcWeb = IsGrpcWeb(request, response);
        var (service, methodName) = isGrpcWeb
            ? DeriveGrpcServiceAndMethod(path)
            : DeriveServiceAndMethod(path, method);

        var step = new BowireRecordingStep
        {
            Id = $"step_har_{index:D4}",
            CapturedAt = ParseStartedDateTime(entry["startedDateTime"]),
            Protocol = isGrpcWeb ? "grpc" : "rest",
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
            Metadata = ExtractHeaders(request["headers"], redactSecrets),
            ResponseHeaders = ExtractHeaders(response?["headers"], redactSecrets)
        };

        if (!string.IsNullOrEmpty(step.Body)) step.Messages.Add(step.Body);

        return step;
    }

    /// <summary>
    /// Pull the path + (optional) host out of a URL. Relative URLs in HAR are
    /// rare but legal — those land with <c>host</c> as <c>null</c> and the
    /// matcher only keys on the path.
    /// </summary>
    public static (string Path, string? Host) SplitUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        // Restrict the absolute-URL path to web schemes. On POSIX,
        // Uri.TryCreate("/foo/bar", UriKind.Absolute, ...) parses as
        // a `file://` URI (Linux treats the leading slash as a real
        // filesystem path), which would mis-route a relative HAR URL
        // through the absolute branch and emit `file://` as the host.
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs)
            && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps
                || abs.Scheme == Uri.UriSchemeWs || abs.Scheme == Uri.UriSchemeWss))
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
    public static (string Service, string Method) DeriveServiceAndMethod(string path, string verb)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(verb);
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
    /// True when the entry is gRPC-Web: an <c>application/grpc-web</c>
    /// content-type on either side (covers <c>+proto</c> / <c>-text</c>
    /// variants). gRPC-Web is the only gRPC dialect that shows up in a
    /// HAR — native gRPC uses HTTP/2 frames DevTools doesn't serialise.
    /// </summary>
    private static bool IsGrpcWeb(JsonNode request, JsonNode? response)
        => HeaderContains(request["headers"], "content-type", "application/grpc-web")
        || HeaderContains(response?["headers"], "content-type", "application/grpc-web");

    /// <summary>
    /// Case-insensitive check for a header whose name equals
    /// <paramref name="name"/> and whose value contains
    /// <paramref name="valueSubstring"/>.
    /// </summary>
    private static bool HeaderContains(JsonNode? headers, string name, string valueSubstring)
    {
        if (headers is not JsonArray arr) return false;
        foreach (var header in arr)
        {
            var n = header?["name"]?.GetValue<string>();
            var v = header?["value"]?.GetValue<string>();
            if (n is null || v is null) continue;
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)
                && v.Contains(valueSubstring, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Derive a (service, method) pair from a gRPC-Web request path. The
    /// wire shape is always <c>/package.Service/Method</c>, so the last
    /// two path segments map straight onto service + method — no id
    /// heuristics needed. Falls back to a <c>grpc</c> catch-all service
    /// for malformed paths so the step still lands somewhere visible.
    /// </summary>
    public static (string Service, string Method) DeriveGrpcServiceAndMethod(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var pathOnly = path.Split('?', 2)[0].Trim('/');
        var segments = pathOnly.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            ? (segments[^2], segments[^1])
            : ("grpc", segments.Length == 1 ? segments[0] : "Unknown");
    }

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
    /// Convert a HAR <c>headers</c> array (request or response) into a
    /// string→string dictionary, preserving the recording format's wire-level
    /// header convention. Cookies stay in <c>Cookie</c>/<c>Set-Cookie</c>
    /// headers — the dedicated HAR <c>cookies</c> array is redundant info.
    /// </summary>
    private static Dictionary<string, string>? ExtractHeaders(JsonNode? headersNode, bool redactSecrets)
    {
        if (headersNode is not JsonArray headers || headers.Count == 0) return null;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            var name = header?["name"]?.GetValue<string>();
            var value = header?["value"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name) || value is null) continue;
            // Strip credential-bearing header values when redaction is on, so
            // a production HAR never lands live tokens / cookies in a recording.
            if (redactSecrets && SensitiveHeaders.Contains(name)) value = RedactedPlaceholder;
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
            var sum = TimingPhaseKeys
                .Select(key => timings[key]?.GetValue<double?>() ?? 0)
                .Where(v => v > 0)
                .Sum();
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

/// <summary>Thrown by <see cref="BowireHarConverter.Convert(string, string?, bool)"/> when the HAR document is malformed.</summary>
public sealed class BowireHarImportException : Exception
{
    public BowireHarImportException() { }
    public BowireHarImportException(string message) : base(message) { }
    public BowireHarImportException(string message, Exception inner) : base(message, inner) { }
}
