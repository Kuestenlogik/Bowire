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
}
