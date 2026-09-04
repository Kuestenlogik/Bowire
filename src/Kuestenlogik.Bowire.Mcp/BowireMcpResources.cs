// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// MCP resource surface for the Bowire-self adapter — read-only
/// "browse the workbench's state" endpoint pendants to the
/// <see cref="BowireMcpTools"/> tools. Where a tool is an action the
/// agent takes (<c>bowire.invoke</c>, <c>bowire.mock.start</c>), a
/// resource is data the agent reads to decide what to do next.
///
/// <para>
/// URI shape: <c>bowire://&lt;collection&gt;[/&lt;id&gt;]</c>. Listing the
/// collection without an id returns the index; appending an id returns
/// the full item. The id space mirrors what the workbench stores on
/// disk under <c>~/.bowire/</c>.
/// </para>
/// </summary>
[McpServerResourceType]
public sealed class BowireMcpResources
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    { PropertyNameCaseInsensitive = true, WriteIndented = true };

    // -------- Environments --------

    [McpServerResource(
        UriTemplate = "bowire://environments",
        Name = "Environments",
        MimeType = "application/json")]
    [Description("All Bowire environments stored in ~/.bowire/environments.json — names, server URLs, variables, auth blocks. Read this to pick the active env before invoking; use the bowire.env.list tool for the same data via the tools/call path.")]
    public static TextResourceContents Environments()
    {
        var path = ConfigPath("environments.json");
        return ReadJsonFile(path, "bowire://environments");
    }

    // -------- Recordings --------

    [McpServerResource(
        UriTemplate = "bowire://recordings",
        Name = "Recordings (index)",
        MimeType = "application/json")]
    [Description("Index of every captured recording — id, name, protocol, captured-at timestamp, step count. Step bodies omitted to keep the index cheap; read bowire://recordings/{id} for the full content.")]
    public static TextResourceContents RecordingsIndex()
    {
        var path = ConfigPath("recordings.json");
        if (!File.Exists(path))
        {
            return TextResource("bowire://recordings",
                JsonSerializer.Serialize(new { path, recordings = Array.Empty<object>() }, JsonOpts));
        }
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var summary = new List<object>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rec in doc.RootElement.EnumerateArray())
                {
                    summary.Add(new
                    {
                        id = rec.TryGetProperty("id", out var i) ? i.GetString() : null,
                        name = rec.TryGetProperty("name", out var n) ? n.GetString() : null,
                        protocol = rec.TryGetProperty("protocol", out var p) ? p.GetString() : null,
                        createdAt = rec.TryGetProperty("createdAt", out var c) ? c.GetString() : null,
                        stepCount = rec.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array
                            ? s.GetArrayLength() : 0
                    });
                }
            }
            return TextResource("bowire://recordings",
                JsonSerializer.Serialize(new { path, recordings = summary }, JsonOpts));
        }
        catch (Exception ex)
        {
            return TextResource("bowire://recordings",
                $"Failed to read recordings.json: {ex.Message}");
        }
    }

    [McpServerResource(
        UriTemplate = "bowire://recordings/{id}",
        Name = "Recording (full)",
        MimeType = "application/json")]
    [Description("Full recording payload including every step's request/response body, headers, timing. Use bowire://recordings to find the id first.")]
    public static TextResourceContents Recording(
        [Description("Recording id from the index (bowire://recordings).")] string id)
    {
        var path = ConfigPath("recordings.json");
        return ReadOneById(path, "recordings", id, "bowire://recordings/" + id);
    }

    // -------- Collections --------

    [McpServerResource(
        UriTemplate = "bowire://collections",
        Name = "Collections (index)",
        MimeType = "application/json")]
    [Description("Index of saved request collections (Postman-style). Each entry's items: protocol, service, method, body, env-vars. On a host that uses workspaces this file is the pre-workspace one and is usually empty or stale — read bowire://workspaces first and use the workspace-scoped resource instead.")]
    public static TextResourceContents CollectionsIndex()
        => ReadJsonFile(ConfigPath("collections.json"), "bowire://collections");

    [McpServerResource(
        UriTemplate = "bowire://collections/{id}",
        Name = "Collection",
        MimeType = "application/json")]
    [Description("Full collection by id — every saved request the user has parked under that name.")]
    public static TextResourceContents Collection(
        [Description("Collection id.")] string id)
        => ReadOneById(ConfigPath("collections.json"), "collections", id, "bowire://collections/" + id);

    // -------- Flows --------

    [McpServerResource(
        UriTemplate = "bowire://flows",
        Name = "Flows (index)",
        MimeType = "application/json")]
    [Description("Index of every saved visual flow — name, step count, last-modified. On a host that uses workspaces this file is the pre-workspace one and is usually empty or stale — read bowire://workspaces first and use the workspace-scoped resource instead.")]
    public static TextResourceContents FlowsIndex()
        => ReadJsonFile(ConfigPath("flows.json"), "bowire://flows");

    [McpServerResource(
        UriTemplate = "bowire://flows/{id}",
        Name = "Flow",
        MimeType = "application/json")]
    [Description("Full flow definition by id — every step, dependency edge, and variable binding the visual builder produced.")]
    public static TextResourceContents Flow(
        [Description("Flow id from the index.")] string id)
        => ReadOneById(ConfigPath("flows.json"), "flows", id, "bowire://flows/" + id);

    // -------- Workspaces (#642) --------
    //
    // The three families above read the workspace-less files, which on any
    // install that uses workspaces means stale data or none. The workbench
    // sends `?workspaceId=` on every call; an agent asking `bowire://flows`
    // sends a URI and nothing else, so the workspace has to be nameable in the
    // URI — and that needs something that can list them. #646 supplied it.
    //
    // The workspace-less resources deliberately stay, and stay first in the
    // description an agent reads: a host that never adopted workspaces has its
    // data exactly where they look, and breaking that to fix this would trade
    // one wrong answer for another.

    [McpServerResource(
        UriTemplate = "bowire://workspaces",
        Name = "Workspaces (index)",
        MimeType = "application/json")]
    [Description("Every workspace this identity has — id, name, and whether it is backed by a git checkout. Use an id here with bowire://workspaces/{workspaceId}/collections, /recordings or /flows to read what the workbench actually shows. An empty list means the host never adopted workspaces, in which case the plain bowire://collections, ://recordings and ://flows are the right ones.")]
    public static TextResourceContents WorkspacesIndex()
    {
        var workspaces = McpPaths.Workspaces();
        return TextResource("bowire://workspaces", JsonSerializer.Serialize(
            new
            {
                workspaces = workspaces.Select(w => new
                {
                    id = w.Id,
                    name = w.Name,
                    // Named rather than inferred: an agent that knows a
                    // workspace lives in a checkout also knows its contents are
                    // shared with whoever clones it.
                    gitNative = !string.IsNullOrEmpty(w.StorageRoot),
                    storageRoot = w.StorageRoot,
                }),
            },
            JsonOpts));
    }

    [McpServerResource(
        UriTemplate = "bowire://workspaces/{workspaceId}/collections",
        Name = "Collections (workspace)",
        MimeType = "application/json")]
    [Description("Saved request collections in one workspace — what the workbench shows when that workspace is open. Get the id from bowire://workspaces.")]
    public static TextResourceContents WorkspaceCollections(
        [Description("Workspace id from bowire://workspaces.")] string workspaceId)
        => WorkspaceFile(workspaceId, "collections.json", "collections");

    [McpServerResource(
        UriTemplate = "bowire://workspaces/{workspaceId}/recordings",
        Name = "Recordings (workspace)",
        MimeType = "application/json")]
    [Description("Recordings captured in one workspace. Get the id from bowire://workspaces.")]
    public static TextResourceContents WorkspaceRecordings(
        [Description("Workspace id from bowire://workspaces.")] string workspaceId)
        => WorkspaceFile(workspaceId, "recordings.json", "recordings");

    [McpServerResource(
        UriTemplate = "bowire://workspaces/{workspaceId}/flows",
        Name = "Flows (workspace)",
        MimeType = "application/json")]
    [Description("Visual flows saved in one workspace. Get the id from bowire://workspaces.")]
    public static TextResourceContents WorkspaceFlows(
        [Description("Workspace id from bowire://workspaces.")] string workspaceId)
        => WorkspaceFile(workspaceId, "flows.json", "flows");

    /// <summary>
    /// One workspace-scoped document, or a message naming the index.
    /// </summary>
    /// <remarks>
    /// An unknown id answers with an error rather than an empty document on
    /// purpose. "No data" cannot be told apart from "wrong id" by the agent
    /// reading it, and this whole ticket exists because a resource answered
    /// confidently with the wrong thing.
    /// </remarks>
    private static TextResourceContents WorkspaceFile(
        string workspaceId, string filename, string collectionName)
    {
        var uri = $"bowire://workspaces/{workspaceId}/{collectionName}";
        var path = McpPaths.WorkspaceConfig(workspaceId, filename);
        if (path is null)
        {
            return TextResource(uri,
                $"No workspace with id '{workspaceId}'. Read bowire://workspaces for the ids that exist.");
        }
        return ReadJsonFile(path, uri);
    }

    // -------- Plugins + allowlist --------

    [McpServerResource(
        UriTemplate = "bowire://plugins",
        Name = "Installed plugins",
        MimeType = "application/json")]
    [Description("Sibling plugins in the host's plugin directory — package id + version per directory. That is ~/.bowire/plugins/ by default, or wherever the host was pointed with --plugin-dir / BOWIRE_PLUGIN_DIR / Bowire:PluginDir. Bundled plugins (the ones shipped inside the Bowire tool) are not listed here; the tool's --version banner covers them.")]
    public static TextResourceContents Plugins()
    {
        var pluginDir = McpPaths.Plugins();
        var entries = new List<object>();
        if (Directory.Exists(pluginDir))
        {
            foreach (var dir in Directory.GetDirectories(pluginDir))
            {
                var metaPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(metaPath)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                    entries.Add(new
                    {
                        packageId = doc.RootElement.TryGetProperty("packageId", out var p)
                            ? p.GetString() : Path.GetFileName(dir),
                        version = doc.RootElement.TryGetProperty("version", out var v)
                            ? v.GetString() : "unknown",
                        directory = dir,
                    });
                }
                catch { /* skip broken plugin.json */ }
            }
        }
        return TextResource("bowire://plugins",
            JsonSerializer.Serialize(new { pluginDir, plugins = entries }, JsonOpts));
    }

    // (No bowire://allowlist resource — the bowire.allowlist.show tool
    // already serves that data, and the allowlist comes off DI-bound
    // options rather than a disk file, so wiring it as a static
    // resource would mean duplicating the accessor surface.)

    // -------- Helpers --------

    internal static string? HomeDirOverride
    {
        get => BowireMcpTools.HomeDirOverride;
        set => BowireMcpTools.HomeDirOverride = value;
    }

    private static string ConfigPath(string filename) => McpPaths.Config(filename);

    private static TextResourceContents ReadJsonFile(string path, string uri)
    {
        if (!File.Exists(path))
        {
            return TextResource(uri, JsonSerializer.Serialize(new { path, items = Array.Empty<object>() }, JsonOpts));
        }
        try
        {
            var text = File.ReadAllText(path);
            return TextResource(uri, text);
        }
        catch (Exception ex)
        {
            return TextResource(uri, $"Failed to read {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static TextResourceContents ReadOneById(string path, string collectionKey, string id, string uri)
    {
        if (!File.Exists(path))
            return TextResource(uri, "{}");
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            // Files are either a top-level array (older layout) or
            // { collectionKey: [...] } (newer layout). Try both.
            JsonElement? array = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.TryGetProperty(collectionKey, out var arr) ? arr : null;
            if (array is null) return TextResource(uri, "{}");

            foreach (var rec in array.Value.EnumerateArray())
            {
                if (rec.TryGetProperty("id", out var i) && i.GetString() == id)
                {
                    return TextResource(uri, rec.GetRawText());
                }
            }
            return TextResource(uri, $"{{ \"error\": \"no {collectionKey} entry with id={id}\" }}");
        }
        catch (Exception ex)
        {
            return TextResource(uri, $"Failed to read: {ex.Message}");
        }
    }

    private static TextResourceContents TextResource(string uri, string text)
        => new()
        {
            Uri = uri,
            MimeType = "application/json",
            Text = text,
        };
}
