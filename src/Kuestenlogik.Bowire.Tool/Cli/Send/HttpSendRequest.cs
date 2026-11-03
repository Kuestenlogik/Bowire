// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.App.Cli.Send;

/// <summary>A part of a multipart form: a text field or a file.</summary>
internal sealed record HttpFormPart(string Name, string? Value, string? FilePath);

/// <summary>
/// The request <c>bowire send http</c> sends (#678), built from the method,
/// the URL and the request items with every variable resolved. It is both
/// what goes on the wire (<see cref="ToMessage"/>) and what <c>--offline</c>
/// prints (<see cref="Render"/>), so the two can never disagree.
/// </summary>
internal sealed partial class HttpSendRequest
{
    private const string JsonMediaType = "application/json";

    // The body goes out as UTF-8, so a field keeps its umlaut rather than
    // becoming ü; what --offline prints is then what a person typed.
    private static readonly JsonSerializerOptions Utf8Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private HttpSendRequest(string method, Uri url)
    {
        Method = method;
        Url = url;
    }

    /// <summary>The method, upper case.</summary>
    public string Method { get; }

    /// <summary>The absolute URL, query items included.</summary>
    public Uri Url { get; }

    /// <summary>The headers in the order they were given; a later one of the same name replaces an earlier one.</summary>
    public List<KeyValuePair<string, string>> Headers { get; } = [];

    /// <summary>A text body (JSON or <c>--data</c>), or <c>null</c>.</summary>
    public string? Body { get; private set; }

    /// <summary>The body's media type, or <c>null</c> without a body.</summary>
    public string? ContentType { get; private set; }

    /// <summary>The form parts when a file item made the body multipart, else empty.</summary>
    public List<HttpFormPart> Form { get; } = [];

    /// <summary>The multipart boundary, set only for a form body.</summary>
    public string? Boundary { get; private set; }

    /// <summary>
    /// Split the positional tokens into method, URL and items.
    /// </summary>
    /// <remarks>
    /// The method is optional. The first token is one when it is a bare word
    /// and another token follows that is a URL rather than an item: a token
    /// with a scheme, the <c>:port</c> shorthand, <c>host:port</c>, or a token
    /// that parses as no item at all (<c>api.example.com/pets</c>). So
    /// <c>bowire send http localhost name=ada</c> posts to localhost, and
    /// <c>bowire send http propfind https://dav/x</c> sends a PROPFIND.
    /// A bare word followed by <c>Name:123</c> reads as a method and a
    /// <c>host:port</c>; a scheme or a dotted host settles it.
    /// </remarks>
    public static (string? Method, string? Url, List<string> Items) SplitPositionals(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0) return (null, null, []);
        if (tokens.Count >= 2 && MethodToken().IsMatch(tokens[0]) && LooksLikeUrl(tokens[1]))
            return (tokens[0], tokens[1], tokens.Skip(2).ToList());
        return (null, tokens[0], tokens.Skip(1).ToList());
    }

    private static bool LooksLikeUrl(string token) =>
        token.Contains("://", StringComparison.Ordinal)
        || token.StartsWith(':')
        || HostAndPort().IsMatch(token)
        || HttpRequestItem.TryParse(token) is null;

    /// <summary>
    /// Complete a URL the way HTTPie does: <c>:3000/x</c> is
    /// <c>http://localhost:3000/x</c>, and a URL without a scheme gets
    /// <paramref name="defaultScheme"/>.
    /// </summary>
    public static string CompleteUrl(string url, string defaultScheme)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.StartsWith(':'))
        {
            var rest = url[1..];
            return rest.Length == 0 || rest[0] == '/'
                ? "http://localhost" + rest
                : "http://localhost:" + rest;
        }
        return url.Contains("://", StringComparison.Ordinal) ? url : defaultScheme + "://" + url;
    }

    /// <summary>
    /// Build the request. Returns the error to print instead when an item
    /// cannot be used, so a typo never sends half a request.
    /// </summary>
    /// <param name="method">The method as typed, or <c>null</c> for the default.</param>
    /// <param name="url">The URL as typed.</param>
    /// <param name="itemTokens">The request items as typed.</param>
    /// <param name="data">A <c>--data</c> body, already read from its file; <c>null</c> without one.</param>
    /// <param name="vars">The variables for <c>{{name}}</c>.</param>
    /// <param name="defaultScheme">The scheme for a URL typed without one.</param>
    public static (HttpSendRequest? Request, string? Error) Build(
        string? method, string url, IReadOnlyList<string> itemTokens, string? data,
        IReadOnlyDictionary<string, string> vars, string defaultScheme)
    {
        ArgumentNullException.ThrowIfNull(itemTokens);
        var items = new List<HttpRequestItem>();
        foreach (var token in itemTokens)
        {
            if (HttpRequestItem.TryParse(token) is not { } item)
                return (null, $"'{token}' is not a request item. Use Header:value, param==value, field=value, field:=json or field@file.");
            // Resolved after the split, so a variable's value can never
            // turn into a separator and change what the item is.
            items.Add(item with
            {
                Name = FlowVariableResolver.Resolve(item.Name, vars),
                Value = FlowVariableResolver.Resolve(item.Value, vars),
            });
        }

        var fields = items.Where(i => i.Kind is HttpRequestItemKind.StringField or HttpRequestItemKind.JsonField).ToList();
        var files = items.Where(i => i.Kind == HttpRequestItemKind.File).ToList();
        if (data is not null && (fields.Count > 0 || files.Count > 0))
            return (null, "--data and body items (field=value, field:=json, field@file) cannot be combined.");
        if (files.Count > 0 && fields.FirstOrDefault(f => f.Kind == HttpRequestItemKind.JsonField) is { } raw)
            return (null, $"'{raw}' is raw JSON, which a multipart form cannot carry; send the file on its own or as field=value.");

        var hasBody = data is not null || fields.Count > 0 || files.Count > 0;
        var verb = string.IsNullOrWhiteSpace(method) ? (hasBody ? "POST" : "GET") : method.Trim().ToUpperInvariant();

        var resolvedUrl = CompleteUrl(FlowVariableResolver.Resolve(url, vars), defaultScheme);
        if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            return (null, $"'{resolvedUrl}' is not an http or https URL.");
        target = AppendQuery(target, items.Where(i => i.Kind == HttpRequestItemKind.Query));

        var request = new HttpSendRequest(verb, target);
        foreach (var header in items.Where(i => i.Kind == HttpRequestItemKind.Header))
        {
            request.Headers.RemoveAll(h => string.Equals(h.Key, header.Name, StringComparison.OrdinalIgnoreCase));
            request.Headers.Add(new(header.Name, header.Value));
        }

        if (files.Count > 0)
        {
            foreach (var item in items)
            {
                if (item.Kind == HttpRequestItemKind.StringField) request.Form.Add(new(item.Name, item.Value, null));
                else if (item.Kind == HttpRequestItemKind.File) request.Form.Add(new(item.Name, null, item.Value));
            }
            var missing = request.Form.FirstOrDefault(p => p.FilePath is not null && !File.Exists(p.FilePath));
            if (missing is not null) return (null, $"File not found: {missing.FilePath}");
            request.Boundary = "bowire-" + Guid.NewGuid().ToString("N");
            request.ContentType = "multipart/form-data; boundary=" + request.Boundary;
        }
        else if (fields.Count > 0)
        {
            var body = new JsonObject();
            foreach (var field in fields)
            {
                if (field.Kind == HttpRequestItemKind.StringField)
                {
                    body[field.Name] = field.Value;
                    continue;
                }
                try
                {
                    body[field.Name] = JsonNode.Parse(field.Value);
                }
                catch (JsonException ex)
                {
                    return (null, $"'{field}' is not valid JSON: {ex.Message}");
                }
            }
            request.Body = body.ToJsonString(Utf8Json);
            request.ContentType = JsonMediaType;
            // What HTTPie asks for with a JSON body: JSON first, anything else after.
            request.DefaultHeader("Accept", "application/json, */*;q=0.5");
        }
        else if (data is not null)
        {
            request.Body = FlowVariableResolver.Resolve(data, vars);
            request.ContentType = JsonMediaType;
        }

        // A Content-Type item names the body's type; the body keeps it.
        var explicitType = request.Headers.FindIndex(h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase));
        if (explicitType >= 0)
        {
            if (hasBody && files.Count == 0) request.ContentType = request.Headers[explicitType].Value;
            if (hasBody) request.Headers.RemoveAt(explicitType);
        }
        return (request, null);
    }

    /// <summary>Add a header unless one of that name was given.</summary>
    public void DefaultHeader(string name, string value)
    {
        if (!Headers.Exists(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)))
            Headers.Add(new(name, value));
    }

    private static Uri AppendQuery(Uri target, IEnumerable<HttpRequestItem> query)
    {
        var pairs = query.Select(q => Uri.EscapeDataString(q.Name) + "=" + Uri.EscapeDataString(q.Value)).ToList();
        if (pairs.Count == 0) return target;
        var builder = new UriBuilder(target);
        var existing = builder.Query.TrimStart('?');
        builder.Query = (existing.Length > 0 ? existing + "&" : string.Empty) + string.Join('&', pairs);
        return builder.Uri;
    }

    /// <summary>The message to send. The caller owns it.</summary>
    public HttpRequestMessage ToMessage()
    {
        var message = new HttpRequestMessage(new HttpMethod(Method), Url);
        try
        {
            foreach (var (name, value) in Headers)
                message.Headers.TryAddWithoutValidation(name, value);
            message.Content = CreateContent();
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private HttpContent? CreateContent()
    {
        if (Boundary is not null)
        {
            var form = new MultipartFormDataContent(Boundary);
            try
            {
                foreach (var part in Form) AddPart(form, part);
                return form;
            }
            catch
            {
                form.Dispose();
                throw;
            }
        }
        if (Body is null) return null;
        var content = new StringContent(Body, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType ?? JsonMediaType);
        return content;
    }

    // CA2000 cannot see that the form owns a part once it is added (the
    // same case as GraphQLMultipartRequest.Build); the form is disposed with
    // the request message, and on failure by CreateContent.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Parts are owned and disposed by the MultipartFormDataContent they are added to.")]
    private static void AddPart(MultipartFormDataContent form, HttpFormPart part)
    {
        if (part.FilePath is null)
        {
            form.Add(new StringContent(part.Value ?? string.Empty, Encoding.UTF8), part.Name);
            return;
        }
        var file = new StreamContent(File.OpenRead(part.FilePath));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, part.Name, Path.GetFileName(part.FilePath));
    }

    /// <summary>
    /// The request as an HTTP/1.1 message, for <c>--offline</c>. A file part
    /// shows its name and size, not its bytes, so a binary upload does not
    /// fill the terminal.
    /// </summary>
    public string Render()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{Method} {Url.PathAndQuery} HTTP/1.1\n");
        text.Append(CultureInfo.InvariantCulture, $"Host: {Url.Authority}\n");
        foreach (var (name, value) in Headers)
            text.Append(CultureInfo.InvariantCulture, $"{name}: {value}\n");
        if (ContentType is not null)
            text.Append(CultureInfo.InvariantCulture, $"Content-Type: {ContentType}\n");
        text.Append('\n');
        if (Boundary is not null)
        {
            foreach (var part in Form)
            {
                text.Append(CultureInfo.InvariantCulture, $"--{Boundary}\n");
                if (part.FilePath is null)
                {
                    text.Append(CultureInfo.InvariantCulture, $"Content-Disposition: form-data; name=\"{part.Name}\"\n\n{part.Value}\n");
                    continue;
                }
                var size = new FileInfo(part.FilePath).Length;
                text.Append(CultureInfo.InvariantCulture,
                    $"Content-Disposition: form-data; name=\"{part.Name}\"; filename=\"{Path.GetFileName(part.FilePath)}\"\n");
                text.Append("Content-Type: application/octet-stream\n\n");
                text.Append(CultureInfo.InvariantCulture, $"[{size} bytes from {part.FilePath}]\n");
            }
            text.Append(CultureInfo.InvariantCulture, $"--{Boundary}--\n");
        }
        else if (Body is not null)
        {
            text.Append(Body);
            if (!Body.EndsWith('\n')) text.Append('\n');
        }
        return text.ToString();
    }

    // RFC 9110 token characters a person types as a method: letters, and the
    // dash some extension methods carry (VERSION-CONTROL).
    [GeneratedRegex("^[A-Za-z][A-Za-z-]*$")]
    private static partial Regex MethodToken();

    [GeneratedRegex(@"^[^\s/:=@]+:\d+(/|\?|$)")]
    private static partial Regex HostAndPort();
}
