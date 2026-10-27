// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Google.Protobuf.Collections;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>
/// Bowire collections as a .http file (#673): <c>###</c> between requests,
/// <c># @name</c> from the item name, headers and body in RFC 9112 shape,
/// <c>{{variables}}</c> kept as references. Environments become
/// <c>http-client.env.json</c>; secret references become names the user fills
/// into <c>http-client.private.env.json</c> — no secret value is ever written.
/// </summary>
internal static class HttpFileExporter
{
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    public static (string Http, string EnvJson) Write(
        RepeatedField<Collection> collections, RepeatedField<V1.Environment> environments,
        HttpDialect dialect, ReportBuilder report)
    {
        var sb = new StringBuilder();
        var privateNames = new SortedSet<string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var written = 0;

        foreach (var collection in collections)
        {
            foreach (var item in collection.Items)
            {
                var subject = ItemName(item);
                if (!TryTarget(item, out var method, out var url, out var reason))
                {
                    report.Skipped(subject, reason);
                    continue;
                }

                var unmapped = new List<string>();
                if (written > 0) sb.Append('\n');
                sb.Append("### ").Append(subject).Append('\n');
                sb.Append("# @name ").Append(UniqueName(subject, usedNames)).Append('\n');
                sb.Append(method).Append(' ').Append(HttpVariables.FromBowire(url, dialect, privateNames, unmapped)).Append('\n');

                foreach (var (key, value) in item.Metadata.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (BowireMetadataKeys.IsInternal(key)) continue;
                    sb.Append(key).Append(": ").Append(HttpVariables.FromBowire(value, dialect, privateNames, unmapped)).Append('\n');
                }

                var body = item.Messages.FirstOrDefault() ?? "";
                if (HasBody(method, body))
                {
                    if (!item.Metadata.Keys.Any(k => k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) && LooksLikeJson(body))
                        sb.Append("Content-Type: application/json\n");
                    sb.Append('\n').Append(HttpVariables.FromBowire(body.TrimEnd(), dialect, privateNames, unmapped)).Append('\n');
                }
                if (item.Messages.Count > 1)
                    report.Approximated(subject, $"only the first of {item.Messages.Count} messages is written; .http sends one body per request");
                foreach (var name in unmapped.Distinct(StringComparer.Ordinal))
                    report.Approximated(subject, $"{{{{{name}}}}} has no .http counterpart without a response-handler script and stays as written");
                if (url.Contains("${", StringComparison.Ordinal) || body.Contains("${", StringComparison.Ordinal))
                    report.Approximated(subject, "a ${...} expression (e.g. ${now+3600}) has no .http counterpart and stays as written");

                report.Mapped(subject);
                written++;
            }
        }

        if (privateNames.Count > 0)
        {
            var header = new StringBuilder();
            header.Append("# Secrets are not exported. Define these in http-client.private.env.json:\n");
            foreach (var name in privateNames) header.Append("#   ").Append(name).Append('\n');
            header.Append('\n');
            sb.Insert(0, header.ToString());
        }

        return (sb.ToString(), WriteEnvironments(environments, privateNames, report));
    }

    private static string WriteEnvironments(RepeatedField<V1.Environment> environments, SortedSet<string> privateNames, ReportBuilder report)
    {
        if (environments.Count == 0) return "";
        var root = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var env in environments)
        {
            var name = string.IsNullOrWhiteSpace(env.Name) ? "default" : env.Name;
            var vars = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var unmapped = new List<string>();
            foreach (var (key, value) in env.Vars)
                vars[key] = HttpVariables.FromBowire(value, HttpDialect.JetBrains, privateNames, unmapped);
            root[name] = vars;
            if (env.Auth.Count > 0 && env.Auth.TryGetValue("type", out var type) && type != "none")
                report.Skipped(name, $"environment auth ({type}): .http has no environment-level auth; add an Authorization header to the requests");
            report.Mapped(name, "environment");
        }
        return JsonSerializer.Serialize(root, s_indented) + "\n";
    }

    /// <summary>The verb and URL of an item, or why it cannot be a .http request.</summary>
    private static bool TryTarget(CollectionItem item, out string method, out string url, out string reason)
    {
        method = url = reason = "";
        var protocol = string.IsNullOrEmpty(item.Protocol) ? "rest" : item.Protocol;
        if (!protocol.Equals("rest", StringComparison.OrdinalIgnoreCase) && !protocol.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"{protocol} is not a plain HTTP request; .http files run REST";
            return false;
        }
        if (!string.IsNullOrEmpty(item.MethodType) && !item.MethodType.Equals("Unary", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"{item.MethodType} method; .http runs one request and one response";
            return false;
        }
        if (IsAbsolute(item.Service))
        {
            // An ad-hoc or imported REST request: the URL is the service, the verb the method.
            method = string.IsNullOrEmpty(item.Method) ? "GET" : item.Method.ToUpperInvariant();
            url = item.Service;
            return true;
        }
        if (!string.IsNullOrEmpty(item.HttpPath))
        {
            method = string.IsNullOrEmpty(item.HttpMethod) ? "GET" : item.HttpMethod.ToUpperInvariant();
            url = (item.ServerUrl ?? "").TrimEnd('/') + "/" + item.HttpPath.TrimStart('/');
            return true;
        }
        reason = "discovered method without its HTTP verb and path; export it from the workbench, which fills them in from the schema";
        return false;
    }

    private static bool IsAbsolute(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("{{", StringComparison.Ordinal) || value.StartsWith("${", StringComparison.Ordinal);

    private static bool HasBody(string method, string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0) return false;
        // Bowire stores "{}" for a request without a body.
        if (trimmed == "{}" && (method is "GET" or "HEAD" or "DELETE" or "OPTIONS")) return false;
        return true;
    }

    private static bool LooksLikeJson(string body)
    {
        var t = body.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }

    private static string ItemName(CollectionItem item)
    {
        var name = !string.IsNullOrWhiteSpace(item.Name) ? item.Name.Trim() : $"{item.Method} {item.Service}".Trim();
        return name.Replace('\n', ' ');
    }

    // `# @name` must be one token and unique within the file: it is how a
    // response handler refers to the request.
    private static string UniqueName(string subject, HashSet<string> used)
    {
        var sb = new StringBuilder();
        foreach (var c in subject)
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var baseName = sb.ToString().Trim('-');
        if (baseName.Length == 0) baseName = "request";
        var candidate = baseName;
        for (var n = 2; !used.Add(candidate); n++) candidate = baseName + "-" + n;
        return candidate;
    }
}
