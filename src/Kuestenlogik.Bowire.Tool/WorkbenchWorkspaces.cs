// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// The workspaces the workbench has saved, as the CLI needs them (#365).
/// </summary>
/// <remarks>
/// <para>
/// The inventory is the only thing that knows a workspace's id, its name and
/// whether it is git-native. A directory scan under the identity's slot finds
/// the second kind and misses the first entirely, because a git-native
/// workspace's files live in its checkout.
/// </para>
/// <para>
/// Deliberately narrow: an id, a name and where the files are. The MCP
/// surface reads the same document for the same reason (<c>McpPaths</c>) and
/// wants the same three things, but lives in another package and cannot share
/// a type with this one without core taking a reference on both.
/// </para>
/// </remarks>
internal static class WorkbenchWorkspaces
{
    /// <summary>One workspace, as the CLI addresses it.</summary>
    /// <param name="Id">What <c>--workspace-id</c> names.</param>
    /// <param name="Name">What the operator called it; the id when unnamed.</param>
    /// <param name="StorageRoot">
    /// Set for a git-native workspace: the checkout its artifacts live in.
    /// Null for one kept under the identity's slot.
    /// </param>
    internal sealed record Entry(string Id, string Name, string? StorageRoot);

    /// <summary>
    /// Every workspace in the calling identity's inventory, or empty when
    /// none has been saved.
    /// </summary>
    /// <remarks>
    /// An unreadable inventory reads as "none" rather than throwing: the
    /// caller turns that into a message naming what to do, which is a better
    /// answer than a stack trace for a file the operator never edits by hand.
    /// </remarks>
    internal static IReadOnlyList<Entry> All()
    {
        var raw = WorkspaceInventoryStore.Load();
        if (string.IsNullOrWhiteSpace(raw)) return [];

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!doc.RootElement.TryGetProperty("workspaces", out var list)) return [];
            if (list.ValueKind != JsonValueKind.Array) return [];

            var found = new List<Entry>();
            foreach (var element in list.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                var id = Text(element, "id");
                // The id is what a caller names, so an entry without one
                // cannot be asked for and has no business in the list.
                if (string.IsNullOrEmpty(id)) continue;
                found.Add(new Entry(id, Text(element, "name") ?? id, Text(element, "storageRoot")));
            }
            return found;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Where <paramref name="workspace"/> keeps its flows.</summary>
    /// <remarks>
    /// Through the same seam the stores use, so a git-native workspace
    /// resolves into its checkout — where a clone carries it — and any other
    /// into the identity's slot.
    /// </remarks>
    internal static string FlowsFile(Entry workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return BowireUserContext.GetWorkspacePath(
            workspaceId: workspace.Id,
            storageRoot: workspace.StorageRoot,
            relativePath: "flows.json");
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
