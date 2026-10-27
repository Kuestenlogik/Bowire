// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop;

/// <summary>
/// Between the Interop messages and the JSON the workbench stores: a
/// <c>.bww</c> workspace file (<c>data.collections</c> / <c>data.environments</c>),
/// a workspace's <c>collections.json</c> (<c>{"collections": […]}</c>), or a bare
/// array of collections. The CLI and the MCP tools read and write these; the
/// workbench maps the same shapes in the browser.
/// </summary>
public static class BowireWorkspaceJson
{
    private static readonly JsonParser s_parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
    private static readonly JsonFormatter s_formatter = new(JsonFormatter.Settings.Default);
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    /// <summary>An import as a <c>.bww</c> workspace file the workbench and <c>bowire workspace import</c> read.</summary>
    public static string ToWorkspaceFile(ImportCollectionResponse import, string workspaceName)
    {
        ArgumentNullException.ThrowIfNull(import);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var collections = new JsonArray();
        foreach (var collection in import.Collections)
        {
            var items = new JsonArray();
            foreach (var item in collection.Items)
            {
                var node = JsonNode.Parse(s_formatter.Format(item))!.AsObject();
                node.Remove("folders");
                node.Remove("httpMethod");
                node.Remove("httpPath");
                if (item.Folders.Count > 0) node["name"] = string.Join(" / ", item.Folders.Append(item.Name));
                node["id"] = "ci_" + NewId();
                // The workbench reads the body from `body` when `messages` is empty.
                node["body"] = item.Messages.FirstOrDefault() ?? "{}";
                items.Add(node);
            }
            collections.Add(new JsonObject
            {
                ["id"] = "col_" + NewId(),
                ["name"] = collection.Name,
                ["items"] = items,
                ["createdAt"] = now,
            });
        }

        var environments = new JsonArray();
        foreach (var env in import.Environments)
        {
            var vars = new JsonObject();
            foreach (var (key, value) in env.Vars) vars[key] = value;
            var node = new JsonObject { ["id"] = "env_" + NewId(), ["name"] = env.Name, ["vars"] = vars };
            if (env.Auth.Count > 0)
            {
                var auth = new JsonObject();
                foreach (var (key, value) in env.Auth) auth[key] = value;
                node["auth"] = auth;
            }
            environments.Add(node);
        }

        var doc = new JsonObject
        {
            ["format"] = "bowire-workspace",
            ["version"] = 2,
            ["workspace"] = new JsonObject { ["name"] = workspaceName, ["color"] = "#6366f1", ["description"] = $"Imported from {import.Format}" },
            ["data"] = new JsonObject { ["collections"] = collections, ["environments"] = environments },
        };
        return doc.ToJsonString(s_indented);
    }

    /// <summary>Collections and environments from a <c>.bww</c>, a <c>collections.json</c> or a bare array.</summary>
    /// <exception cref="InteropFormatException">The JSON is none of these.</exception>
    public static (List<Collection> Collections, List<V1.Environment> Environments) Read(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InteropFormatException("not JSON: " + ex.Message, ex);
        }

        JsonArray? collections = null;
        JsonArray? environments = null;
        switch (root)
        {
            case JsonArray array:
                collections = array;
                break;
            case JsonObject obj when obj["data"] is JsonObject data:
                collections = data["collections"] as JsonArray;
                environments = data["environments"] as JsonArray;
                break;
            case JsonObject obj:
                collections = obj["collections"] as JsonArray;
                environments = obj["environments"] as JsonArray;
                break;
        }
        if (collections is null) throw new InteropFormatException("expected a .bww workspace file, a collections.json or an array of collections");

        var result = new List<Collection>();
        foreach (var node in collections.OfType<JsonObject>())
        {
            var collection = new Collection { Name = (string?)node["name"] ?? "" };
            foreach (var itemNode in (node["items"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var clean = itemNode.DeepClone().AsObject();
                // `metadata: null` is the workbench's "no headers"; protobuf JSON wants it absent.
                if (clean["metadata"] is null) clean.Remove("metadata");
                var item = s_parser.Parse<CollectionItem>(clean.ToJsonString());
                if (item.Messages.Count == 0 && (string?)itemNode["body"] is { Length: > 0 } body) item.Messages.Add(body);
                collection.Items.Add(item);
            }
            result.Add(collection);
        }

        var envs = new List<V1.Environment>();
        foreach (var node in (environments ?? []).OfType<JsonObject>())
        {
            var env = new V1.Environment { Name = (string?)node["name"] ?? "" };
            foreach (var (key, value) in node["vars"] as JsonObject ?? []) env.Vars[key] = Scalar(value);
            foreach (var (key, value) in node["auth"] as JsonObject ?? []) env.Auth[key] = Scalar(value);
            envs.Add(env);
        }
        return (result, envs);
    }

    private static string Scalar(JsonNode? value) => value switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value.ToJsonString(),
    };

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];
}
