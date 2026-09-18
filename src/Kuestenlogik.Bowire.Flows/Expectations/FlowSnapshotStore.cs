// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Flows.Expectations;

/// <summary>
/// Where a flow's snapshot baselines live, and how to read and write one
/// (#171).
/// </summary>
/// <remarks>
/// <para>
/// Beside <see cref="FlowSnapshotComparer"/> for the same reason that one is
/// here: the CLI runner and the workbench's approve action have to agree, or
/// a baseline approved in the browser is not the file CI later compares
/// against — and nobody finds out until a green run says nothing.
/// </para>
/// <para>
/// Baselines sit in <c>__snapshots__/</c> beside the flow file and are
/// checked in with it, so drift shows up in the diff of the change that
/// caused it. That is the Jest convention, which a reviewer already knows
/// how to read.
/// </para>
/// </remarks>
public static class FlowSnapshotStore
{
    /// <summary>The directory name baselines are collected under.</summary>
    public const string DirectoryName = "__snapshots__";

    private const string FileSuffix = ".snap.json";

    /// <summary>
    /// The directory holding one flow's baselines.
    /// </summary>
    /// <param name="flowPath">The file the flow was read from.</param>
    /// <param name="flowId">
    /// The flow's id when <paramref name="flowPath"/> is a workspace envelope
    /// holding several flows; null when the file is one flow.
    /// </param>
    /// <remarks>
    /// <para>
    /// Keyed by the file's stem for a file that is one flow. That is the
    /// export format, and where every baseline already checked in lives.
    /// </para>
    /// <para>
    /// Keyed by flow id inside an envelope, because there every flow shares
    /// the stem and step ids are only unique within a flow. Two flows that
    /// both name a step <c>n1</c> shared one baseline file, and the failure
    /// was silent — the second flow's response overwrote the first flow's
    /// truth, and the next run compared against it and passed.
    /// </para>
    /// </remarks>
    public static string DirectoryFor(string flowPath, string? flowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowPath);

        var dir = Path.GetDirectoryName(Path.GetFullPath(flowPath)) ?? ".";
        var key = string.IsNullOrWhiteSpace(flowId)
            ? Path.GetFileNameWithoutExtension(flowPath)
            : Safe(flowId);
        return Path.Combine(dir, DirectoryName, key);
    }

    /// <summary>
    /// The directory holding the baselines of a flow in a saved workspace.
    /// </summary>
    /// <param name="workspaceId">The workspace the flow belongs to.</param>
    /// <param name="storageRoot">
    /// The checkout, for a git-native workspace; null otherwise.
    /// </param>
    /// <param name="flowId">The flow.</param>
    /// <remarks>
    /// Resolved through the same seam the stores use, so a git-native
    /// workspace's baselines land in the repository and travel with a clone —
    /// which is the whole point of checking them in.
    /// </remarks>
    public static string DirectoryForWorkspace(string workspaceId, string? storageRoot, string flowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);

        var flowsFile = BowireUserContext.GetWorkspacePath(workspaceId, storageRoot, "flows.json");
        return DirectoryFor(flowsFile, flowId);
    }

    /// <summary>The baseline file for one step.</summary>
    public static string FileFor(string snapshotDirectory, string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);

        return Path.Combine(snapshotDirectory, Safe(stepId) + FileSuffix);
    }

    /// <summary>
    /// The baseline for one step, or <c>null</c> when none has been captured.
    /// </summary>
    public static async Task<string?> ReadAsync(
        string snapshotDirectory, string stepId, CancellationToken ct = default)
    {
        var file = FileFor(snapshotDirectory, stepId);
        try
        {
            return File.Exists(file)
                ? await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable reads as "not captured": the caller then offers to
            // capture one, which is a better answer than an error about a
            // file the operator never edits by hand.
            _ = ex;
            return null;
        }
    }

    /// <summary>Make <paramref name="body"/> the baseline for one step.</summary>
    /// <returns>The file it was written to.</returns>
    public static async Task<string> WriteAsync(
        string snapshotDirectory, string stepId, string body, CancellationToken ct = default)
    {
        var file = FileFor(snapshotDirectory, stepId);
        Directory.CreateDirectory(snapshotDirectory);
        await File.WriteAllTextAsync(file, body ?? string.Empty, ct).ConfigureAwait(false);
        return file;
    }

    /// <summary>
    /// One path segment, whatever the id was.
    /// </summary>
    /// <remarks>
    /// Flow and step ids are read off a file on disk, so they are not
    /// automatically safe to put in a path. Flattened rather than rejected:
    /// an id that cannot be filed is still a flow somebody wants to run.
    /// </remarks>
    private static string Safe(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(id.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
