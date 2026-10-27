// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>Auth as Bruno and OpenCollection spell it: a type plus its fields; "inherit" defers to the folder.</summary>
internal sealed class BrunoAuth
{
    public string Type { get; init; } = "none";
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Headers, auth and variables a collection or folder hands down to its requests.</summary>
internal sealed class BrunoDefaults
{
    public List<BruPair> Headers { get; } = [];
    public BrunoAuth? Auth { get; set; }
    public List<BruPair> Variables { get; } = [];
    public bool HasScripts { get; set; }
}

/// <summary>One request of a Bruno collection, read from a .bru file or OpenCollection YAML.</summary>
internal sealed class BrunoRequest
{
    public string Name { get; init; } = "";
    public List<string> Folders { get; init; } = [];
    public string Kind { get; init; } = "http";
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public List<BruPair> Headers { get; } = [];
    public List<BruPair> Query { get; } = [];
    public List<BruPair> PathParams { get; } = [];

    /// <summary>json, text, xml, sparql, form-urlencoded, multipart-form, graphql, file or none.</summary>
    public string BodyType { get; set; } = "none";
    public string? BodyText { get; set; }
    public List<BruPair> Form { get; } = [];
    public string? GraphQlVariables { get; set; }

    /// <summary><c>null</c> or type "inherit": the folder's auth applies.</summary>
    public BrunoAuth? Auth { get; set; }

    /// <summary>What the request carries that Bowire does not import, already worded for the report.</summary>
    public List<string> LeftOut { get; } = [];
}

/// <summary>An environment: variables, and the names of secret ones whose values are not in the files.</summary>
internal sealed class BrunoEnvironment
{
    public string Name { get; init; } = "";
    public List<BruPair> Variables { get; } = [];
    public List<string> SecretNames { get; } = [];

    /// <summary>The environment this one extends (OpenCollection), resolved after all are read.</summary>
    public string? Extends { get; init; }
}

/// <summary>A whole collection in the shared model.</summary>
internal sealed class BrunoCollection
{
    public string Name { get; set; } = "Bruno";
    public BrunoDefaults Root { get; } = new();

    /// <summary>Folder defaults by folder path ("Users/Admin").</summary>
    public Dictionary<string, BrunoDefaults> Folders { get; } = new(StringComparer.Ordinal);
    public List<BrunoRequest> Requests { get; } = [];
    public List<BrunoEnvironment> Environments { get; } = [];
}

/// <summary>
/// The Bruno collection model in Bowire terms, shared by the .bru and the
/// OpenCollection readers: requests become REST items with inherited
/// headers and auth, environments become Bowire environments.
/// </summary>
internal static partial class BrunoMapper
{
    public static void Emit(BrunoCollection source, string? name, ImportCollectionResponse response, ReportBuilder report)
    {
        var collection = new Collection { Name = name ?? source.Name };
        foreach (var request in source.Requests) ReadRequest(source, request, collection, report);
        if (collection.Items.Count == 0) throw new InteropFormatException("no HTTP request found in the collection");
        response.Collections.Add(collection);

        // Collection and folder variables have no place of their own in Bowire.
        var defaultsVars = new List<BruPair>(source.Root.Variables.Where(v => !v.Disabled));
        foreach (var folder in source.Folders.Values) defaultsVars.AddRange(folder.Variables.Where(v => !v.Disabled));
        if (source.Root.HasScripts || source.Folders.Values.Any(f => f.HasScripts))
            report.Skipped(collection.Name, "collection or folder scripts: Bowire scripts use their own ctx.* API; port them by hand");

        foreach (var env in source.Environments)
        {
            var target = new V1.Environment { Name = env.Name };
            var unmapped = new List<string>();
            foreach (var v in env.Variables.Where(v => !v.Disabled)) target.Vars[v.Name] = BrunoVariables.ToBowire(v.Value, unmapped);
            foreach (var v in defaultsVars) target.Vars.TryAdd(v.Name, BrunoVariables.ToBowire(v.Value, unmapped));
            foreach (var secret in env.SecretNames)
            {
                target.Vars.TryAdd(secret, "");
                report.Approximated(env.Name, $"secret variable {secret}: its value is not in the collection files; fill it in, or move it to a Bowire secret ({{{{secret.{secret}}}}})");
            }
            foreach (var u in unmapped.Distinct(StringComparer.Ordinal)) report.Approximated(env.Name, $"{u} has no Bowire equivalent and stays as written");
            response.Environments.Add(target);
            report.Mapped(env.Name, "environment");
        }
        if (defaultsVars.Count > 0)
        {
            if (source.Environments.Count == 0)
            {
                var target = new V1.Environment { Name = collection.Name + " (collection variables)" };
                var unmapped = new List<string>();
                foreach (var v in defaultsVars) target.Vars.TryAdd(v.Name, BrunoVariables.ToBowire(v.Value, unmapped));
                response.Environments.Add(target);
            }
            report.Approximated(collection.Name, "collection and folder variables were merged into the environments: Bowire has no per-folder variables");
        }
    }

    private static void ReadRequest(BrunoCollection source, BrunoRequest request, Collection collection, ReportBuilder report)
    {
        var subject = request.Folders.Count > 0 ? string.Join(" / ", request.Folders) + " / " + request.Name : request.Name;
        switch (request.Kind)
        {
            case "grpc":
                report.Skipped(subject, "gRPC request: Bowire discovers gRPC services from the server or a .proto source; add that instead");
                return;
            case "websocket" or "ws":
                report.Skipped(subject, "WebSocket request: Bowire discovers WebSocket endpoints from the server; add that instead");
                return;
        }

        var unmapped = new List<string>();
        var url = BrunoVariables.ToBowire(request.Url, unmapped);
        foreach (var p in request.PathParams.Where(p => !p.Disabled))
        {
            url = PathParam(p.Name).Replace(url, BrunoVariables.ToBowire(p.Value, unmapped));
        }
        // Bruno keeps the enabled query parameters in the URL as well; add them only when it does not.
        if (!url.Contains('?', StringComparison.Ordinal))
        {
            url = ImportHelpers.AppendQuery(url, request.Query.Where(q => !q.Disabled)
                .Select(q => Uri.EscapeDataString(q.Name) + "=" + ImportHelpers.EscapeKeepingRefs(BrunoVariables.ToBowire(q.Value, unmapped))).ToList());
        }
        foreach (var q in request.Query.Where(q => q.Disabled)) report.Skipped(subject, $"disabled query parameter {q.Name}");

        var item = new CollectionItem
        {
            Name = request.Name,
            Protocol = "rest",
            Service = url,
            Method = request.Method.ToUpperInvariant(),
            MethodType = "Unary",
            ServerUrl = url,
        };
        item.Folders.Add(request.Folders);

        // Collection headers, then each folder's, then the request's own: the nearest wins.
        var chain = Chain(source, request.Folders).ToList();
        foreach (var defaults in chain)
        {
            foreach (var h in defaults.Headers.Where(h => !h.Disabled)) item.Metadata[h.Name] = BrunoVariables.ToBowire(h.Value, unmapped);
        }
        foreach (var h in request.Headers)
        {
            if (h.Disabled) report.Skipped(subject, $"disabled header {h.Name}");
            else item.Metadata[h.Name] = BrunoVariables.ToBowire(h.Value, unmapped);
        }

        var changed = ReadBody(request, item, subject, unmapped, report);
        var auth = request.Auth is { Type: not "inherit" } own ? own
            : chain.AsEnumerable().Reverse().Select(d => d.Auth).FirstOrDefault(a => a is { Type: not "inherit" });
        ReadAuth(auth, item, subject, unmapped, report);
        report.CheckHeaders(subject, item.Metadata);

        foreach (var reason in request.LeftOut) report.Skipped(subject, reason);
        foreach (var u in unmapped.Distinct(StringComparer.Ordinal)) report.Approximated(subject, $"{u} has no Bowire equivalent and stays as written");
        if (!changed) report.Mapped(subject);
        collection.Items.Add(item);
    }

    // The defaults that apply to a request, outermost first.
    private static IEnumerable<BrunoDefaults> Chain(BrunoCollection source, List<string> folders)
    {
        yield return source.Root;
        for (var depth = 1; depth <= folders.Count; depth++)
        {
            if (source.Folders.TryGetValue(string.Join("/", folders.Take(depth)), out var folder)) yield return folder;
        }
    }

    // Returns true when the body changed shape (already reported).
    private static bool ReadBody(BrunoRequest request, CollectionItem item, string subject, List<string> unmapped, ReportBuilder report)
    {
        switch (request.BodyType)
        {
            case "json":
                Raw("application/json");
                return false;
            case "text":
                Raw("text/plain");
                return false;
            case "xml":
                Raw("application/xml");
                return false;
            case "sparql":
                Raw("application/sparql-query");
                return false;
            case "form-urlencoded":
            {
                var pairs = request.Form.Where(f => !f.Disabled)
                    .Select(f => Uri.EscapeDataString(f.Name) + "=" + ImportHelpers.EscapeKeepingRefs(BrunoVariables.ToBowire(f.Value, unmapped)));
                item.Messages.Add(string.Join("&", pairs));
                ImportHelpers.SetContentType(item, "application/x-www-form-urlencoded");
                report.Approximated(subject, "form fields were joined into a urlencoded body");
                return true;
            }
            case "graphql":
            {
                var body = new Dictionary<string, object?> { ["query"] = BrunoVariables.ToBowire(request.BodyText ?? "", unmapped) };
                if (!string.IsNullOrWhiteSpace(request.GraphQlVariables))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(BrunoVariables.ToBowire(request.GraphQlVariables, unmapped));
                        body["variables"] = doc.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        report.Approximated(subject, "GraphQL variables are not plain JSON (a {{variable}} outside quotes?) and were left out");
                    }
                }
                item.Messages.Add(JsonSerializer.Serialize(body));
                ImportHelpers.SetContentType(item, "application/json");
                report.Approximated(subject, "GraphQL request became a REST POST with a {\"query\": …} body; for schema-aware calls, add the endpoint as a GraphQL source");
                return true;
            }
            case "multipart-form":
                item.Messages.Add("{}");
                report.Skipped(subject, "multipart form body: not imported; rebuild it in Bowire's form editor");
                return true;
            case "file":
                item.Messages.Add("{}");
                report.Skipped(subject, "body from a file: the file is not part of the import");
                return true;
            default:
                item.Messages.Add("{}");
                return false;
        }

        void Raw(string mime)
        {
            item.Messages.Add(string.IsNullOrEmpty(request.BodyText) ? "{}" : BrunoVariables.ToBowire(request.BodyText, unmapped));
            ImportHelpers.SetContentType(item, mime);
        }
    }

    // Bruno keeps auth per request and folder; Bowire per environment. What
    // fits in a header comes across as one; the rest is named in the report.
    private static void ReadAuth(BrunoAuth? auth, CollectionItem item, string subject, List<string> unmapped, ReportBuilder report)
    {
        if (auth is null || auth.Type is "none" or "inherit" or "") return;
        string Field(string key) => BrunoVariables.ToBowire(auth.Fields.GetValueOrDefault(key) ?? "", unmapped);

        switch (auth.Type)
        {
            case "bearer":
                item.Metadata.TryAdd("Authorization", "Bearer " + Field("token"));
                break;
            case "basic":
                var basic = ImportHelpers.BasicHeader(Field("username"), Field("password"));
                if (basic is null) report.Approximated(subject, "Basic auth with variables cannot be encoded ahead of time; set Basic auth on the Bowire environment instead");
                else item.Metadata.TryAdd("Authorization", basic);
                break;
            case "apikey":
                var key = Field("key");
                var value = Field("value");
                if ((auth.Fields.GetValueOrDefault("placement") ?? "header").StartsWith("query", StringComparison.OrdinalIgnoreCase))
                {
                    item.Service = ImportHelpers.AppendQuery(item.Service, [Uri.EscapeDataString(key) + "=" + ImportHelpers.EscapeKeepingRefs(value)]);
                    item.ServerUrl = item.Service;
                    if (!ReportBuilder.IsReference(value)) report.Credential(subject, $"API key in query parameter {key}");
                }
                else
                {
                    item.Metadata.TryAdd(key, value);
                    if (!ReportBuilder.IsReference(value)) report.Credential(subject, $"API key header {key}");
                }
                break;
            default:
                report.Skipped(subject, $"{auth.Type} auth: configure it on the Bowire environment (Bowire has {ImportHelpers.BowireEquivalent(auth.Type)})");
                break;
        }
    }

    // `:id` as a whole path segment.
    private static Regex PathParam(string name) => new("(?<=/):" + Regex.Escape(name) + @"(?=/|\?|#|$)", RegexOptions.CultureInvariant);
}

/// <summary>Bruno's dynamic variables in Bowire's syntax; everything else is the same <c>{{name}}</c>.</summary>
internal static partial class BrunoVariables
{
    private static readonly Dictionary<string, string> s_dynamic = new(StringComparer.Ordinal)
    {
        ["$randomUUID"] = "uuid",
        ["$guid"] = "uuid",
        ["$timestamp"] = "now",
        ["$isoTimestamp"] = "timestamp",
        ["$randomInt"] = "random",
    };

    public static string ToBowire(string text, ICollection<string> unmapped)
    {
        if (!text.Contains("{{", StringComparison.Ordinal)) return text;
        return Reference().Replace(text, m =>
        {
            var name = m.Groups[1].Value.Trim();
            if (s_dynamic.TryGetValue(name, out var bowire)) return "{{" + bowire + "}}";
            if (name.StartsWith('$') || name.StartsWith("process.env.", StringComparison.Ordinal)) unmapped.Add("{{" + name + "}}");
            return "{{" + name + "}}";
        });
    }

    [GeneratedRegex(@"\{\{([^{}]+)\}\}")]
    private static partial Regex Reference();
}
