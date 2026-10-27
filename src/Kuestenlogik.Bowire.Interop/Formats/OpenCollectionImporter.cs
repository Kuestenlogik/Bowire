// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>
/// OpenCollection YAML (#674), Bruno's format since 3.0
/// (schema: https://schema.opencollection.com/opencollection/v1.0.0.json).
/// Two layouts: one bundled file with the whole <c>items</c> tree, or a folder
/// with <c>opencollection.yml</c>, one YAML file per request,
/// <c>folder.yml</c> per folder and <c>environments/*.yml</c>.
/// </summary>
internal sealed class OpenCollectionImporter : IInteropImporter
{
    public string Id => "opencollection";

    public bool CanRead(IReadOnlyList<SourceFile> files) => files.Any(f => IsYaml(f.Path) && HasRootMarker(f.Content));

    public ImportCollectionResponse Read(IReadOnlyList<SourceFile> files, string? name, ReportBuilder report)
    {
        var rootFile = files.Where(f => IsYaml(f.Path) && HasRootMarker(f.Content)).OrderBy(f => f.Path.Length).First();
        var root = Load(rootFile);
        var source = new BrunoCollection
        {
            Name = StructuredDocument.Text((root["info"] as JsonObject)?["name"]) ?? Path.GetFileNameWithoutExtension(rootFile.Path),
        };
        ReadDefaults(root["request"] as JsonObject, source.Root);
        foreach (var env in ((root["config"] as JsonObject)?["environments"] as JsonArray ?? []).OfType<JsonObject>())
            source.Environments.Add(ReadEnvironment(env));

        if (root["items"] is JsonArray items)
        {
            Walk(items, [], source, report);
        }
        else
        {
            ReadFolderLayout(files, rootFile, source, report);
        }

        ResolveExtends(source);
        var response = new ImportCollectionResponse { Format = Id };
        BrunoMapper.Emit(source, name, response, report);
        return response;
    }

    // ---- bundled: one tree ----

    private static void Walk(JsonArray items, List<string> folders, BrunoCollection source, ReportBuilder report)
    {
        foreach (var item in items.OfType<JsonObject>().OrderBy(Seq))
        {
            var info = item["info"] as JsonObject;
            var type = StructuredDocument.Text(info?["type"]);
            var itemName = StructuredDocument.Text(info?["name"]) ?? "Request";
            if (type == "folder" || item["items"] is JsonArray)
            {
                List<string> path = [.. folders, itemName];
                var defaults = new BrunoDefaults();
                ReadDefaults(item["request"] as JsonObject, defaults);
                source.Folders[string.Join("/", path)] = defaults;
                Walk(item["items"] as JsonArray ?? [], path, source, report);
            }
            else if (type == "script" || item["script"] is not null)
            {
                report.Skipped(itemName, "shared script module: Bowire scripts use their own ctx.* API");
            }
            else
            {
                source.Requests.Add(ReadRequest(item, folders, itemName));
            }
        }
    }

    // ---- on disk: a file per request ----

    private static void ReadFolderLayout(IReadOnlyList<SourceFile> files, SourceFile rootFile, BrunoCollection source, ReportBuilder report)
    {
        var rootDir = Directory(rootFile.Path);
        var folderNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var candidates = files
            .Where(f => IsYaml(f.Path) && f != rootFile)
            .Select(f => (File: f, Rel: Relative(f.Path, rootDir)))
            .Where(f => f.Rel is not null)
            .ToList();

        foreach (var (file, rel) in candidates.Where(c => FileName(c.Rel!) is "folder.yml" or "folder.yaml"))
        {
            var doc = Load(file);
            var dir = Directory(rel!);
            folderNames[dir] = StructuredDocument.Text((doc["info"] as JsonObject)?["name"]) ?? LastSegment(dir);
            var defaults = new BrunoDefaults();
            ReadDefaults(doc["request"] as JsonObject, defaults);
            source.Folders[string.Join("/", DisplayPath(dir, folderNames))] = defaults;
        }

        var requests = new List<(string Dir, double Seq, BrunoRequest Request)>();
        foreach (var (file, rel) in candidates)
        {
            var dir = Directory(rel!);
            if (FileName(rel!) is "folder.yml" or "folder.yaml") continue;
            var doc = Load(file);
            if (LastSegment(dir).Equals("environments", StringComparison.OrdinalIgnoreCase))
            {
                var env = ReadEnvironment(doc);
                if (env.Name.Length == 0) env = Renamed(env, Path.GetFileNameWithoutExtension(rel!));
                source.Environments.Add(env);
                continue;
            }
            var info = doc["info"] as JsonObject;
            if (info is null && doc["http"] is null && doc["graphql"] is null)
            {
                report.Skipped(rel!, "not a request file");
                continue;
            }
            var itemName = StructuredDocument.Text(info?["name"]) ?? Path.GetFileNameWithoutExtension(rel!);
            requests.Add((dir, Seq(doc), ReadRequest(doc, DisplayPath(dir, folderNames), itemName)));
        }
        foreach (var (_, _, request) in requests.OrderBy(r => r.Dir, StringComparer.Ordinal).ThenBy(r => r.Seq))
            source.Requests.Add(request);
    }

    // ---- the pieces ----

    private static BrunoRequest ReadRequest(JsonObject item, List<string> folders, string itemName)
    {
        var info = item["info"] as JsonObject;
        var type = StructuredDocument.Text(info?["type"])
            ?? (item["graphql"] is not null ? "graphql" : item["grpc"] is not null ? "grpc" : item["websocket"] is not null ? "websocket" : "http");
        var details = (item["http"] ?? item["graphql"]) as JsonObject;
        var request = new BrunoRequest
        {
            Name = itemName,
            Folders = folders,
            Kind = type,
            Method = StructuredDocument.Text(details?["method"]) ?? (type == "graphql" ? "POST" : "GET"),
            Url = StructuredDocument.Text(details?["url"]) ?? "",
        };
        if (details is null) return request;

        request.Headers.AddRange(Pairs(details["headers"] as JsonArray));
        foreach (var p in (details["params"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var pair = Pair(p);
            if (StructuredDocument.Text(p["type"]) == "path") request.PathParams.Add(pair);
            else request.Query.Add(pair);
        }

        var body = details["body"] switch
        {
            JsonArray variants => variants.OfType<JsonObject>().FirstOrDefault(v => StructuredDocument.IsTrue(v["selected"]))?["body"] as JsonObject
                ?? variants.OfType<JsonObject>().FirstOrDefault()?["body"] as JsonObject,
            JsonObject single => single,
            _ => null,
        };
        if (type == "graphql")
        {
            request.BodyType = "graphql";
            request.BodyText = StructuredDocument.Text(body?["query"]);
            request.GraphQlVariables = StructuredDocument.Text(body?["variables"]);
        }
        else if (body is not null)
        {
            request.BodyType = StructuredDocument.Text(body["type"]) ?? "none";
            if (body["data"] is JsonArray formData) request.Form.AddRange(Pairs(formData));
            else request.BodyText = StructuredDocument.Text(body["data"]);
        }

        request.Auth = ReadAuth(details["auth"]);

        var runtime = item["runtime"] as JsonObject;
        foreach (var script in (runtime?["scripts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            request.LeftOut.Add(StructuredDocument.Text(script["type"]) switch
            {
                "before-request" => "pre-request script: Bowire scripts use their own ctx.* API; port it by hand",
                "after-response" => "post-response script: write it as Bowire assertions or a post-script",
                "tests" => "tests: write them as Bowire assertions",
                var other => $"{other} script: not imported",
            });
        }
        if ((runtime?["assertions"] as JsonArray)?.Count > 0)
            request.LeftOut.Add("assertions: Bowire's assertions use their own operators; re-create them on the item");
        if ((runtime?["variables"] as JsonArray)?.Count > 0)
            request.LeftOut.Add("request variables: Bowire has no per-request variables; put them in the environment");
        if ((runtime?["actions"] as JsonArray)?.Count > 0)
            request.LeftOut.Add("runtime actions (set variable from the response): use {{response.path}} chaining in Bowire");
        return request;
    }

    private static void ReadDefaults(JsonObject? request, BrunoDefaults defaults)
    {
        if (request is null) return;
        defaults.Headers.AddRange(Pairs(request["headers"] as JsonArray));
        defaults.Auth = ReadAuth(request["auth"]);
        defaults.Variables.AddRange(Pairs(request["variables"] as JsonArray));
        defaults.HasScripts = (request["scripts"] as JsonArray)?.Count > 0;
    }

    private static BrunoAuth? ReadAuth(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue) return new BrunoAuth { Type = StructuredDocument.Text(node) ?? "inherit" };
        if (node is not JsonObject obj) return null;
        var auth = new BrunoAuth { Type = StructuredDocument.Text(obj["type"]) ?? "none" };
        foreach (var (key, value) in obj)
        {
            if (key != "type" && StructuredDocument.Text(value) is { } text) auth.Fields[key] = text;
        }
        return auth;
    }

    private static BrunoEnvironment ReadEnvironment(JsonObject env)
    {
        var result = new BrunoEnvironment { Name = StructuredDocument.Text(env["name"]) ?? "", Extends = StructuredDocument.Text(env["extends"]) };
        foreach (var variable in (env["variables"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var varName = StructuredDocument.Text(variable["name"]) ?? "";
            if (varName.Length == 0) continue;
            if (StructuredDocument.IsTrue(variable["secret"]))
            {
                if (!StructuredDocument.IsTrue(variable["disabled"])) result.SecretNames.Add(varName);
                continue;
            }
            result.Variables.Add(new BruPair(varName, VariableValue(variable["value"]), StructuredDocument.IsTrue(variable["disabled"])));
        }
        return result;
    }

    // `extends` names another environment: its values fill what this one does not set.
    private static void ResolveExtends(BrunoCollection source)
    {
        foreach (var env in source.Environments.Where(e => !string.IsNullOrEmpty(e.Extends)))
        {
            if (source.Environments.FirstOrDefault(e => e.Name == env.Extends) is not { } baseEnv || baseEnv == env) continue;
            foreach (var v in baseEnv.Variables.Where(v => env.Variables.All(o => o.Name != v.Name)).ToList()) env.Variables.Add(v);
            foreach (var secret in baseEnv.SecretNames.Where(n => !env.SecretNames.Contains(n)).ToList()) env.SecretNames.Add(secret);
        }
    }

    private static BrunoEnvironment Renamed(BrunoEnvironment env, string name)
    {
        var copy = new BrunoEnvironment { Name = name, Extends = env.Extends };
        copy.Variables.AddRange(env.Variables);
        copy.SecretNames.AddRange(env.SecretNames);
        return copy;
    }

    // A value is a string, a typed {type, data}, or a list of variants of either.
    private static string VariableValue(JsonNode? value) => value switch
    {
        JsonObject typed => StructuredDocument.Text(typed["data"]) ?? "",
        JsonArray variants => VariableValue((variants.OfType<JsonObject>().FirstOrDefault(v => StructuredDocument.IsTrue(v["selected"]))
            ?? variants.OfType<JsonObject>().FirstOrDefault())?["value"]),
        _ => StructuredDocument.Text(value) ?? "",
    };

    private static IEnumerable<BruPair> Pairs(JsonArray? array) => (array ?? []).OfType<JsonObject>().Select(Pair).Where(p => p.Name.Length > 0);

    private static BruPair Pair(JsonObject o) =>
        new(StructuredDocument.Text(o["name"]) ?? "", StructuredDocument.Text(o["value"]) ?? "", StructuredDocument.IsTrue(o["disabled"]));

    private static double Seq(JsonObject item) =>
        double.TryParse(StructuredDocument.Text((item["info"] as JsonObject)?["seq"]), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;

    private static JsonObject Load(SourceFile file) =>
        StructuredDocument.TryLoad(file.Content) ?? throw new InteropFormatException($"{file.Path} is not valid YAML");

    private static bool HasRootMarker(string content) =>
        content.StartsWith("opencollection:", StringComparison.Ordinal) || content.Contains("\nopencollection:", StringComparison.Ordinal)
        || content.TrimStart().StartsWith('{') && content.Contains("\"opencollection\"", StringComparison.Ordinal);

    private static bool IsYaml(string path) =>
        path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static List<string> DisplayPath(string dir, Dictionary<string, string> folderNames)
    {
        var result = new List<string>();
        if (dir.Length == 0) return result;
        var parts = dir.Split('/');
        for (var i = 1; i <= parts.Length; i++)
            result.Add(folderNames.GetValueOrDefault(string.Join("/", parts.Take(i))) ?? parts[i - 1]);
        return result;
    }

    private static string? Relative(string path, string rootDir)
    {
        var normal = path.Replace('\\', '/');
        if (rootDir.Length == 0) return normal;
        return normal.StartsWith(rootDir + "/", StringComparison.Ordinal) ? normal[(rootDir.Length + 1)..] : null;
    }

    private static string FileName(string path) => Path.GetFileName(path.Replace('\\', '/'));

    private static string Directory(string path)
    {
        var normal = path.Replace('\\', '/');
        var slash = normal.LastIndexOf('/');
        return slash < 0 ? "" : normal[..slash];
    }

    private static string LastSegment(string dir) => dir[(dir.LastIndexOf('/') + 1)..];
}
