// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Mock.Management;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// Adapter that lets the Mock package's
/// <see cref="BowireMockManagementEndpoints"/> resolve recordings
/// without taking a hard reference on the workbench's internal
/// recording stores. Standalone tool registers this at startup (#94,
/// consolidated under #223).
/// </summary>
/// <remarks>
/// Resolution order:
/// <list type="number">
///   <item>Walk every per-workspace directory under
///     <c>~/.bowire/workspaces/&lt;wsId&gt;/recordings/</c> via
///     <see cref="ChunkedRecordingStore.LoadAll(string?, bool, string?)"/>
///     until a recording with the matching id surfaces. The mock
///     host endpoint doesn't carry a workspaceId today (one of the
///     fields a future "use as mock" flow could plumb through), so
///     we discover the workspace the recording belongs to by
///     scanning. Cheap for the typical 1-5 workspaces a user has,
///     and avoids the "but which workspace?" coupling at the call
///     site.</item>
///   <item>Fall back to the legacy unscoped
///     <see cref="RecordingStore"/> at <c>~/.bowire/recordings.json</c>
///     so v1.x recordings (or any host that hasn't yet adopted the
///     workspace-scoped layout) still light up the mock.</item>
/// </list>
/// </remarks>
internal sealed class WorkbenchRecordingJsonProvider : IRecordingJsonProvider
{
    public string? TryGetRecordingJson(string recordingId)
    {
        // 1) Try every workspace's chunked store. Match by recording id
        //    inside the loaded envelope. First match wins.
        foreach (var wsId in EnumerateWorkspaceIds())
        {
            string envelope;
            try { envelope = ChunkedRecordingStore.LoadAll(wsId, manifestOnly: false, storageRoot: null); }
            catch { continue; }
            if (TryExtractMatchingRecording(envelope, recordingId) is { } match)
            {
                return match;
            }
        }

        // 2) Legacy unscoped fallback.
        var legacyEnvelope = RecordingStore.Load();
        return TryExtractMatchingRecording(legacyEnvelope, recordingId);
    }

    /// <summary>
    /// Workspace directories live under
    /// <c>BowireUserContext.GetUserPath("workspaces")</c>. Each sub-
    /// directory is a workspace id. Missing parent or read errors
    /// return an empty list so the provider degrades to the legacy
    /// store instead of throwing.
    /// </summary>
    private static IEnumerable<string> EnumerateWorkspaceIds()
    {
        string root;
        try { root = BowireUserContext.GetUserPath("workspaces"); }
        catch { yield break; }
        if (!Directory.Exists(root)) yield break;
        string[] entries;
        try { entries = Directory.GetDirectories(root); }
        catch { yield break; }
        foreach (var dir in entries)
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrWhiteSpace(name)) yield return name;
        }
    }

    private static string? TryExtractMatchingRecording(string envelope, string recordingId)
    {
        try
        {
            using var doc = JsonDocument.Parse(envelope);
            if (!doc.RootElement.TryGetProperty("recordings", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            foreach (var rec in arr.EnumerateArray()
                         .Where(rec => rec.TryGetProperty("id", out var idProp) &&
                                       idProp.ValueKind == JsonValueKind.String &&
                                       string.Equals(idProp.GetString(), recordingId, StringComparison.Ordinal)))
            {
                return rec.GetRawText();
            }
        }
        catch (JsonException)
        {
            // Malformed envelope — skip + let the caller try the next source.
        }
        return null;
    }
}
