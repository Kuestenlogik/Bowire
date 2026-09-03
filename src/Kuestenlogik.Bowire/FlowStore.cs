// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Disk-backed store for the flows the visual builder produces (#641).
/// </summary>
/// <remarks>
/// <para>
/// Flows were the last major artifact the workbench never sent anywhere. They
/// lived in the browser under <c>bowire_flows</c>, which had four consequences
/// worth naming, because each of them looked like a separate defect:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The MCP resources <c>bowire://flows</c> and <c>bowire://flows/{id}</c> read
/// a <c>flows.json</c> that nothing wrote. An agent asking for a flow it could
/// see in the workbench got an empty document.
/// </description></item>
/// <item><description>
/// <c>bowire test --workspace</c> reads flow files from a directory, so it
/// could never run what somebody built in the workbench without a manual
/// export first.
/// </description></item>
/// <item><description>
/// A flow did not travel with a git-native workspace — the one artifact most
/// likely to encode a team's shared regression path stayed in one person's
/// browser profile, unreviewable in a pull request.
/// </description></item>
/// <item><description>
/// A flow was outside the per-identity slot (#97), so it did not migrate, did
/// not archive on deactivation, and was not purged with the rest.
/// </description></item>
/// </list>
/// <para>
/// The shape deliberately mirrors <see cref="CollectionStore"/> down to the
/// method signatures: one rule for where a workspace's artifacts live is worth
/// more than a store that is individually clever.
/// </para>
/// </remarks>
internal static class FlowStore
{
    private static string? _testStorePathOverride;

    /// <summary>
    /// The legacy, workspace-less location. Kept for the same reason
    /// collections keep theirs: a caller that names no workspace is either the
    /// CLI or a host that predates workspaces, and neither should be handed
    /// somebody's workspace file.
    /// </summary>
    internal static string StorePath
    {
        get => _testStorePathOverride ?? BowireUserContext.GetUserPath("flows.json");
        set => _testStorePathOverride = value;
    }

    private static readonly Lock FileLock = new();

    private const string EmptyEnvelope = """{"flows":[]}""";

    /// <summary>Where this workspace's flows live.</summary>
    /// <remarks>
    /// With a <paramref name="storageRoot"/> that is the git-native workspace's
    /// own directory, so the file lands in the repository and travels with a
    /// clone. Without one it is the workspace's folder under the identity's
    /// slot.
    /// </remarks>
    private static string ResolvePath(string? workspaceId, string? storageRoot)
    {
        if (_testStorePathOverride is not null
            && string.IsNullOrWhiteSpace(workspaceId)
            && string.IsNullOrWhiteSpace(storageRoot))
        {
            return _testStorePathOverride;
        }

        if (string.IsNullOrWhiteSpace(workspaceId) && string.IsNullOrWhiteSpace(storageRoot))
            return StorePath;

        return BowireUserContext.GetWorkspacePath(
            workspaceId: workspaceId ?? string.Empty,
            storageRoot: storageRoot,
            relativePath: "flows.json");
    }

    /// <summary>Load the legacy, workspace-less document.</summary>
    public static string Load() => Load(null, null);

    /// <summary>
    /// Load this workspace's flows.
    /// </summary>
    /// <remarks>
    /// A workspace that has never saved returns the empty envelope rather than
    /// inheriting the legacy global file. Handing one workspace's flows to the
    /// first other workspace that happens to look is the cross-workspace bleed
    /// the collection store already learned not to do.
    /// </remarks>
    public static string Load(string? workspaceId, string? storageRoot = null)
    {
        var path = ResolvePath(workspaceId, storageRoot);

        lock (FileLock)
        {
            try
            {
                if (!File.Exists(path)) return EmptyEnvelope;

                var json = File.ReadAllText(path);
                // Parse to validate: a corrupt file yields the empty envelope
                // so the workbench still opens, rather than an error page in
                // front of everything else the person has.
                using var _ = JsonDocument.Parse(json);
                return json;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return EmptyEnvelope;
            }
        }
    }

    /// <summary>Persist to the legacy, workspace-less location.</summary>
    public static void Save(string json) => Save(json, null, null);

    /// <summary>
    /// Persist this workspace's flows verbatim, creating the parent directory
    /// on the way.
    /// </summary>
    /// <remarks>
    /// Validated before writing: a malformed PUT must not be able to leave a
    /// file behind that the next load has to recover from.
    /// </remarks>
    public static void Save(string json, string? workspaceId, string? storageRoot = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("JSON payload required", nameof(json));

        using var _ = JsonDocument.Parse(json);

        var path = ResolvePath(workspaceId, storageRoot);

        lock (FileLock)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
    }
}
