// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>
/// Yaak exports (#939): the JSON file Yaak writes from <i>Export Data</i> or
/// <c>yaak export</c>, <c>{ yaakVersion, yaakSchema, resources: { workspaces,
/// environments, folders, httpRequests, grpcRequests, websocketRequests } }</c>,
/// with every resource linked to its workspace and folder by id.
/// </summary>
/// <remarks>
/// <para>
/// Each workspace becomes a collection, its folders the item folders. Yaak
/// sends the headers of the workspace and of every folder above a request
/// with it, and lets a request inherit authentication from the nearest
/// folder or the workspace that sets one; the import does both, so the
/// imported item sends what Yaak sent.
/// </para>
/// <para>
/// Environments: a workspace's base environment, and each of its
/// sub-environments laid over it. Private environments are in the file only
/// when it was exported with them, and their values are reported as
/// credentials.
/// </para>
/// </remarks>
internal sealed class YaakImporter : IInteropImporter
{
    public string Id => "yaak";

    public bool CanRead(IReadOnlyList<SourceFile> files) => files.Any(f =>
        f.Content.Contains("\"yaakSchema\"", StringComparison.Ordinal)
        && f.Content.Contains("\"resources\"", StringComparison.Ordinal));

    public ImportCollectionResponse Read(IReadOnlyList<SourceFile> files, string? name, ReportBuilder report)
    {
        var response = new ImportCollectionResponse { Format = Id };
        var notHttp = 0;
        foreach (var file in files)
        {
            var doc = StructuredDocument.TryLoad(file.Content);
            if (doc?["resources"] is not JsonObject resources) continue;

            var workspaces = List(resources, "workspaces");
            foreach (var workspace in workspaces)
            {
                notHttp += ReadWorkspace(workspace, resources, files.Count == 1 && workspaces.Count == 1 ? name : null, response, report);
            }
        }
        if (response.Collections.Count == 0)
        {
            throw new InteropFormatException(notHttp > 0
                ? $"the export holds no HTTP request, only gRPC or WebSocket requests ({notHttp}). "
                    + "Bowire lists those from the server: add its URL as a source"
                : "no workspace with requests found in the export");
        }
        return response;
    }

    // ---- one workspace ----

    private static int ReadWorkspace(JsonObject workspace, JsonObject resources, string? name, ImportCollectionResponse response, ReportBuilder report)
    {
        var wsId = Text(workspace, "id");
        var collection = new Collection { Name = name ?? Text(workspace, "name") ?? "Yaak" };
        bool InWorkspace(JsonObject r) => string.Equals(Text(r, "workspaceId"), wsId, StringComparison.Ordinal);

        var folders = List(resources, "folders").Where(InWorkspace)
            .ToDictionary(f => Text(f, "id") ?? "", f => f, StringComparer.Ordinal);
        var requests = List(resources, "httpRequests").Where(InWorkspace).ToList();
        var others = List(resources, "grpcRequests").Where(InWorkspace).Select(r => ("gRPC", r))
            .Concat(List(resources, "websocketRequests").Where(InWorkspace).Select(r => ("WebSocket", r))).ToList();

        // Depth-first in Yaak's own order: folders and requests of one parent
        // by sortPriority, so the collection reads as the sidebar did.
        var children = folders.Values.Select(f => (Parent: Text(f, "folderId") ?? "", Node: f, IsFolder: true))
            .Concat(requests.Select(r => (Parent: Text(r, "folderId") ?? "", Node: r, IsFolder: false)))
            .GroupBy(c => c.Parent, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => SortPriority(c.Node)).ToList(), StringComparer.Ordinal);

        var inherited = new Scope(Headers(workspace), OwnAuth(workspace));
        Walk("", [], inherited);

        foreach (var (kind, other) in others)
        {
            report.Skipped(Subject(PathOf(Text(other, "folderId")), Text(other, "name") ?? kind),
                $"{kind} request: Bowire discovers {kind} services from the server; add its URL as a source instead");
        }

        if (collection.Items.Count == 0)
        {
            report.Skipped(collection.Name, "no HTTP request in this workspace");
        }
        else
        {
            response.Collections.Add(collection);
        }
        ReadEnvironments(wsId, resources, response, report);
        return others.Count;

        void Walk(string parentId, List<string> path, Scope scope)
        {
            if (!children.TryGetValue(parentId, out var nodes)) return;
            foreach (var (_, node, isFolder) in nodes)
            {
                if (isFolder)
                {
                    var folderName = Text(node, "name") ?? "Folder";
                    Walk(Text(node, "id") ?? "", [.. path, folderName], scope.Under(Headers(node), OwnAuth(node)));
                }
                else
                {
                    ReadRequest(node, path, scope, collection, report);
                }
            }
        }

        List<string> PathOf(string? folderId)
        {
            var path = new List<string>();
            for (var id = folderId; id is not null && folders.TryGetValue(id, out var f); id = Text(f, "folderId"))
                path.Insert(0, Text(f, "name") ?? "Folder");
            return path;
        }
    }

    // What a request inherits from the workspace and the folders above it.
    private sealed record Scope(List<(string Name, string Value)> Headers, JsonObject? Auth)
    {
        public Scope Under(List<(string Name, string Value)> headers, JsonObject? auth) =>
            new([.. Headers.Where(h => !headers.Any(o => o.Name.Equals(h.Name, StringComparison.OrdinalIgnoreCase))), .. headers],
                auth ?? Auth);
    }

    private static void ReadRequest(JsonObject src, List<string> folders, Scope scope, Collection collection, ReportBuilder report)
    {
        var requestName = Text(src, "name") ?? "Request";
        var subject = Subject(folders, requestName);
        var unmapped = new List<string>();
        var method = (Text(src, "method") ?? "GET").ToUpperInvariant();
        var url = YaakTemplates.ToBowire(Text(src, "url") ?? "", unmapped);

        var query = new List<string>();
        foreach (var p in Array(src, "urlParameters"))
        {
            var pName = Text(p, "name") ?? "";
            if (pName.Length == 0) continue;
            if (p["enabled"] is not null && !StructuredDocument.IsTrue(p["enabled"]))
            {
                report.Skipped(subject, $"disabled query parameter {pName}");
                continue;
            }
            var value = YaakTemplates.ToBowire(Text(p, "value") ?? "", unmapped);
            // A path parameter (:id) is filled in place, not appended.
            if (pName.StartsWith(':') && url.Contains(pName, StringComparison.Ordinal))
            {
                url = url.Replace(pName, value, StringComparison.Ordinal);
                continue;
            }
            query.Add(Uri.EscapeDataString(pName) + "=" + ImportHelpers.EscapeKeepingRefs(value));
        }
        url = ImportHelpers.AppendQuery(url, query);

        var item = new CollectionItem
        {
            Name = requestName,
            Protocol = "rest",
            Service = url,
            Method = method,
            MethodType = "Unary",
            ServerUrl = url,
        };
        item.Folders.Add(folders);

        var own = Headers(src, report, subject);
        foreach (var (hName, hValue) in scope.Under(own, null).Headers)
            item.Metadata[hName] = YaakTemplates.ToBowire(hValue, unmapped);

        var reshaped = ReadBody(src, item, subject, unmapped, report);
        ReadAuth(AuthOf(src) ?? scope.Auth, item, subject, unmapped, report);
        report.CheckHeaders(subject, item.Metadata);

        foreach (var u in unmapped.Distinct(StringComparer.Ordinal)) report.Approximated(subject, YaakTemplates.Unmapped(u));
        if (!reshaped) report.Mapped(subject);
        collection.Items.Add(item);
    }

    // Returns true when the body changed shape (already reported).
    private static bool ReadBody(JsonObject src, CollectionItem item, string subject, List<string> unmapped, ReportBuilder report)
    {
        var type = Text(src, "bodyType");
        var body = src["body"] as JsonObject ?? [];
        switch (type)
        {
            case null or "" or "none":
                item.Messages.Add("{}");
                return false;
            case "application/x-www-form-urlencoded":
            {
                var pairs = new List<string>();
                foreach (var f in Array(body, "form"))
                {
                    if (f["enabled"] is not null && !StructuredDocument.IsTrue(f["enabled"])) continue;
                    pairs.Add(Uri.EscapeDataString(Text(f, "name") ?? "") + "="
                        + ImportHelpers.EscapeKeepingRefs(YaakTemplates.ToBowire(Text(f, "value") ?? "", unmapped)));
                }
                item.Messages.Add(string.Join("&", pairs));
                ImportHelpers.SetContentType(item, type);
                report.Approximated(subject, "form fields were joined into a urlencoded body");
                return true;
            }
            case "multipart/form-data":
                item.Messages.Add("{}");
                report.Skipped(subject, "multipart form body: not imported; rebuild it in Bowire's form editor");
                return true;
            case "binary":
                item.Messages.Add("{}");
                report.Skipped(subject, "body from a file: the file is not part of the export");
                return true;
            case "graphql":
            {
                // Yaak keeps the query and the variables (as text) apart; a
                // GraphQL endpoint takes them as one JSON document over POST.
                var graphQl = new JsonObject { ["query"] = YaakTemplates.ToBowire(Text(body, "query") ?? "", unmapped) };
                var variables = Text(body, "variables");
                if (!string.IsNullOrWhiteSpace(variables))
                {
                    try { graphQl["variables"] = JsonNode.Parse(YaakTemplates.ToBowire(variables, unmapped)); }
                    catch (System.Text.Json.JsonException) { graphQl["variables"] = variables; }
                }
                item.Messages.Add(graphQl.ToJsonString());
                ImportHelpers.SetContentType(item, "application/json");
                report.Approximated(subject, "GraphQL request became a REST POST with its {\"query\": …} body; for schema-aware calls, add the endpoint as a GraphQL source");
                return true;
            }
            default:
            {
                // application/json, application/xml, text/plain, "other": the text as written.
                var text = Text(body, "text");
                item.Messages.Add(string.IsNullOrEmpty(text) ? "{}" : YaakTemplates.ToBowire(text, unmapped));
                if (type.Contains('/', StringComparison.Ordinal)) ImportHelpers.SetContentType(item, type);
                return false;
            }
        }
    }

    // Yaak keeps auth per request, folder and workspace; Bowire keeps it per
    // environment. What fits in a header comes across as one.
    private static void ReadAuth(JsonObject? auth, CollectionItem item, string subject, List<string> unmapped, ReportBuilder report)
    {
        if (auth is null) return;
        var type = Text(auth, "type") ?? "";
        var values = auth["values"] as JsonObject ?? [];
        if (type is "" or "none" || (values["disabled"] is not null && StructuredDocument.IsTrue(values["disabled"]))) return;
        string Field(string key) => YaakTemplates.ToBowire(Text(values, key) ?? "", unmapped);

        switch (type)
        {
            case "bearer":
            {
                var prefix = Text(values, "prefix");
                item.Metadata.TryAdd("Authorization", (string.IsNullOrEmpty(prefix) ? "Bearer" : prefix) + " " + Field("token"));
                break;
            }
            case "basic":
            {
                var header = ImportHelpers.BasicHeader(Field("username"), Field("password"));
                if (header is null)
                {
                    report.Approximated(subject, "Basic auth with variables cannot be encoded ahead of time; set Basic auth on the Bowire environment instead");
                    return;
                }
                item.Metadata.TryAdd("Authorization", header);
                break;
            }
            case "apikey":
            {
                var key = Field("key");
                var value = Field("value");
                if (string.Equals(Text(values, "location"), "query", StringComparison.OrdinalIgnoreCase))
                {
                    item.Service = ImportHelpers.AppendQuery(item.Service, [Uri.EscapeDataString(key) + "=" + ImportHelpers.EscapeKeepingRefs(value)]);
                    item.ServerUrl = item.Service;
                    if (!ReportBuilder.IsReference(value)) report.Credential(subject, $"API key in query parameter {key}");
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
                report.Skipped(subject, $"{type} auth: configure it on the Bowire environment (Bowire has {ImportHelpers.BowireEquivalent(type)})");
                break;
        }
    }

    private static void ReadEnvironments(string? wsId, JsonObject resources, ImportCollectionResponse response, ReportBuilder report)
    {
        var all = List(resources, "environments")
            .Where(e => string.Equals(Text(e, "workspaceId"), wsId, StringComparison.Ordinal)).ToList();
        var byId = all.ToDictionary(e => Text(e, "id") ?? "", e => e, StringComparer.Ordinal);
        var reportedDisabled = new HashSet<string>(StringComparer.Ordinal);
        // The base environment hangs off the workspace. Every other one is a
        // sub-environment: of the one its parentId names, or of the base when
        // it names none. Picking one in Yaak lays it over its parents and the base.
        var baseEnv = all.FirstOrDefault(e => Text(e, "parentModel") == "workspace");
        var subs = all.Where(e => !ReferenceEquals(e, baseEnv)).OrderBy(SortPriority).ToList();
        if (subs.Count == 0)
        {
            if (baseEnv is not null) Add(Text(baseEnv, "name") ?? "Global Variables", [baseEnv]);
            return;
        }
        foreach (var sub in subs)
        {
            var layers = new List<JsonObject>();
            var seen = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
            for (var e = sub; e is not null && seen.Add(e);
                 e = Text(e, "parentId") is { } parent && byId.TryGetValue(parent, out var p) ? p : null)
            {
                layers.Insert(0, e);
            }
            if (baseEnv is not null) layers.Insert(0, baseEnv);
            Add(Text(sub, "name") ?? "Environment", layers);
        }

        void Add(string envName, List<JsonObject> layers)
        {
            var env = new V1.Environment { Name = envName };
            var unmapped = new List<string>();
            foreach (var layer in layers)
            {
                foreach (var v in Array(layer, "variables"))
                {
                    var key = Text(v, "name") ?? "";
                    if (key.Length == 0) continue;
                    if (v["enabled"] is not null && !StructuredDocument.IsTrue(v["enabled"]))
                    {
                        // Once, under the environment that declares it: a
                        // base layer lies under every sub-environment.
                        var owner = Text(layer, "name") ?? envName;
                        if (reportedDisabled.Add(owner + "\u0000" + key)) report.Skipped(owner, $"disabled variable {key}");
                        continue;
                    }
                    env.Vars[key] = YaakTemplates.ToBowire(Text(v, "value") ?? "", unmapped);
                }
            }
            // A private environment is in the file only when it was exported
            // with its values; they are what the export keeps out by default.
            if (layers.Any(l => l["public"] is not null && !StructuredDocument.IsTrue(l["public"])))
            {
                foreach (var key in env.Vars.Keys) report.Credential(envName, $"private variable {key}");
            }
            foreach (var u in unmapped.Distinct(StringComparer.Ordinal)) report.Approximated(envName, YaakTemplates.Unmapped(u));
            response.Environments.Add(env);
            report.Mapped(envName, "environment");
        }
    }

    // ---- small readers ----

    // A request, folder or workspace sets auth with authenticationType;
    // null inherits, "none" stops inheriting.
    private static JsonObject? OwnAuth(JsonObject node) => AuthOf(node);

    private static JsonObject? AuthOf(JsonObject node)
    {
        var type = Text(node, "authenticationType");
        if (type is null) return null;
        return new JsonObject { ["type"] = type, ["values"] = (node["authentication"] as JsonObject)?.DeepClone() ?? new JsonObject() };
    }

    private static List<(string Name, string Value)> Headers(JsonObject node, ReportBuilder? report = null, string? subject = null)
    {
        var headers = new List<(string, string)>();
        foreach (var h in Array(node, "headers"))
        {
            var hName = Text(h, "name") ?? "";
            if (hName.Length == 0) continue;
            if (h["enabled"] is not null && !StructuredDocument.IsTrue(h["enabled"]))
            {
                report?.Skipped(subject ?? "", $"disabled header {hName}");
                continue;
            }
            headers.Add((hName, Text(h, "value") ?? ""));
        }
        return headers;
    }

    private static string Subject(List<string> folders, string name) =>
        folders.Count > 0 ? string.Join(" / ", folders) + " / " + name : name;

    private static double SortPriority(JsonObject node) =>
        double.TryParse(Text(node, "sortPriority"), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;

    private static List<JsonObject> List(JsonObject parent, string key) => Array(parent, key);

    private static List<JsonObject> Array(JsonObject parent, string key) =>
        (parent[key] as JsonArray ?? []).OfType<JsonObject>().ToList();

    private static string? Text(JsonObject node, string key) => StructuredDocument.Text(node[key]);
}
