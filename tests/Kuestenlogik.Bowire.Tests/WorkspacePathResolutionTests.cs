// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit coverage for <see cref="BowireUserContext.GetWorkspacePath"/>
/// — the #147 storage-root resolver. The contract:
/// <list type="bullet">
///   <item>When storageRoot is set, the result lives under it verbatim
///     (operator opted into the git-backed layout).</item>
///   <item>When storageRoot is null/empty, the result falls back to the
///     legacy per-user folder under <c>~/.bowire/workspaces/&lt;id&gt;/</c>.</item>
///   <item>An empty workspace id collapses the legacy fallback to the
///     bare <c>workspaces/&lt;relative&gt;</c> bucket so callers that
///     don't carry an id (early-boot paths, shared scripts) still get a
///     stable layout.</item>
/// </list>
/// </summary>
[Collection("BowireUserContext")]
public sealed class WorkspacePathResolutionTests
{
    [Fact]
    public void GetWorkspacePath_with_storageRoot_anchors_under_it()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "fake-workspace-root");
        var result = BowireUserContext.GetWorkspacePath(
            workspaceId: "ws_payments",
            storageRoot: storageRoot,
            relativePath: Path.Combine("environments", "staging.json"));

        Assert.StartsWith(storageRoot, result);
        Assert.Contains("environments", result);
        Assert.EndsWith("staging.json", result);
    }

    [Fact]
    public void GetWorkspacePath_without_storageRoot_falls_back_to_per_user_folder()
    {
        var result = BowireUserContext.GetWorkspacePath(
            workspaceId: "ws_payments",
            storageRoot: null,
            relativePath: Path.Combine("environments", "staging.json"));

        // The result lives under whatever the active IBowireUserStore
        // hands back for "workspaces/ws_payments/environments/staging.json".
        // Assert the structural pieces rather than the absolute path so
        // the test stays portable across OSes and test-store overrides.
        Assert.Contains("workspaces", result);
        Assert.Contains("ws_payments", result);
        Assert.EndsWith("staging.json", result);
    }

    [Fact]
    public void GetWorkspacePath_with_empty_storageRoot_treats_it_as_unset()
    {
        var resultEmpty = BowireUserContext.GetWorkspacePath(
            workspaceId: "ws_x", storageRoot: "", relativePath: "globals.json");
        var resultNull = BowireUserContext.GetWorkspacePath(
            workspaceId: "ws_x", storageRoot: null, relativePath: "globals.json");

        Assert.Equal(resultNull, resultEmpty);
    }

    [Fact]
    public void GetWorkspacePath_with_empty_workspaceId_drops_the_id_segment()
    {
        var result = BowireUserContext.GetWorkspacePath(
            workspaceId: "", storageRoot: null, relativePath: "globals.json");

        // The collapsed form is "workspaces/globals.json" (no empty
        // id segment in between). Asserting the literal Path.Combine
        // result of those two parts keeps the test portable across
        // path separators.
        Assert.EndsWith(Path.Combine("workspaces", "globals.json"), result);
    }

    [Fact]
    public void GetWorkspacePath_rejects_empty_relativePath()
    {
        Assert.Throws<ArgumentException>(() =>
            BowireUserContext.GetWorkspacePath("ws_x", storageRoot: null, relativePath: ""));
    }

    [Fact]
    public void GetWorkspacePath_rejects_absolute_relativePath()
    {
        // Path.Combine silently drops earlier segments when a later
        // segment is rooted — the guard rejects that footgun up-front
        // so callers can't smuggle an absolute path into a
        // workspace-scoped slot.
        var absolute = Path.Combine(Path.GetTempPath(), "evil.json");
        var ex = Assert.Throws<ArgumentException>(() =>
            BowireUserContext.GetWorkspacePath("ws_x", storageRoot: null, relativePath: absolute));
        Assert.Equal("relativePath", ex.ParamName);
    }

    [Fact]
    public void GetWorkspacePath_rejects_absolute_relativePath_even_with_storageRoot()
    {
        // The IsPathRooted check on relativePath fires before the
        // storageRoot branch, so the guard catches an absolute
        // relativePath in both code paths.
        var absolute = Path.Combine(Path.GetTempPath(), "evil.json");
        var storageRoot = Path.Combine(Path.GetTempPath(), "ws-root");
        Assert.Throws<ArgumentException>(() =>
            BowireUserContext.GetWorkspacePath("ws_x", storageRoot, absolute));
    }

    [Fact]
    public void GetWorkspacePath_rejects_absolute_workspaceId()
    {
        // workspaceId becomes a path segment in the legacy fallback —
        // the guard refuses an absolute string so the Path.Combine
        // below doesn't drop the leading "workspaces" anchor.
        var rooted = Path.Combine(Path.GetTempPath(), "wsId");
        var ex = Assert.Throws<ArgumentException>(() =>
            BowireUserContext.GetWorkspacePath(
                workspaceId: rooted,
                storageRoot: null,
                relativePath: "ok.json"));
        Assert.Equal("workspaceId", ex.ParamName);
    }

    [Fact]
    public void GetWorkspacePath_empty_workspaceId_skips_rooted_check()
    {
        // The rooted-workspaceId guard short-circuits on empty, so an
        // empty workspaceId is accepted (collapses the legacy path to
        // the bare "workspaces/<relative>" bucket).
        var result = BowireUserContext.GetWorkspacePath(
            workspaceId: "",
            storageRoot: null,
            relativePath: "shared.json");

        Assert.Contains("workspaces", result, StringComparison.Ordinal);
        Assert.EndsWith("shared.json", result, StringComparison.Ordinal);
    }
}
