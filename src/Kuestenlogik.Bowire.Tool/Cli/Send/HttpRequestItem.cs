// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Kuestenlogik.Bowire.App.Cli.Send;

/// <summary>What an HTTPie-style request item contributes to the request.</summary>
internal enum HttpRequestItemKind
{
    /// <summary><c>Name:value</c>, a request header.</summary>
    Header,

    /// <summary><c>name==value</c>, a query parameter.</summary>
    Query,

    /// <summary><c>field=value</c>, a JSON (or form) field holding a string.</summary>
    StringField,

    /// <summary><c>field:=42</c>, a JSON field holding raw JSON.</summary>
    JsonField,

    /// <summary><c>field@path</c>, a file uploaded as a multipart form part.</summary>
    File,
}

/// <summary>
/// One request item of <c>bowire send http</c> (#678), in HTTPie's notation:
/// <c>Header:value</c>, <c>param==value</c>, <c>field=value</c>,
/// <c>field:=json</c> and <c>field@file</c>.
/// </summary>
/// <remarks>
/// The item's kind is decided by the first separator in it, the longest one
/// where two start at the same place (<c>:=</c> before <c>:</c>, <c>==</c>
/// before <c>=</c>). So <c>Authorization:Bearer a=b</c> is a header and
/// <c>q==a:b</c> a query parameter. A backslash before a separator character
/// keeps it in the name (<c>a\=b=c</c> is the field <c>a=b</c>); the value is
/// everything after the separator, taken as it stands.
/// </remarks>
internal sealed record HttpRequestItem(HttpRequestItemKind Kind, string Name, string Value)
{
    /// <summary>Parse <paramref name="token"/>; <c>null</c> when it is not an item.</summary>
    public static HttpRequestItem? TryParse(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var name = new StringBuilder();
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            if (c == '\\' && i + 1 < token.Length && token[i + 1] is '\\' or ':' or '=' or '@')
            {
                name.Append(token[++i]);
                continue;
            }
            var doubled = i + 1 < token.Length && token[i + 1] == '=';
            HttpRequestItemKind? kind = c switch
            {
                ':' => doubled ? HttpRequestItemKind.JsonField : HttpRequestItemKind.Header,
                '=' => doubled ? HttpRequestItemKind.Query : HttpRequestItemKind.StringField,
                '@' => HttpRequestItemKind.File,
                _ => null,
            };
            var length = doubled && c is ':' or '=' ? 2 : 1;
            if (kind is null)
            {
                name.Append(c);
                continue;
            }
            // An item names something: `=x` or `:foo` is not one, and
            // `:3000/pets` is the localhost shorthand for a URL.
            if (name.Length == 0) return null;
            return new HttpRequestItem(kind.Value, name.ToString(), token[(i + length)..]);
        }
        return null;
    }

    /// <summary>The item as it would be typed, for messages.</summary>
    public override string ToString() => Kind switch
    {
        HttpRequestItemKind.Header => $"{Name}:{Value}",
        HttpRequestItemKind.Query => $"{Name}=={Value}",
        HttpRequestItemKind.StringField => $"{Name}={Value}",
        HttpRequestItemKind.JsonField => $"{Name}:={Value}",
        _ => $"{Name}@{Value}",
    };
}
