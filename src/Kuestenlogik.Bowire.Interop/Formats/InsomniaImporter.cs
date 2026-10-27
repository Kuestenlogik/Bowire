// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>
/// Insomnia exports (#674): the v4 export (<c>__export_format: 4</c>, JSON or
/// YAML, a flat <c>resources</c> list linked by <c>parentId</c>) and the v5
/// collection file (<c>type: collection.insomnia.rest/5.0</c>, a nested
/// <c>collection</c> tree). Both are read into one tree first, so the mapping
/// to Bowire is written once.
/// </summary>
internal sealed class InsomniaImporter : IInteropImporter
{
    private const string V5CollectionType = "collection.insomnia.rest/5";

    public string Id => "insomnia";

    public bool CanRead(IReadOnlyList<SourceFile> files) => files.Any(f =>
        f.Content.Contains("__export_format", StringComparison.Ordinal)
        || f.Content.Contains(V5CollectionType, StringComparison.Ordinal)
        || f.Content.Contains("insomnia.rest/5.0", StringComparison.Ordinal));

    public ImportCollectionResponse Read(IReadOnlyList<SourceFile> files, string? name, ReportBuilder report)
    {
        var response = new ImportCollectionResponse { Format = Id };
        foreach (var file in files)
        {
            var doc = StructuredDocument.TryLoad(file.Content);
            if (doc is null) continue;
            var type = StructuredDocument.Text(doc["type"]) ?? "";
            List<Workspace> workspaces;
            if (doc["__export_format"] is not null) workspaces = ReadV4(doc, report);
            else if (type.StartsWith("collection.insomnia.rest/", StringComparison.Ordinal)) workspaces = [ReadV5(doc, report)];
            else if (type.Contains("insomnia", StringComparison.Ordinal))
                throw new InteropFormatException($"{type} is not a request collection; add a design document's spec to Bowire as a source instead");
            else continue;
            foreach (var workspace in workspaces) Emit(workspace, files.Count == 1 && workspaces.Count == 1 ? name : null, response, report);
        }
        if (response.Collections.Count == 0) throw new InteropFormatException("no request collection found in the export");
        return response;
    }

    // ---- the common tree ----

    private sealed class Folder
    {
        public string Name { get; init; } = "";
        public JsonObject? Auth { get; set; }
        public JsonObject? Variables { get; set; }
        public List<object> Children { get; } = [];
    }

    private sealed class Request
    {
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "request";
        public JsonObject Source { get; init; } = [];
    }

    private sealed class Workspace
    {
        public Folder Root { get; init; } = new();
        public List<(string Name, JsonObject? Data, bool IsPrivate)> Environments { get; } = [];
    }

    // ---- v4: a flat list linked by parentId ----

    private static List<Workspace> ReadV4(JsonObject doc, ReportBuilder report)
    {
        var resources = (doc["resources"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var byParent = resources.GroupBy(r => StructuredDocument.Text(r["parentId"]) ?? "")
            .ToDictionary(g => g.Key, g => g.OrderBy(SortKey).ToList(), StringComparer.Ordinal);
        var result = new List<Workspace>();

        foreach (var ws in resources.Where(r => Type(r) == "workspace"))
        {
            var id = StructuredDocument.Text(ws["_id"]) ?? "";
            var workspace = new Workspace { Root = new Folder { Name = StructuredDocument.Text(ws["name"]) ?? "Insomnia" } };
            if (StructuredDocument.Text(ws["scope"]) == "design")
                report.Approximated(workspace.Root.Name, "a design document: its requests are imported, its API spec is not (add the spec as a source)");
            Fill(workspace.Root, id);

            // Base environment under the workspace, sub environments under the base.
            foreach (var baseEnv in Children(id).Where(r => Type(r) == "environment"))
            {
                var baseName = StructuredDocument.Text(baseEnv["name"]) ?? "Base Environment";
                var subs = Children(StructuredDocument.Text(baseEnv["_id"]) ?? "").Where(r => Type(r) == "environment").ToList();
                if (subs.Count == 0)
                {
                    workspace.Environments.Add((baseName, baseEnv["data"] as JsonObject, StructuredDocument.IsTrue(baseEnv["isPrivate"])));
                    continue;
                }
                foreach (var sub in subs)
                {
                    workspace.Environments.Add((StructuredDocument.Text(sub["name"]) ?? "Environment",
                        Merge(baseEnv["data"] as JsonObject, sub["data"] as JsonObject), StructuredDocument.IsTrue(sub["isPrivate"])));
                }
            }
            foreach (var other in resources.Where(r => Type(r) is "cookie_jar" && StructuredDocument.Text(r["parentId"]) == id))
            {
                if ((other["cookies"] as JsonArray)?.Count > 0)
                    report.Skipped(workspace.Root.Name, "cookie jar: Bowire keeps cookies per environment and fills them from responses");
            }
            result.Add(workspace);
        }

        foreach (var unknown in resources.Select(Type).OfType<string>().Where(t => t is "api_spec" or "unit_test_suite" or "unit_test" or "proto_file" or "proto_directory" or "mock_server" or "mock_route").Distinct())
        {
            report.Skipped(unknown, unknown switch
            {
                "api_spec" => "API spec: add it to Bowire as a schema source instead",
                "unit_test_suite" or "unit_test" => "Insomnia unit tests: Bowire writes assertions on collection items and recordings",
                "proto_file" or "proto_directory" => "proto files: add them to Bowire as a schema source instead",
                _ => "mock definitions: Bowire builds mocks from recordings and schemas",
            });
        }
        return result;

        List<JsonObject> Children(string parentId) => byParent.TryGetValue(parentId, out var list) ? list : [];

        void Fill(Folder folder, string parentId)
        {
            foreach (var child in Children(parentId))
            {
                switch (Type(child))
                {
                    case "request_group":
                        var sub = new Folder
                        {
                            Name = StructuredDocument.Text(child["name"]) ?? "Folder",
                            Auth = child["authentication"] as JsonObject,
                            Variables = child["environment"] as JsonObject,
                        };
                        Fill(sub, StructuredDocument.Text(child["_id"]) ?? "");
                        folder.Children.Add(sub);
                        break;
                    case "request":
                    case "grpc_request":
                    case "websocket_request":
                        folder.Children.Add(new Request { Name = StructuredDocument.Text(child["name"]) ?? "Request", Kind = Type(child)!, Source = child });
                        break;
                }
            }
        }

        static string? Type(JsonObject r) => StructuredDocument.Text(r["_type"]);
        static double SortKey(JsonObject r) => double.TryParse(StructuredDocument.Text(r["metaSortKey"]), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var k) ? k : 0;
    }

    // ---- v5: a nested tree ----

    private static Workspace ReadV5(JsonObject doc, ReportBuilder report)
    {
        var workspace = new Workspace { Root = new Folder { Name = StructuredDocument.Text(doc["name"]) ?? "Insomnia" } };
        Fill(workspace.Root, doc["collection"] as JsonArray);

        if (doc["environments"] is JsonObject baseEnv)
        {
            var baseName = StructuredDocument.Text(baseEnv["name"]) ?? "Base Environment";
            var subs = (baseEnv["subEnvironments"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (subs.Count == 0)
                workspace.Environments.Add((baseName, baseEnv["data"] as JsonObject, StructuredDocument.IsTrue(baseEnv["isPrivate"])));
            foreach (var sub in subs)
            {
                workspace.Environments.Add((StructuredDocument.Text(sub["name"]) ?? "Environment",
                    Merge(baseEnv["data"] as JsonObject, sub["data"] as JsonObject), StructuredDocument.IsTrue(sub["isPrivate"])));
            }
        }
        if (doc["cookieJar"] is JsonObject jar && (jar["cookies"] as JsonArray)?.Count > 0)
            report.Skipped(workspace.Root.Name, "cookie jar: Bowire keeps cookies per environment and fills them from responses");
        return workspace;

        static void Fill(Folder folder, JsonArray? items)
        {
            foreach (var item in (items ?? []).OfType<JsonObject>())
            {
                var itemName = StructuredDocument.Text(item["name"]) ?? "Request";
                if (item["children"] is JsonArray children)
                {
                    var sub = new Folder { Name = itemName, Auth = item["authentication"] as JsonObject, Variables = item["environment"] as JsonObject };
                    Fill(sub, children);
                    folder.Children.Add(sub);
                    continue;
                }
                var id = StructuredDocument.Text((item["meta"] as JsonObject)?["id"]) ?? "";
                var kind = id.StartsWith("ws-req", StringComparison.Ordinal) ? "websocket_request"
                    : id.StartsWith("greq", StringComparison.Ordinal) || item["protoMethodName"] is not null ? "grpc_request"
                    : "request";
                folder.Children.Add(new Request { Name = itemName, Kind = kind, Source = item });
            }
        }
    }

    // ---- the mapping to Bowire ----

    private static void Emit(Workspace workspace, string? name, ImportCollectionResponse response, ReportBuilder report)
    {
        var collection = new Collection { Name = name ?? workspace.Root.Name };
        var folderVars = new JsonObject();
        Walk(workspace.Root, [], null);
        if (collection.Items.Count == 0)
        {
            report.Skipped(collection.Name, "no HTTP request in this workspace");
            return;
        }
        response.Collections.Add(collection);

        foreach (var (envName, data, isPrivate) in workspace.Environments)
        {
            var env = new V1.Environment { Name = envName };
            var unmapped = new List<string>();
            foreach (var (key, value) in Flatten(data)) env.Vars[key] = InsomniaTemplates.ToBowire(value, unmapped);
            foreach (var (key, value) in Flatten(folderVars)) env.Vars.TryAdd(key, InsomniaTemplates.ToBowire(value, unmapped));
            if (isPrivate)
            {
                foreach (var key in env.Vars.Keys) report.Credential(envName, $"private variable {key}");
            }
            foreach (var u in unmapped.Distinct(StringComparer.Ordinal)) report.Approximated(envName, $"{u} has no Bowire equivalent and stays as written");
            response.Environments.Add(env);
            report.Mapped(envName, "environment");
        }
        if (folderVars.Count > 0)
        {
            if (workspace.Environments.Count == 0)
            {
                var env = new V1.Environment { Name = collection.Name + " (folder variables)" };
                var unmapped = new List<string>();
                foreach (var (key, value) in Flatten(folderVars)) env.Vars[key] = InsomniaTemplates.ToBowire(value, unmapped);
                response.Environments.Add(env);
            }
            report.Approximated(collection.Name, "folder variables were merged into the environments: Bowire has no per-folder variables");
        }

        void Walk(Folder folder, List<string> path, JsonObject? inheritedAuth)
        {
            var auth = HasAuth(folder.Auth) ? folder.Auth : inheritedAuth;
            if (folder.Variables is { Count: > 0 } vars)
            {
                foreach (var (key, value) in vars) folderVars[key] ??= value?.DeepClone();
            }
            foreach (var child in folder.Children)
            {
                switch (child)
                {
                    case Folder sub:
                        Walk(sub, [.. path, sub.Name], auth);
                        break;
                    case Request request:
                        ReadRequest(request, path, auth, collection, report);
                        break;
                }
            }
        }
    }

    private static void ReadRequest(Request request, List<string> folders, JsonObject? inheritedAuth, Collection collection, ReportBuilder report)
    {
        var subject = folders.Count > 0 ? string.Join(" / ", folders) + " / " + request.Name : request.Name;
        switch (request.Kind)
        {
            case "grpc_request":
                report.Skipped(subject, "gRPC request: Bowire discovers gRPC services from the server or a .proto source; add that instead");
                return;
            case "websocket_request":
                report.Skipped(subject, "WebSocket request: Bowire discovers WebSocket endpoints from the server; add that instead");
                return;
        }

        var src = request.Source;
        var unmapped = new List<string>();
        var method = (StructuredDocument.Text(src["method"]) ?? "GET").ToUpperInvariant();
        var url = InsomniaTemplates.ToBowire(StructuredDocument.Text(src["url"]) ?? "", unmapped);

        var query = new List<string>();
        foreach (var p in (src["parameters"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var pName = StructuredDocument.Text(p["name"]) ?? "";
            if (pName.Length == 0) continue;
            if (StructuredDocument.IsTrue(p["disabled"]))
            {
                report.Skipped(subject, $"disabled query parameter {pName}");
                continue;
            }
            query.Add(Uri.EscapeDataString(pName) + "=" + EscapeKeepingRefs(InsomniaTemplates.ToBowire(StructuredDocument.Text(p["value"]) ?? "", unmapped)));
        }
        if (query.Count > 0) url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + string.Join("&", query);

        var item = new CollectionItem
        {
            Name = request.Name,
            Protocol = "rest",
            Service = url,
            Method = method,
            MethodType = "Unary",
            ServerUrl = url,
        };
        item.Folders.Add(folders);

        foreach (var h in (src["headers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var hName = StructuredDocument.Text(h["name"]) ?? "";
            if (hName.Length == 0) continue;
            if (StructuredDocument.IsTrue(h["disabled"]))
            {
                report.Skipped(subject, $"disabled header {hName}");
                continue;
            }
            item.Metadata[hName] = InsomniaTemplates.ToBowire(StructuredDocument.Text(h["value"]) ?? "", unmapped);
        }

        var approximatedBody = ReadBody(src["body"] as JsonObject, item, subject, unmapped, report);
        var auth = src["authentication"] is JsonObject own && HasAuthOrNone(own) ? own : inheritedAuth;
        ReadAuth(auth, item, subject, unmapped, report);
        report.CheckHeaders(subject, item.Metadata);

        var scripts = src["scripts"] as JsonObject;
        if (!string.IsNullOrWhiteSpace(StructuredDocument.Text(src["preRequestScript"]) ?? StructuredDocument.Text(scripts?["preRequest"])))
            report.Skipped(subject, "pre-request script: Bowire scripts use their own ctx.* API; port it by hand");
        if (!string.IsNullOrWhiteSpace(StructuredDocument.Text(src["afterResponseScript"]) ?? StructuredDocument.Text(scripts?["afterResponse"])))
            report.Skipped(subject, "after-response script: write it as Bowire assertions or a post-script");
        foreach (var u in unmapped.Distinct(StringComparer.Ordinal))
            report.Approximated(subject, $"{u} has no Bowire equivalent and stays as written");

        if (!approximatedBody) report.Mapped(subject);
        collection.Items.Add(item);
    }

    // Returns true when the body changed shape (already reported).
    private static bool ReadBody(JsonObject? body, CollectionItem item, string subject, List<string> unmapped, ReportBuilder report)
    {
        var mime = StructuredDocument.Text(body?["mimeType"]) ?? "";
        var text = StructuredDocument.Text(body?["text"]);
        if (body is null || (mime.Length == 0 && text is null))
        {
            item.Messages.Add("{}");
            return false;
        }

        if (!string.IsNullOrEmpty(StructuredDocument.Text(body["fileName"])))
        {
            item.Messages.Add("{}");
            report.Skipped(subject, "body from a file: the file is not part of the export");
            return true;
        }

        switch (mime)
        {
            case "application/x-www-form-urlencoded":
            {
                var pairs = new List<string>();
                foreach (var p in (body["params"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (StructuredDocument.IsTrue(p["disabled"])) continue;
                    pairs.Add(Uri.EscapeDataString(StructuredDocument.Text(p["name"]) ?? "") + "="
                        + EscapeKeepingRefs(InsomniaTemplates.ToBowire(StructuredDocument.Text(p["value"]) ?? "", unmapped)));
                }
                item.Messages.Add(string.Join("&", pairs));
                SetContentType(item, mime);
                report.Approximated(subject, "form fields were joined into a urlencoded body");
                return true;
            }
            case "multipart/form-data":
                item.Messages.Add("{}");
                report.Skipped(subject, "multipart form body: not imported; rebuild it in Bowire's form editor");
                return true;
            case "application/graphql":
                // Insomnia stores {"query": …, "variables": …} — exactly what a GraphQL endpoint takes over POST.
                item.Messages.Add(InsomniaTemplates.ToBowire(text ?? "{}", unmapped));
                SetContentType(item, "application/json");
                report.Approximated(subject, "GraphQL request became a REST POST with its {\"query\": …} body; for schema-aware calls, add the endpoint as a GraphQL source");
                return true;
            default:
                item.Messages.Add(string.IsNullOrEmpty(text) ? "{}" : InsomniaTemplates.ToBowire(text, unmapped));
                if (mime.Length > 0) SetContentType(item, mime);
                return false;
        }
    }

    // Insomnia keeps auth per request; Bowire keeps it per environment. What
    // fits in a header comes across as one; the rest is named in the report.
    private static void ReadAuth(JsonObject? auth, CollectionItem item, string subject, List<string> unmapped, ReportBuilder report)
    {
        var type = StructuredDocument.Text(auth?["type"]) ?? "";
        if (auth is null || type is "" or "none" or "inherit" || StructuredDocument.IsTrue(auth["disabled"])) return;
        string Field(string key) => InsomniaTemplates.ToBowire(StructuredDocument.Text(auth[key]) ?? "", unmapped);

        switch (type)
        {
            case "bearer":
            {
                var prefix = StructuredDocument.Text(auth["prefix"]);
                item.Metadata.TryAdd("Authorization", (string.IsNullOrEmpty(prefix) ? "Bearer" : prefix) + " " + Field("token"));
                break;
            }
            case "basic":
            {
                var user = Field("username");
                var password = Field("password");
                if (user.Contains("{{", StringComparison.Ordinal) || password.Contains("{{", StringComparison.Ordinal))
                {
                    report.Approximated(subject, "Basic auth with variables cannot be encoded ahead of time; set Basic auth on the Bowire environment instead");
                    return;
                }
                item.Metadata.TryAdd("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
                break;
            }
            case "apikey":
            {
                var key = Field("key");
                var value = Field("value");
                var addTo = StructuredDocument.Text(auth["addTo"]) ?? "header";
                if (addTo == "queryParams")
                {
                    item.Service += (item.Service.Contains('?', StringComparison.Ordinal) ? "&" : "?") + Uri.EscapeDataString(key) + "=" + EscapeKeepingRefs(value);
                    item.ServerUrl = item.Service;
                    if (!ReportBuilder.IsReference(value)) report.Credential(subject, $"API key in query parameter {key}");
                }
                else if (addTo == "cookie")
                {
                    item.Metadata.TryAdd("Cookie", key + "=" + value);
                }
                else
                {
                    item.Metadata.TryAdd(key, value);
                    // A custom header name escapes CheckHeaders' list.
                    if (!ReportBuilder.IsReference(value)) report.Credential(subject, $"API key header {key}");
                }
                break;
            }
            default:
                report.Skipped(subject, $"{type} auth: configure it on the Bowire environment (Bowire has {BowireEquivalent(type)})");
                break;
        }
    }

    private static string BowireEquivalent(string insomniaType) => insomniaType switch
    {
        "oauth2" => "OAuth 2 client-credentials and authorization-code auth",
        "iam" => "AWS SigV4 auth",
        "digest" => "Digest auth",
        "ntlm" => "NTLM auth",
        "asap" or "hawk" or "netrc" => "no equivalent; send the header yourself",
        _ => "its own auth types",
    };

    private static bool HasAuth(JsonObject? auth) =>
        auth is not null && StructuredDocument.Text(auth["type"]) is { Length: > 0 } t && t != "inherit";

    // An explicit "none" on a request stops inheritance; an empty object inherits.
    private static bool HasAuthOrNone(JsonObject auth) =>
        StructuredDocument.Text(auth["type"]) is { Length: > 0 } t && t != "inherit";

    private static void SetContentType(CollectionItem item, string mime)
    {
        if (!item.Metadata.Keys.Any(k => k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            item.Metadata["Content-Type"] = mime;
    }

    // Escape a query value but leave {{refs}} readable for substitution.
    private static string EscapeKeepingRefs(string value)
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

    private static JsonObject? Merge(JsonObject? baseData, JsonObject? own)
    {
        var merged = baseData?.DeepClone() as JsonObject ?? [];
        foreach (var (key, value) in own ?? []) merged[key] = value?.DeepClone();
        return merged;
    }

    // Nested environment objects become dotted names: {"api": {"url": …}}
    // is {{ _.api.url }} in Insomnia and {{api.url}} in Bowire.
    private static IEnumerable<KeyValuePair<string, string>> Flatten(JsonObject? data, string prefix = "")
    {
        foreach (var (key, value) in data ?? [])
        {
            var full = prefix.Length == 0 ? key : prefix + "." + key;
            if (value is JsonObject nested)
            {
                foreach (var pair in Flatten(nested, full)) yield return pair;
            }
            else
            {
                yield return KeyValuePair.Create(full, value is JsonArray ? value.ToJsonString() : StructuredDocument.Text(value) ?? "");
            }
        }
    }
}
