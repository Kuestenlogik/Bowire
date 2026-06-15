// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// Coverage for the #196 Phase 2.3 wiring — per-workspace
/// <c>storageRoot</c> threaded through <see cref="ChunkedRecordingStore"/>.
/// The contract:
/// <list type="bullet">
///   <item>When <c>storageRoot</c> is set, <see cref="ChunkedRecordingStore.ResolveRootPath"/>
///     anchors recordings under <c>&lt;storageRoot&gt;/recordings/</c> verbatim
///     (operator pointed the workspace at a checked-out git folder).</item>
///   <item>When <c>storageRoot</c> is null/empty, the resolver falls back
///     to the legacy per-user <c>workspaces/&lt;id&gt;/recordings</c>
///     layout — installs that never opted in see zero change.</item>
///   <item>A SaveAll → LoadAll round-trip against a real storageRoot
///     writes and reads through the operator-chosen folder, never
///     touching the per-user fallback.</item>
/// </list>
/// </summary>
[Collection("BowireUserContext")]
public sealed class WorkspaceStorageRootTests : IDisposable
{
    private readonly string _storageRoot;
    private readonly string? _originalOverride;

    public WorkspaceStorageRootTests()
    {
        _storageRoot = Directory.CreateTempSubdirectory("bowire-storageroot-").FullName;
        _originalOverride = SafeGetRootOverride();
    }

    public void Dispose()
    {
        // Restore the pre-test override (or clear it). Same handling as
        // ChunkedRecordingStoreTests so we don't smear state between
        // collections.
        if (_originalOverride is null)
        {
            try
            {
                typeof(ChunkedRecordingStore)
                    .GetField("_testRootOverride", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?
                    .SetValue(null, null);
            }
            catch
            {
                // Best-effort.
            }
        }
        else
        {
            ChunkedRecordingStore.RootPath = _originalOverride;
        }

        try { Directory.Delete(_storageRoot, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static string? SafeGetRootOverride()
    {
        try
        {
            return typeof(ChunkedRecordingStore)
                .GetField("_testRootOverride", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?
                .GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    [Fact]
    public void ResolveRootPath_with_storageRoot_anchors_under_it_verbatim()
    {
        var resolved = ChunkedRecordingStore.ResolveRootPath(
            workspaceId: "ws_payments",
            storageRoot: _storageRoot);

        // The contract pins the result to <storageRoot>/recordings.
        // Assert the structural pieces so the test stays portable
        // across path separators and OS canonicalisation quirks.
        Assert.StartsWith(_storageRoot, resolved);
        Assert.EndsWith("recordings", resolved);
        // workspaceId is intentionally NOT a path segment when
        // storageRoot is set — the folder already identifies the
        // workspace, so doubling up would land bytes at
        // <storageRoot>/ws_payments/recordings, which is the wrong
        // layout for a checked-out git folder.
        Assert.DoesNotContain("ws_payments", resolved);
    }

    [Fact]
    public void ResolveRootPath_with_empty_storageRoot_falls_back_to_per_user_folder()
    {
        var withEmpty = ChunkedRecordingStore.ResolveRootPath(
            workspaceId: "ws_payments", storageRoot: "");
        var withNull = ChunkedRecordingStore.ResolveRootPath(
            workspaceId: "ws_payments", storageRoot: null);

        // Empty and null should land in the same place — the legacy
        // per-user fallback under workspaces/<id>/recordings.
        Assert.Equal(withNull, withEmpty);
        Assert.Contains("workspaces", withEmpty);
        Assert.Contains("ws_payments", withEmpty);
        Assert.EndsWith("recordings", withEmpty);
    }

    [Fact]
    public void SaveAll_then_LoadAll_round_trips_through_storageRoot()
    {
        const string Input = """{"recordings":[{"id":"r1","name":"On-disk"}]}""";

        ChunkedRecordingStore.SaveAll(Input,
            workspaceId: "ws_x",
            storageRoot: _storageRoot);

        // The recording lives under <storageRoot>/recordings/r1/, NOT
        // the per-user folder. Assert the on-disk presence directly so
        // a future refactor that silently dropped storageRoot would
        // fail loudly here.
        var expectedRecordingDir = Path.Combine(_storageRoot, "recordings", "r1");
        Assert.True(Directory.Exists(expectedRecordingDir),
            $"Expected the recording at {expectedRecordingDir}");

        var loaded = ChunkedRecordingStore.LoadAll(
            workspaceId: "ws_x",
            storageRoot: _storageRoot);

        using var doc = JsonDocument.Parse(loaded);
        var arr = doc.RootElement.GetProperty("recordings");
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("r1", arr[0].GetProperty("id").GetString());
        Assert.Equal("On-disk", arr[0].GetProperty("name").GetString());
    }

    [Fact]
    public void SaveAll_with_storageRoot_does_not_touch_per_user_folder()
    {
        // Point the per-user folder at a separate temp dir so we can
        // assert the bytes did NOT land there. The override is the
        // documented test seam for redirecting the per-user root away
        // from ~/.bowire/.
        var peruserTemp = Directory.CreateTempSubdirectory("bowire-peruser-").FullName;
        try
        {
            ChunkedRecordingStore.RootPath = peruserTemp;

            const string Input = """{"recordings":[{"id":"r_sr","name":"Anchored"}]}""";
            ChunkedRecordingStore.SaveAll(Input,
                workspaceId: "ws_x",
                storageRoot: _storageRoot);

            // Recording must live under the storageRoot, never under
            // the per-user folder.
            Assert.True(Directory.Exists(Path.Combine(_storageRoot, "recordings", "r_sr")));
            Assert.False(Directory.Exists(Path.Combine(peruserTemp, "workspaces", "ws_x", "recordings", "r_sr")),
                "storageRoot must shadow the per-user fallback entirely.");
        }
        finally
        {
            try { Directory.Delete(peruserTemp, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void BowireUserContext_GetWorkspacePath_underpins_the_resolver()
    {
        // Sanity check: the resolver delegates to BowireUserContext
        // when storageRoot is set, so the same path comes out either
        // way. Pins the contract that ChunkedRecordingStore.ResolveRootPath
        // doesn't shadow GetWorkspacePath with its own layout policy.
        var viaStore = ChunkedRecordingStore.ResolveRootPath(
            workspaceId: "ws_y", storageRoot: _storageRoot);
        var viaContext = BowireUserContext.GetWorkspacePath(
            workspaceId: "ws_y", storageRoot: _storageRoot, relativePath: "recordings");

        Assert.Equal(viaContext, viaStore);
    }
}
