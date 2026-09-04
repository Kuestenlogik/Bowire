// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// Where the MCP surface reads Bowire's own configuration from (#616).
/// </summary>
/// <remarks>
/// <para>
/// Four call sites across three types each rebuilt
/// <c>Path.Combine(home, ".bowire", …)</c> with their own copy of the home
/// override. Which meant that when the storage root moved — a project that
/// opted into <c>.bowire/</c>, an instance segment, the machine scope — the
/// MCP tools kept answering from the old place, and did so while reporting
/// success.
/// </para>
/// <para>
/// <see cref="HomeDirOverride"/> survives as the narrower seam the MCP tests
/// already use. It is a <em>home directory</em>, not a storage root, which is
/// why it still appends <c>.bowire</c>; <c>BOWIRE_DATA_DIR</c> is the broader
/// replacement and needs no such assumption.
/// </para>
/// </remarks>
internal static class McpPaths
{
    /// <summary>
    /// Test-only home-directory override. Production callers leave it null.
    /// </summary>
    /// <remarks>
    /// One property rather than one per type: the tests set it to redirect
    /// "everything MCP reads", and three separate switches made that a promise
    /// each new call site had to remember to keep.
    /// </remarks>
    internal static string? HomeDirOverride { get; set; }

    /// <summary>A file directly under the Bowire storage root.</summary>
    internal static string Config(string filename)
        => HomeDirOverride is { Length: > 0 } home
            ? Path.Combine(home, ".bowire", filename)
            : BowirePaths.Resolve(BowireStorageScope.Data, filename);

    /// <summary>
    /// The calling identity's workspace inventory, or an empty list when they
    /// have none (#642).
    /// </summary>
    /// <remarks>
    /// The list only became answerable in #646, which moved it out of the
    /// browser's localStorage and into the identity's slot. Before that there
    /// was nothing on the server that knew a workspace existed, which is why
    /// this ticket could not be a one-line fix: an agent asking
    /// <c>bowire://flows</c> sends a URI and nothing else, so naming a
    /// workspace in the URI needs something that can list them first.
    /// </remarks>
    internal static IReadOnlyList<McpWorkspace> Workspaces()
    {
        var raw = HomeDirOverride is { Length: > 0 } home
            ? ReadIfExists(Path.Combine(home, ".bowire", "workspaces.json"))
            : WorkspaceInventoryStore.Load();

        if (string.IsNullOrWhiteSpace(raw)) return [];

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!doc.RootElement.TryGetProperty("workspaces", out var list)) return [];
            if (list.ValueKind != JsonValueKind.Array) return [];

            var found = new List<McpWorkspace>();
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var id = Text(entry, "id");
                if (string.IsNullOrEmpty(id)) continue;
                found.Add(new McpWorkspace(id, Text(entry, "name") ?? id, Text(entry, "storageRoot")));
            }
            return found;
        }
        catch (JsonException ex)
        {
            // An unreadable inventory reads as "no workspaces", so the
            // workspace-less resources still answer rather than the whole MCP
            // surface failing over one file.
            _ = ex;
            return [];
        }
    }

    /// <summary>
    /// A file inside one workspace, resolved the same way the workbench
    /// resolves it (#642).
    /// </summary>
    /// <remarks>
    /// With a git-native workspace's <c>storageRoot</c> this lands in the
    /// checkout, so an agent reads what a clone carries. Without one it is the
    /// workspace's folder under the identity's slot. Returns <c>null</c> when
    /// no workspace of that id exists, which the caller turns into a message
    /// naming the index rather than into an empty document — an agent handed
    /// "no data" cannot tell a wrong id from an empty workspace.
    /// </remarks>
    internal static string? WorkspaceConfig(string workspaceId, string filename)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return null;

        var workspace = Workspaces()
            .FirstOrDefault(w => string.Equals(w.Id, workspaceId, StringComparison.Ordinal));
        if (workspace is null) return null;

        if (HomeDirOverride is { Length: > 0 } home && string.IsNullOrEmpty(workspace.StorageRoot))
        {
            return Path.Combine(home, ".bowire", "workspaces", workspaceId, filename);
        }

        return BowireUserContext.GetWorkspacePath(
            workspaceId: workspaceId,
            storageRoot: workspace.StorageRoot,
            relativePath: filename);
    }

    private static string? ReadIfExists(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return null;
        }
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The plugin directory.</summary>
    /// <remarks>
    /// The test override still wins — it exists to keep a suite off the
    /// developer's real <c>~/.bowire</c>. Below it,
    /// <see cref="Kuestenlogik.Bowire.Plugins.BowirePluginRoot"/> rather than the raw resolver,
    /// so <c>bowire.plugins</c> reports the directory a host started with
    /// <c>--plugin-dir</c> actually loads from (#549).
    /// </remarks>
    internal static string Plugins()
        => HomeDirOverride is { Length: > 0 } home
            ? Path.Combine(home, ".bowire", "plugins")
            : Kuestenlogik.Bowire.Plugins.BowirePluginRoot.Current;
}

/// <summary>One entry of the workspace inventory, as MCP needs it (#642).</summary>
/// <param name="Id">The workspace id — what a resource URI names.</param>
/// <param name="Name">What the operator called it.</param>
/// <param name="StorageRoot">
/// Set for a git-native workspace: the checkout its artifacts live in, so an
/// agent reads what a clone carries rather than a private copy.
/// </param>
internal sealed record McpWorkspace(string Id, string Name, string? StorageRoot);
