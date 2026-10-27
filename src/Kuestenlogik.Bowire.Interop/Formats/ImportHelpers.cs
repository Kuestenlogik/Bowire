// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>Small pieces the importers share.</summary>
internal static class ImportHelpers
{
    /// <summary>Escape a URL query value but leave <c>{{refs}}</c> readable for substitution.</summary>
    public static string EscapeKeepingRefs(string value)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < value.Length)
        {
            var open = value.IndexOf("{{", i, StringComparison.Ordinal);
            var close = open < 0 ? -1 : value.IndexOf("}}", open, StringComparison.Ordinal);
            if (open < 0 || close < 0)
            {
                sb.Append(Uri.EscapeDataString(value[i..]));
                break;
            }
            sb.Append(Uri.EscapeDataString(value[i..open])).Append(value, open, close + 2 - open);
            i = close + 2;
        }
        return sb.ToString();
    }

    /// <summary>Set Content-Type unless the request already has one.</summary>
    public static void SetContentType(CollectionItem item, string mime)
    {
        if (!item.Metadata.Keys.Any(k => k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            item.Metadata["Content-Type"] = mime;
    }

    /// <summary>A Basic header, or <c>null</c> when a part is a variable and cannot be encoded ahead of time.</summary>
    public static string? BasicHeader(string user, string password) =>
        user.Contains("{{", StringComparison.Ordinal) || password.Contains("{{", StringComparison.Ordinal)
            ? null
            : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));

    /// <summary>Append a query string to a URL.</summary>
    public static string AppendQuery(string url, IReadOnlyCollection<string> pairs) =>
        pairs.Count == 0 ? url : url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + string.Join("&", pairs);

    /// <summary>The object <paramref name="own"/> laid over a copy of <paramref name="baseData"/>.</summary>
    public static JsonObject Merge(JsonObject? baseData, JsonObject? own)
    {
        var merged = baseData?.DeepClone() as JsonObject ?? [];
        foreach (var (key, value) in own ?? []) merged[key] = value?.DeepClone();
        return merged;
    }

    /// <summary>What Bowire offers for an auth type the importers cannot turn into a header.</summary>
    public static string BowireEquivalent(string type) => type.ToUpperInvariant() switch
    {
        "OAUTH2" => "OAuth 2 client-credentials and authorization-code auth",
        "IAM" or "AWSV4" => "AWS SigV4 auth",
        "DIGEST" => "Digest auth",
        "NTLM" => "NTLM auth",
        _ => "no equivalent; send the header yourself",
    };
}
