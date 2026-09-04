// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Which workspaces an identity has (#646).
/// </summary>
/// <remarks>
/// <para>
/// Multi-tenancy separated everything <i>inside</i> a workspace and nothing
/// <i>about</i> which workspaces exist. A workspace's recordings,
/// environments, collections, flows and plugin settings resolved into the
/// identity's own slot; the list naming those workspaces stayed in
/// <c>bowire_workspaces</c> in one browser's localStorage. Three consequences,
/// each of which reads as its own defect:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Two identities sharing a browser profile saw one list. Their data never
/// mixed, but the inventory in front of them was not theirs, and deleting an
/// entry removed it for the other.
/// </description></item>
/// <item><description>
/// One identity on two machines saw two lists. The directories were still on
/// the server, unreachable because nothing else knew their ids.
/// </description></item>
/// <item><description>
/// Deprovisioning (#96) and <c>bowire users migrate</c> (#97) both work on the
/// slot. The inventory was not in the slot, so it survived a purge and could
/// not be carried into one.
/// </description></item>
/// </list>
/// <para>
/// Putting the file in the slot fixes the last one by construction rather than
/// by adding a case: the migrator walks everything under the storage root that
/// is not on its not-personal list, and deprovisioning archives the slot
/// directory whole. Neither needed a line for flows either.
/// </para>
/// <para>
/// <b>The active selection stays in the browser.</b> Which workspace <i>this
/// window</i> is looking at is view state — two windows may honestly differ,
/// and syncing it would make opening a second tab move the first one. Only the
/// list is an identity's property.
/// </para>
/// <para>
/// <b>Git-native workspaces are listed like any other.</b> Their contents are
/// shared by design — that is the point of pointing one at a checkout — but
/// the entry pointing at one is a personal bookmark. Filing the entry per
/// identity and the contents in the checkout is what lets two people open the
/// same repository without inheriting each other's list.
/// </para>
/// </remarks>
internal static class WorkspaceInventoryStore
{
    private const string FileName = "workspaces.json";

    private static readonly Lock s_gate = new();

    private static string? s_testPathOverride;

    /// <summary>
    /// Test seam — point the store at a scratch file. Set to <c>null</c> to
    /// go back to resolving through <see cref="BowireUserContext"/>.
    /// </summary>
    internal static string? TestPathOverride
    {
        get => s_testPathOverride;
        set => s_testPathOverride = value;
    }

    /// <summary>
    /// The calling identity's inventory as written, or <c>null</c> when they
    /// have never saved one.
    /// </summary>
    /// <remarks>
    /// The null is the point, and it is the distinction #612 cost the
    /// collections: "never saved" and "saved an empty list" have to stay
    /// distinguishable, because the first means the browser's copy is still
    /// authoritative and the second means the person deliberately has no
    /// workspaces. Collapsing them either loses a list or resurrects one
    /// somebody deleted.
    /// </remarks>
    public static string? Load()
    {
        var path = ResolvePath();
        if (path is null) return null;

        lock (s_gate)
        {
            try
            {
                if (!File.Exists(path)) return null;

                var json = File.ReadAllText(path);
                // Parse to validate. A corrupt file reads as "never saved" so
                // the workbench opens on the browser's copy instead of an
                // error page in front of everything the person has.
                using var _ = JsonDocument.Parse(json);
                return json;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _ = ex;
                return null;
            }
        }
    }

    /// <summary>Persist the calling identity's inventory verbatim.</summary>
    /// <param name="json">The document, validated before anything is written.</param>
    /// <returns>
    /// Whether it was stored. <c>false</c> when no slot resolves — an embedded
    /// host with an unusual store — in which case the browser copy carries on
    /// alone, exactly as it did before this existed.
    /// </returns>
    public static bool Save(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        // Validated before the path is touched: a malformed PUT must not be
        // able to leave a file behind that the next load has to recover from.
        // Named rather than discarded: `_` is a using variable here, and the
        // catch below assigns to `_` too.
        using var validated = JsonDocument.Parse(json);

        var path = ResolvePath();
        if (path is null) return false;

        lock (s_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, json);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
                return false;
            }
        }
    }

    /// <summary>
    /// The calling identity's file, or <c>null</c> when no slot can be
    /// resolved. Degrading to "the browser keeps its own list" is the right
    /// direction to fail in: the workbench still opens with the workspaces
    /// the person can see.
    /// </summary>
    private static string? ResolvePath()
    {
        if (s_testPathOverride is not null) return s_testPathOverride;

        try { return BowireUserContext.GetUserPath(FileName); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _ = ex;
            return null;
        }
    }
}
