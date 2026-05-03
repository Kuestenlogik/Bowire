// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.Rest;

/// <summary>
/// Sends an HTTP request derived from a <see cref="BowireMethodInfo"/> with
/// HTTP verb / path metadata plus a flat JSON value object holding all the
/// form values. Path / query / header / body parameters are split based on
/// each field's <see cref="BowireFieldInfo.Source"/> annotation.
///
/// Lives in the REST plugin (not core) so users who only want gRPC don't pull
/// in any HTTP invocation infrastructure. Exposed to core via
/// <see cref="IInlineHttpInvoker"/> implemented by <see cref="BowireRestProtocol"/>.
/// </summary>
internal static class RestInvoker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    /// <summary>
    /// Magic metadata key the JS aws_sigv4 auth helper uses to ship its
    /// credentials JSON to the invoker. Stripped from the request headers
    /// before the actual HTTP call so the marker never reaches the wire.
    /// </summary>
    internal const string AwsSigV4MarkerKey = "__bowireAwsSigV4__";

    /// <summary>
    /// Decoded AWS Sig v4 credentials carried inline in the metadata dict.
    /// </summary>
    internal sealed record AwsSigV4Config(
        string AccessKey,
        string SecretKey,
        string Region,
        string Service,
        string? SessionToken)
    {
        public static AwsSigV4Config? TryParse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                string? Get(string name) =>
                    root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                        ? p.GetString()
                        : null;

                var ak = Get("accessKey");
                var sk = Get("secretKey");
                var rg = Get("region");
                var sv = Get("service");
                if (string.IsNullOrEmpty(ak) || string.IsNullOrEmpty(sk) ||
                    string.IsNullOrEmpty(rg) || string.IsNullOrEmpty(sv))
                {
                    return null;
                }

                return new AwsSigV4Config(ak, sk, rg, sv, Get("sessionToken"));
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public static async Task<InvokeResult> InvokeAsync(
        HttpClient http,
        string serverUrl,
        BowireMethodInfo methodInfo,
        List<string> jsonMessages,
        Dictionary<string, string>? requestMetadata,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(methodInfo.HttpMethod) || string.IsNullOrEmpty(methodInfo.HttpPath))
        {
            return new InvokeResult(
                Response: null,
                DurationMs: 0,
                Status: "Error",
                Metadata: new Dictionary<string, string> { ["error"] = "Method has no HTTP annotation" });
        }

        // Parse the user's flat input object — fields keyed by their REST name
        Dictionary<string, JsonElement> values;
        try
        {
            var raw = jsonMessages.Count > 0 ? jsonMessages[0] : "{}";
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            values = ExtractTopLevel(doc.RootElement);
        }
        catch (JsonException ex)
        {
            return new InvokeResult(
                Response: null,
                DurationMs: 0,
                Status: "Error",
                Metadata: new Dictionary<string, string> { ["error"] = "Invalid request body JSON: " + ex.Message });
        }

        // Bucket the input fields by their declared source
        var pathValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var queryValues = new List<KeyValuePair<string, string>>();
        var headerValues = new List<KeyValuePair<string, string>>();
        var bodyObject = new Dictionary<string, object?>();
        var bodyHasFields = false;
        var formdataParts = new List<MultipartPart>();

        foreach (var field in methodInfo.InputType.Fields)
        {
            if (!values.TryGetValue(field.Name, out var element)) continue;
            if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined) continue;

            switch (field.Source)
            {
                case "path":
                    pathValues[field.Name] = ElementToString(element);
                    break;
                case "query":
                    AppendQueryValue(queryValues, field.Name, element);
                    break;
                case "header":
                    headerValues.Add(new KeyValuePair<string, string>(field.Name, ElementToString(element)));
                    break;
                case "formdata":
                    formdataParts.Add(new MultipartPart(field, element));
                    break;
                case "body":
                default:
                    bodyObject[field.Name] = ElementToObject(element);
                    bodyHasFields = true;
                    break;
            }
        }

        // Build the resolved URL: substitute path placeholders, append query string
        string resolvedPath;
        try
        {
            resolvedPath = ApplyPathParams(methodInfo.HttpPath, pathValues);
        }
        catch (ArgumentException ex)
        {
            return new InvokeResult(
                Response: null,
                DurationMs: 0,
                Status: "Error",
                Metadata: new Dictionary<string, string> { ["error"] = ex.Message });
        }

        var fullUrl = CombineUrl(serverUrl, resolvedPath);
        if (queryValues.Count > 0)
        {
            fullUrl += (fullUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?')
                + string.Join('&', queryValues.Select(kv =>
                    Uri.EscapeDataString(kv.Key) + '=' + Uri.EscapeDataString(kv.Value)));
        }

        if (!Uri.TryCreate(fullUrl, UriKind.Absolute, out var requestUri))
        {
            return new InvokeResult(
                Response: null,
                DurationMs: 0,
                Status: "Error",
                Metadata: new Dictionary<string, string> { ["error"] = "Could not build request URL: " + fullUrl });
        }

        // Build the request — methodInfo.HttpMethod is required for the
        // REST invocation path. Discovery always sets it for REST methods
        // and the gRPC transcoding bridge sets it before calling us.
        var httpMethod = new HttpMethod(methodInfo.HttpMethod
            ?? throw new InvalidOperationException(
                $"Method '{methodInfo.FullName}' has no HttpMethod — cannot invoke as HTTP."));
        using var request = new HttpRequestMessage(httpMethod, requestUri);

        // Per-method headers from the input fields
        foreach (var (k, v) in headerValues)
        {
            request.Headers.TryAddWithoutValidation(k, v);
        }

        // Caller-provided metadata (auth helpers, custom headers via the
        // Metadata tab). The aws_sigv4, mtls, and cookie-jar helpers mark
        // their configs with magic prefixes so we can pull them out before
        // forwarding the remaining entries as plain HTTP headers.
        AwsSigV4Config? awsConfig = null;
        var mtlsConfig = MtlsConfig.TryParseFromMetadata(requestMetadata);
        string? cookieEnvId = null;
        if (requestMetadata is not null)
        {
            foreach (var (k, v) in requestMetadata)
            {
                if (string.Equals(k, AwsSigV4MarkerKey, StringComparison.Ordinal))
                {
                    awsConfig = AwsSigV4Config.TryParse(v);
                    continue;
                }
                if (string.Equals(k, MtlsConfig.MtlsMarkerKey, StringComparison.Ordinal))
                {
                    continue;
                }
                if (string.Equals(k, CookieJar.MarkerKey, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(v)) cookieEnvId = v;
                    continue;
                }
                request.Headers.TryAddWithoutValidation(k, v);
            }
        }

        // Body — only attach if the verb supports it AND there's something to send.
        // multipart/form-data wins when the discovery flagged the operation as
        // form-encoded; the same fields can't be both because Source bucketed
        // them mutually-exclusively.
        if (formdataParts.Count > 0 && CanHaveBody(httpMethod))
        {
            request.Content = BuildMultipartContent(formdataParts);
        }
        else if (bodyHasFields && CanHaveBody(httpMethod))
        {
            var json = JsonSerializer.Serialize(bodyObject, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        // AWS Sig v4 signing has to happen AFTER the body is attached because
        // the signature includes the body hash. The signer adds X-Amz-Date,
        // X-Amz-Content-Sha256, optional X-Amz-Security-Token and the
        // Authorization header in place.
        if (awsConfig is not null)
        {
            try
            {
                await AwsSigV4Signer.SignAsync(
                    request,
                    awsConfig.AccessKey,
                    awsConfig.SecretKey,
                    awsConfig.SessionToken,
                    awsConfig.Region,
                    awsConfig.Service,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new InvokeResult(
                    Response: null,
                    DurationMs: 0,
                    Status: "Error",
                    Metadata: new Dictionary<string, string> { ["error"] = "AWS Sig v4 signing failed: " + ex.Message });
            }
        }

        // mTLS requires a dedicated HttpClient because the certificate is
        // attached to the handler, not the request — the shared HttpClient
        // can't carry per-call client certs. Cookie-jar mode also needs a
        // dedicated handler so the per-env CookieContainer reaches it.
        // Build one on-demand and dispose it together with the response.
        // Allocation cost is negligible compared to the TLS handshake
        // itself, and a dev tool isn't on the hot path of a production
        // server anyway.
        HttpClient? perCallClient = null;
        MtlsHandlerOwner? mtlsOwner = null;
        HttpClientHandler? cookieHandler = null;
        HttpClient effectiveClient = http;
        var sw = Stopwatch.StartNew();
        try
        {
            if (mtlsConfig is not null)
            {
                mtlsOwner = MtlsHandlerOwner.CreateHttpClientHandler(mtlsConfig, out var mtlsError);
                if (mtlsOwner is null)
                {
                    return new InvokeResult(
                        Response: null,
                        DurationMs: 0,
                        Status: "Error",
                        Metadata: new Dictionary<string, string> { ["error"] = mtlsError ?? "mTLS configuration invalid" });
                }
                // mTLS + cookie-jar can compose: attach the per-env
                // CookieContainer to the existing mTLS HttpClientHandler.
                if (cookieEnvId is not null && mtlsOwner.Handler is HttpClientHandler mtlsHandler)
                {
                    mtlsHandler.UseCookies = true;
                    mtlsHandler.CookieContainer = CookieJar.GetOrCreate(cookieEnvId);
                }
                // disposeHandler=false — the MtlsHandlerOwner owns the handler
                // (and the X509 resources alongside it) and disposes everything
                // in one place in the finally block below. CA5400 suppressed:
                // CRL checks default off (see MtlsHandlerOwner for the rationale).
#pragma warning disable CA5400
                perCallClient = new HttpClient(mtlsOwner.Handler, disposeHandler: false) { Timeout = http.Timeout };
#pragma warning restore CA5400
                effectiveClient = perCallClient;
            }
            else if (cookieEnvId is not null)
            {
                cookieHandler = new HttpClientHandler
                {
                    UseCookies = true,
                    CookieContainer = CookieJar.GetOrCreate(cookieEnvId)
                };
#pragma warning disable CA5399, CA5400
                // CRL checks intentionally off — same rationale as the
                // mTLS path. Cookie-jar mode is a dev-tool convenience.
                perCallClient = new HttpClient(cookieHandler, disposeHandler: false) { Timeout = http.Timeout };
#pragma warning restore CA5399, CA5400
                effectiveClient = perCallClient;
            }

            using var resp = await effectiveClient.SendAsync(request, ct).ConfigureAwait(false);
            sw.Stop();

            var bodyText = resp.Content is null
                ? string.Empty
                : await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Map HTTP status into the same name space the JS understands so the
            // status badge picks the right color (ok/warning/error).
            var statusName = HttpStatusToBowireStatus((int)resp.StatusCode);

            var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["http_status"] = ((int)resp.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["http_reason"] = resp.ReasonPhrase ?? string.Empty
            };
            foreach (var h in resp.Headers)
            {
                responseHeaders[h.Key] = string.Join(", ", h.Value);
            }
            if (resp.Content?.Headers is not null)
            {
                foreach (var h in resp.Content.Headers)
                {
                    responseHeaders[h.Key] = string.Join(", ", h.Value);
                }
            }

            return new InvokeResult(
                Response: string.IsNullOrEmpty(bodyText) ? null : bodyText,
                DurationMs: sw.ElapsedMilliseconds,
                Status: statusName,
                Metadata: responseHeaders);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return new InvokeResult(
                Response: null,
                DurationMs: sw.ElapsedMilliseconds,
                Status: "NetworkError",
                Metadata: new Dictionary<string, string> { ["error"] = ex.Message });
        }
        finally
        {
            perCallClient?.Dispose();
            mtlsOwner?.Dispose();
            cookieHandler?.Dispose();
        }
    }

    private static Dictionary<string, JsonElement> ExtractTopLevel(JsonElement root)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object) return dict;
        foreach (var prop in root.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.Clone();
        }
        return dict;
    }

    private static string ElementToString(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? string.Empty,
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => e.GetRawText()
        };
    }

    private static object? ElementToObject(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => e.EnumerateArray().Select(ElementToObject).ToArray(),
            JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => ElementToObject(p.Value)),
            _ => e.GetRawText()
        };
    }

    private static void AppendQueryValue(List<KeyValuePair<string, string>> bucket, string key, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                bucket.Add(new KeyValuePair<string, string>(key, ElementToString(item)));
            }
        }
        else
        {
            bucket.Add(new KeyValuePair<string, string>(key, ElementToString(element)));
        }
    }

    private static string ApplyPathParams(string template, Dictionary<string, string> values)
    {
        // Replace each {name} placeholder with its URL-encoded value. Strips
        // {name=subpath/*} sub-path constraints because Bowire matches by the
        // bare placeholder name (gRPC transcoding allows them).
        var sb = new StringBuilder(template.Length + 32);
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                var end = template.IndexOf('}', i + 1);
                if (end < 0)
                {
                    sb.Append(template, i, template.Length - i);
                    break;
                }
                var inner = template.Substring(i + 1, end - i - 1);
                var equalsIdx = inner.IndexOf('=', StringComparison.Ordinal);
                var name = equalsIdx >= 0 ? inner.Substring(0, equalsIdx) : inner;
                if (!values.TryGetValue(name, out var v))
                    throw new ArgumentException("Missing value for path parameter '" + name + "'");
                sb.Append(Uri.EscapeDataString(v));
                i = end + 1;
            }
            else
            {
                sb.Append(template[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        var trimmedBase = baseUrl.TrimEnd('/');
        var trimmedPath = path.StartsWith('/') ? path : "/" + path;
        return trimmedBase + trimmedPath;
    }

    private static bool CanHaveBody(HttpMethod method)
    {
        return method != HttpMethod.Get
            && method != HttpMethod.Head
            && method != HttpMethod.Delete
            && method != HttpMethod.Options
            && method != HttpMethod.Trace;
    }

    /// <summary>
    /// One field destined for a <c>multipart/form-data</c> request body —
    /// pairs the discovered field metadata (name, IsBinary flag) with the
    /// raw JSON value the user supplied. Binary fields ship as base64 in
    /// the JSON envelope and are decoded back into a <see cref="StreamContent"/>
    /// here; plain fields go on the wire as <see cref="StringContent"/>.
    /// </summary>
    private readonly record struct MultipartPart(BowireFieldInfo Field, JsonElement Value);

    /// <summary>
    /// Compose the request's <see cref="MultipartFormDataContent"/> from the
    /// bucketed parts. Binary fields get a stream + filename hint when one
    /// was supplied (form value shape: <c>{ "filename": "...", "data": "&lt;base64&gt;" }</c>);
    /// bare base64 strings work too — the filename stays empty.
    /// </summary>
    private static MultipartFormDataContent BuildMultipartContent(List<MultipartPart> parts)
    {
        // CA2000 suppression rationale: MultipartFormDataContent owns the
        // sub-contents passed to its Add(...) overloads — its Dispose()
        // calls Dispose() on each part. Roslyn can't see that ownership
        // transfer, so it flags every `new StringContent(...)` /
        // `new StreamContent(...)` call as a leak.
#pragma warning disable CA2000
        var content = new MultipartFormDataContent();
        foreach (var part in parts)
        {
            var name = part.Field.Name;
            if (part.Field.IsBinary)
            {
                var (filename, base64) = ExtractFilePayload(part.Value);
                byte[] bytes;
                try { bytes = Convert.FromBase64String(base64); }
                catch (FormatException) { bytes = []; }
                var streamContent = new StreamContent(new MemoryStream(bytes));
                streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                if (string.IsNullOrEmpty(filename))
                {
                    content.Add(streamContent, name);
                }
                else
                {
                    content.Add(streamContent, name, filename);
                }
            }
            else
            {
                // Repeated form fields (`tags[]=a&tags[]=b` style) ship as
                // arrays — emit one part per element so server frameworks that
                // bind the repeated shape pick them all up.
                if (part.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in part.Value.EnumerateArray())
                    {
                        content.Add(new StringContent(ElementToString(item), Encoding.UTF8), name);
                    }
                }
                else
                {
                    content.Add(new StringContent(ElementToString(part.Value), Encoding.UTF8), name);
                }
            }
        }
        return content;
#pragma warning restore CA2000
    }

    /// <summary>
    /// Pull the filename + base64 payload out of a binary form-value. The
    /// frontend file picker emits the structured shape
    /// <c>{ "filename": "photo.jpg", "data": "&lt;base64&gt;" }</c>;
    /// power users typing a bare base64 string in the JSON tab also work.
    /// </summary>
    private static (string Filename, string Base64) ExtractFilePayload(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            string? filename = null;
            string? data = null;
            if (element.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String)
                filename = fn.GetString();
            if (element.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String)
                data = d.GetString();
            return (filename ?? string.Empty, data ?? string.Empty);
        }
        if (element.ValueKind == JsonValueKind.String)
        {
            return (string.Empty, element.GetString() ?? string.Empty);
        }
        return (string.Empty, string.Empty);
    }

    /// <summary>
    /// Map an HTTP status code to a status name the existing JS status colors
    /// already understand. 2xx → OK, 3xx → OK (redirect handled by HttpClient
    /// follow), 4xx → InvalidArgument/NotFound/etc., 5xx → Unavailable/Internal.
    /// </summary>
    private static string HttpStatusToBowireStatus(int code)
    {
        if (code is >= 200 and < 300) return "OK";
        if (code == 400) return "InvalidArgument";
        if (code == 401) return "Unauthenticated";
        if (code == 403) return "PermissionDenied";
        if (code == 404) return "NotFound";
        if (code == 408) return "DeadlineExceeded";
        if (code == 409) return "AlreadyExists";
        if (code == 429) return "ResourceExhausted";
        if (code is >= 400 and < 500) return "FailedPrecondition";
        if (code == 501) return "Unimplemented";
        if (code == 503) return "Unavailable";
        if (code is >= 500) return "Internal";
        return "Unknown";
    }
}
