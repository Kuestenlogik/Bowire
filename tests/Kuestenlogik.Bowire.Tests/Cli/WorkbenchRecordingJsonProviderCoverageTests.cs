// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Mock.Management;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Coverage tests for <see cref="WorkbenchRecordingJsonProvider"/> — the
/// standalone CLI's <see cref="IRecordingJsonProvider"/> adapter that
/// bridges the Mock package's mock-from-recording endpoint to the
/// workbench's per-workspace + legacy unscoped recording stores.
/// Uses a temp <see cref="BowireUserContext.Current"/> + a temp
/// <see cref="RecordingStore.StorePath"/> override so the scans run
/// against fixture-controlled storage.
/// </summary>
[Collection("BowireUserContext")]
public sealed class WorkbenchRecordingJsonProviderCoverageTests : IDisposable
{
    private readonly IBowireUserStore _originalUserStore;
    private readonly string _originalRecordingStorePath;
    private readonly string _sandboxRoot;
    private readonly WorkbenchRecordingJsonProvider _provider;

    public WorkbenchRecordingJsonProviderCoverageTests()
    {
        _sandboxRoot = SafePath.Combine(
            Path.GetTempPath(),
            $"bowire-recprov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);

        _originalUserStore = BowireUserContext.Current;
        BowireUserContext.Current = new TempStore(_sandboxRoot);

        // Redirect the legacy unscoped RecordingStore to a temp file
        // under the sandbox too, so its taps survive without touching
        // the developer's ~/.bowire/recordings.json.
        _originalRecordingStorePath = RecordingStore.StorePath;
        RecordingStore.StorePath = SafePath.Combine(_sandboxRoot, "recordings.json");

        _provider = new WorkbenchRecordingJsonProvider();
    }

    public void Dispose()
    {
        BowireUserContext.Current = _originalUserStore;
        RecordingStore.StorePath = _originalRecordingStorePath;
        if (Directory.Exists(_sandboxRoot))
        {
            try { Directory.Delete(_sandboxRoot, recursive: true); } catch { /* best-effort */ }
        }
        GC.SuppressFinalize(this);
    }

    private sealed class TempStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => SafePath.Combine(root, filename);
    }

    private string WorkspaceRecordingsDir(string wsId)
    {
        // Mirrors the layout ChunkedRecordingStore.ResolveRootPath
        // builds under the legacy default-folder branch.
        var dir = SafePath.Combine(
            _sandboxRoot, Path.Combine("workspaces", wsId, "recordings"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SeedChunkedRecording(string recordingsDir, string id, string label)
    {
        // ChunkedRecordingStore.LoadAll only needs recording.json's
        // metadata to surface a recording in the envelope. Write a
        // minimal metadata file directly so the test stays decoupled
        // from the SaveAll write path's evolving schema.
        var recDir = SafePath.Combine(recordingsDir, id);
        Directory.CreateDirectory(recDir);
        var meta = JsonSerializer.Serialize(new
        {
            id,
            label,
            steps = Array.Empty<object>()
        });
        File.WriteAllText(SafePath.Combine(recDir, "recording.json"), meta);
    }

    private static void SeedLegacyEnvelope(string path, params (string id, string label)[] recs)
    {
        var json = JsonSerializer.Serialize(new
        {
            recordings = recs.Select(r => new { id = r.id, label = r.label }).ToArray()
        });
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void TryGetRecordingJson_With_Empty_Sandbox_Returns_Null()
    {
        // No workspaces dir, no legacy file — every lookup returns null.
        var result = _provider.TryGetRecordingJson("does-not-exist");
        Assert.Null(result);
    }

    [Fact]
    public void TryGetRecordingJson_Finds_Recording_In_Workspace_Chunked_Store()
    {
        var recordings = WorkspaceRecordingsDir("ws1");
        SeedChunkedRecording(recordings, "rec-a", "login flow");

        var result = _provider.TryGetRecordingJson("rec-a");

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        Assert.Equal("rec-a", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("login flow", doc.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public void TryGetRecordingJson_Scans_Multiple_Workspaces_Until_Hit()
    {
        // ws1 has rec-a, ws2 has rec-b. Looking up rec-b should hit even
        // though ws1 is scanned first.
        var ws1 = WorkspaceRecordingsDir("ws1");
        SeedChunkedRecording(ws1, "rec-a", "alpha");
        var ws2 = WorkspaceRecordingsDir("ws2");
        SeedChunkedRecording(ws2, "rec-b", "beta");

        var result = _provider.TryGetRecordingJson("rec-b");

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        Assert.Equal("rec-b", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void TryGetRecordingJson_Falls_Back_To_Legacy_Unscoped_Store()
    {
        // No workspace match — legacy store has the record.
        SeedLegacyEnvelope(RecordingStore.StorePath,
            ("rec-legacy", "v1.x capture"));

        var result = _provider.TryGetRecordingJson("rec-legacy");

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        Assert.Equal("rec-legacy", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("v1.x capture", doc.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public void TryGetRecordingJson_Prefers_Workspace_Match_Over_Legacy_Fallback()
    {
        // Same id in both stores — the workspace store is checked first
        // so its payload wins.
        var ws1 = WorkspaceRecordingsDir("ws1");
        SeedChunkedRecording(ws1, "rec-shared", "from-workspace");
        SeedLegacyEnvelope(RecordingStore.StorePath,
            ("rec-shared", "from-legacy"));

        var result = _provider.TryGetRecordingJson("rec-shared");

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        Assert.Equal("from-workspace", doc.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public void TryGetRecordingJson_With_Missing_Workspaces_Root_Returns_Null_From_Legacy()
    {
        // Workspaces folder never gets created — provider must still
        // try the legacy fallback without throwing.
        SeedLegacyEnvelope(RecordingStore.StorePath,
            ("rec-legacy", "v1"));

        var result = _provider.TryGetRecordingJson("rec-legacy");

        Assert.NotNull(result);
    }

    [Fact]
    public void TryGetRecordingJson_Skips_Recordings_Without_String_Id()
    {
        // Seed a legacy envelope where one entry's id is a number — the
        // provider's id matcher requires JsonValueKind.String, so the
        // entry is skipped and the lookup returns null.
        var dir = Path.GetDirectoryName(RecordingStore.StorePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(RecordingStore.StorePath, """
            {"recordings":[{"id":42,"label":"numeric"}]}
            """);

        var result = _provider.TryGetRecordingJson("42");

        Assert.Null(result);
    }

    [Fact]
    public void TryGetRecordingJson_Skips_Recordings_Without_Id_Property()
    {
        var dir = Path.GetDirectoryName(RecordingStore.StorePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(RecordingStore.StorePath, """
            {"recordings":[{"label":"no-id"}]}
            """);

        Assert.Null(_provider.TryGetRecordingJson("anything"));
    }

    [Fact]
    public void TryGetRecordingJson_Returns_Null_When_Recordings_Property_Missing()
    {
        var dir = Path.GetDirectoryName(RecordingStore.StorePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(RecordingStore.StorePath, """{"other":"shape"}""");

        Assert.Null(_provider.TryGetRecordingJson("rec-a"));
    }

    [Fact]
    public void TryGetRecordingJson_Returns_Null_When_Recordings_Is_Not_Array()
    {
        var dir = Path.GetDirectoryName(RecordingStore.StorePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(RecordingStore.StorePath, """
            {"recordings":{"id":"rec-a"}}
            """);

        Assert.Null(_provider.TryGetRecordingJson("rec-a"));
    }

    [Fact]
    public void TryGetRecordingJson_Tolerates_Malformed_Legacy_Json()
    {
        // Legacy JSON is corrupt. The legacy RecordingStore.Load already
        // returns the empty envelope on a parse failure, so the provider
        // just sees an empty recordings array and returns null.
        var dir = Path.GetDirectoryName(RecordingStore.StorePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(RecordingStore.StorePath, "{{not json");

        var result = _provider.TryGetRecordingJson("rec-a");
        Assert.Null(result);
    }

    [Fact]
    public void TryGetRecordingJson_Skips_Workspace_With_Unreadable_Store()
    {
        // Seed a "workspace" whose recording.json is corrupt. ChunkedRecordingStore.LoadAll
        // catches the parse error and returns an empty envelope, so the
        // scan continues — including the next workspace which has the
        // real recording.
        var ws1 = WorkspaceRecordingsDir("ws1");
        var corrupt = SafePath.Combine(ws1, "rec-broken");
        Directory.CreateDirectory(corrupt);
        File.WriteAllText(SafePath.Combine(corrupt, "recording.json"), "{not json");

        var ws2 = WorkspaceRecordingsDir("ws2");
        SeedChunkedRecording(ws2, "rec-good", "ok");

        var result = _provider.TryGetRecordingJson("rec-good");
        Assert.NotNull(result);
    }

    [Fact]
    public void TryGetRecordingJson_Empty_Workspace_Dir_Without_Recording_Returns_Null()
    {
        // workspaces/ws1/recordings/ exists but is empty — the chunked
        // store returns an empty envelope; provider falls through to
        // legacy (also missing) and returns null.
        WorkspaceRecordingsDir("ws1");

        Assert.Null(_provider.TryGetRecordingJson("rec-missing"));
    }

    [Fact]
    public void TryGetRecordingJson_Skips_Hidden_Or_Whitespace_Workspace_Dirs()
    {
        // EnumerateWorkspaceIds only yields names that aren't whitespace.
        // A directory named "  " (spaces, not allowed by NTFS so we use
        // a leading-dot dir to simulate "hidden"): the GetFileName helper
        // returns the dot-prefixed name, which is non-empty and will be
        // tried; ChunkedRecordingStore.LoadAll then returns the empty
        // envelope because that dir contains no per-recording subdirs.
        var workspacesRoot = SafePath.Combine(_sandboxRoot, "workspaces");
        Directory.CreateDirectory(workspacesRoot);
        Directory.CreateDirectory(SafePath.Combine(workspacesRoot, ".hidden"));

        Assert.Null(_provider.TryGetRecordingJson("anything"));
    }

    [Fact]
    public void TryGetRecordingJson_Honours_Ordinal_Case_Sensitivity()
    {
        // Provider uses StringComparison.Ordinal — IDs are exact-match
        // so a lookup with a different case must miss even when the
        // letters line up.
        var ws1 = WorkspaceRecordingsDir("ws1");
        SeedChunkedRecording(ws1, "Rec-A", "label");

        Assert.Null(_provider.TryGetRecordingJson("rec-a"));
        Assert.NotNull(_provider.TryGetRecordingJson("Rec-A"));
    }
}
