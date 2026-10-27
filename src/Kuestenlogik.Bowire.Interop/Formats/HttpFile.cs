// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>One request of a .http file.</summary>
internal sealed class HttpFileRequest
{
    /// <summary>The title after <c>###</c>: what the IDE shows; null when it has none.</summary>
    public string? Title { get; set; }

    /// <summary>The <c># @name</c> identifier other requests refer to; null when it has none.</summary>
    public string? Name { get; set; }

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = "";

    public List<KeyValuePair<string, string>> Headers { get; } = [];

    public string Body { get; set; } = "";

    /// <summary><c># @directive</c> lines other than <c>@name</c>, e.g. <c>no-redirect</c>.</summary>
    public List<string> Directives { get; } = [];

    /// <summary>Lines the request model has no place for: response handlers, file bodies, output redirects.</summary>
    public List<string> Unsupported { get; } = [];

    /// <summary>The 1-based line the request line sits on, for messages.</summary>
    public int Line { get; set; }
}

/// <summary>A parsed .http file: its in-file variables and its requests.</summary>
internal sealed class HttpFileDocument
{
    public List<KeyValuePair<string, string>> Variables { get; } = [];

    public List<HttpFileRequest> Requests { get; } = [];
}

/// <summary>
/// The JetBrains HTTP Client dialect of .http files (also read by Visual Studio
/// 2022; VS Code's REST Client reads the common core): <c>###</c> separators,
/// <c># @name</c>, <c>@var = value</c>, a request line, headers, a blank line,
/// a body.
/// </summary>
internal static partial class HttpFile
{
    /// <summary>The verbs a request line may start with.</summary>
    public static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT",
        // JetBrains request kinds beyond plain HTTP.
        "GRAPHQL", "WEBSOCKET", "GRPC",
    };

    public static HttpFileDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var doc = new HttpFileDocument();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        HttpFileRequest? current = null;
        string? pendingTitle = null;
        string? pendingName = null;
        var pendingDirectives = new List<string>();
        var inHeaders = false;
        var body = new StringBuilder();

        void Finish()
        {
            if (current is null) return;
            current.Body = body.ToString().TrimEnd('\n', ' ', '\t');
            doc.Requests.Add(current);
            current = null;
            body.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("###", StringComparison.Ordinal))
            {
                Finish();
                var title = trimmed[3..].Trim();
                pendingTitle = title.Length > 0 ? title : null;
                pendingName = null;
                pendingDirectives.Clear();
                continue;
            }

            if (current is null)
            {
                // Between requests: comments, directives, file variables, a request line.
                if (trimmed.Length == 0) continue;
                if (IsComment(trimmed, out var comment))
                {
                    if (comment.StartsWith('@'))
                    {
                        var directive = comment[1..].Trim();
                        if (directive.StartsWith("name ", StringComparison.OrdinalIgnoreCase) || directive.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                            pendingName = directive[4..].TrimStart(' ', '=').Trim();
                        else if (directive.Length > 0)
                            pendingDirectives.Add(directive);
                    }
                    continue;
                }
                if (FileVariable().Match(trimmed) is { Success: true } variable)
                {
                    doc.Variables.Add(new(variable.Groups[1].Value, variable.Groups[2].Value.Trim()));
                    continue;
                }

                current = ParseRequestLine(trimmed, i + 1);
                current.Title = pendingTitle;
                current.Name = pendingName;
                pendingTitle = null;
                current.Directives.AddRange(pendingDirectives);
                pendingName = null;
                pendingDirectives.Clear();
                inHeaders = true;
                // Query continuation lines: an indented line starting with ? or &.
                while (i + 1 < lines.Length && lines[i + 1].Length > 0 && char.IsWhiteSpace(lines[i + 1][0])
                       && lines[i + 1].TrimStart() is var next && (next.StartsWith('?') || next.StartsWith('&')))
                {
                    current.Url += next.Trim();
                    i++;
                }
                continue;
            }

            if (inHeaders)
            {
                if (trimmed.Length == 0)
                {
                    inHeaders = false;
                    continue;
                }
                if (IsComment(trimmed, out _)) continue;
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && HeaderName().IsMatch(line[..colon].Trim()))
                {
                    current.Headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
                    continue;
                }
                // No blank line before the body: the body starts here.
                inHeaders = false;
            }

            // Body. Handler and redirect lines end it; a file reference replaces it.
            if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">" || trimmed.StartsWith(">>", StringComparison.Ordinal)
                || trimmed.StartsWith("<>", StringComparison.Ordinal))
            {
                current.Unsupported.Add(trimmed);
                // A multi-line handler `> {% ... %}` runs to its closing `%}`.
                if (trimmed.Contains("{%", StringComparison.Ordinal) && !trimmed.Contains("%}", StringComparison.Ordinal))
                {
                    while (i + 1 < lines.Length && !lines[i + 1].Contains("%}", StringComparison.Ordinal)) i++;
                    i++;
                }
                continue;
            }
            if (trimmed.StartsWith("< ", StringComparison.Ordinal))
            {
                current.Unsupported.Add(trimmed);
                continue;
            }
            body.Append(line).Append('\n');
        }
        Finish();
        return doc;
    }

    /// <summary>Whether <paramref name="text"/> looks like a .http file: a request line before anything else that would rule it out.</summary>
    public static bool LooksLikeHttp(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Take(200))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("###", StringComparison.Ordinal) || IsComment(line, out _)
                || FileVariable().IsMatch(line)) continue;
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            var first = space < 0 ? line : line[..space];
            if (Methods.Contains(first) && space > 0) return true;
            return line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static HttpFileRequest ParseRequestLine(string line, int lineNumber)
    {
        var request = new HttpFileRequest { Line = lineNumber };
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var at = 0;
        if (parts.Length > 1 && Methods.Contains(parts[0]))
        {
            request.Method = parts[0].ToUpperInvariant();
            at = 1;
        }
        // The URL is everything up to an optional trailing HTTP version.
        var end = parts.Length;
        if (end - at > 1 && parts[end - 1].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) end--;
        request.Url = string.Join(' ', parts[at..end]);
        return request;
    }

    private static bool IsComment(string trimmed, out string text)
    {
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            text = trimmed[2..].Trim();
            return true;
        }
        if (trimmed.StartsWith('#') && !trimmed.StartsWith("###", StringComparison.Ordinal))
        {
            text = trimmed[1..].Trim();
            return true;
        }
        text = "";
        return false;
    }

    // An RFC 9110 field name: what separates `Content-Type: x` from `{"a": 1}`.
    [GeneratedRegex(@"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$")]
    private static partial Regex HeaderName();

    // `@name = value` at line start: an in-file variable (JetBrains, Visual Studio, VS Code).
    [GeneratedRegex(@"^@([A-Za-z_][\w.\-]*)\s*=\s*(.*)$")]
    private static partial Regex FileVariable();
}
