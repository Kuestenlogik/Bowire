// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kuestenlogik.Bowire.Workspace.Git;

/// <summary>
/// DI entry-point for the git-backed workspace runtime. Embedded
/// hosts that want the surface call <c>AddBowireGitWorkspace()</c>
/// after <c>AddBowire()</c>; standalone <c>Kuestenlogik.Bowire.Tool</c>
/// references the package and wires it from <c>BowireCli</c>'s host
/// build so the CLI ships the runtime bundled.
/// </summary>
public static class BowireGitWorkspaceServiceCollectionExtensions
{
    /// <summary>
    /// Register the git-backed workspace runtime — per-entity reader/
    /// writer, FileSystemWatcher → SSE producer, secret-overlay merge,
    /// lockfile machinery. The runtime is opt-in so that an embedded
    /// host that only needs the legacy per-user bundle store doesn't
    /// pull <see cref="System.IO.FileSystemWatcher"/>-class
    /// dependencies through transitive references.
    /// </summary>
    public static IServiceCollection AddBowireGitWorkspace(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Singleton so the watcher + lockfile coordinator have one
        // shared view of the active workspaces across every request.
        services.TryAddSingleton<BowireGitWorkspaceExtension>();
        // Phase 2.4 — singleton WorkspaceWatcher fanout. One per
        // process; per-root FileSystemWatchers spin up lazily on
        // first subscribe + tear down with the last unsubscribe so
        // a workspace switch doesn't leak watchers.
        services.TryAddSingleton<WorkspaceWatcher>();

        return services;
    }
}

/// <summary>
/// Marker + activation type for the git-backed workspace runtime.
/// The workbench checks for an <see cref="BowireGitWorkspaceExtension"/>
/// resolved from DI; presence means "the operator wired the runtime,
/// route per-workspace reads/writes through the per-entity layout
/// when <c>workspace.storageRoot</c> is set". Absence keeps
/// <see cref="Kuestenlogik.Bowire.Auth.BowireUserContext.GetWorkspacePath"/>
/// on the legacy per-user fallback.
/// </summary>
/// <remarks>
/// Phase 2.1 ships the activation marker only — the per-entity reader/
/// writer (#148), FS-Watch SSE producer (#150), secret-overlay merge
/// (#151), and lockfile machinery (#151) land as follow-up phases on
/// the same #196 ticket. This keeps the wire-up commit-sized and lets
/// the embedded-mode regression test (verify no FileSystemWatcher
/// deps leak into the core package) move in front of the bulky
/// implementation work.
/// </remarks>
public sealed class BowireGitWorkspaceExtension
{
    // Identity fields stay non-static instance getters so a future
    // phase can vary them by host configuration (e.g. surface a
    // different display name when the package is in
    // read-only/inspection mode). For Phase 2.1 they're effectively
    // constants — the constructor pins the values.

    /// <summary>
    /// Stable identifier used by <c>Settings → Plugins</c> + the
    /// workbench's "git-backed runtime detected" status pill. Matches
    /// the package id so installed-vs-active reads consistently.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Display name surfaced in the workbench's plugin / extension
    /// settings pane.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// One-line summary shown beneath the display name in the same
    /// settings pane.
    /// </summary>
    public string Description { get; }

    public BowireGitWorkspaceExtension()
    {
        Id          = "Kuestenlogik.Bowire.Workspace.Git";
        Name        = "Git-backed workspaces";
        Description = "Per-entity file layout + FS-watch + secret-overlay merge for workspaces backed by a checked-out folder.";
    }
}
