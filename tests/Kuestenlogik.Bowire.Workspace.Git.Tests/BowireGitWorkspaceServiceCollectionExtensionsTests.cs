// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Workspace.Git;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Workspace.Git.Tests;

/// <summary>
/// Phase 2.1 smoke tests — confirms <c>AddBowireGitWorkspace()</c>
/// registers the activation marker exactly once and that the marker
/// carries the identity fields the workbench reads for the
/// "Settings → Plugins" pane.
/// </summary>
public sealed class BowireGitWorkspaceServiceCollectionExtensionsTests
{
    [Fact]
    public void AddBowireGitWorkspace_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BowireGitWorkspaceServiceCollectionExtensions.AddBowireGitWorkspace(null!));
    }

    [Fact]
    public void AddBowireGitWorkspace_RegistersActivationMarker_AsSingleton()
    {
        var services = new ServiceCollection();
        services.AddBowireGitWorkspace();

        using var sp = services.BuildServiceProvider();
        var first  = sp.GetRequiredService<BowireGitWorkspaceExtension>();
        var second = sp.GetRequiredService<BowireGitWorkspaceExtension>();
        Assert.Same(first, second);
    }

    [Fact]
    public void AddBowireGitWorkspace_Idempotent_AcrossMultipleCalls()
    {
        var services = new ServiceCollection();
        services.AddBowireGitWorkspace();
        services.AddBowireGitWorkspace();
        services.AddBowireGitWorkspace();

        // TryAddSingleton — only the first call registers; subsequent
        // calls are no-ops. The host can call AddBowireGitWorkspace()
        // multiple times without doubling the activation marker.
        using var sp = services.BuildServiceProvider();
        var all = sp.GetServices<BowireGitWorkspaceExtension>().ToList();
        Assert.Single(all);
    }

    [Fact]
    public void Extension_Carries_Stable_Identity_Fields()
    {
        var sut = new BowireGitWorkspaceExtension();
        Assert.Equal("Kuestenlogik.Bowire.Workspace.Git", sut.Id);
        Assert.Equal("Git-backed workspaces", sut.Name);
        Assert.Contains("FS-watch", sut.Description, StringComparison.Ordinal);
        Assert.Contains("secret-overlay", sut.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Embedded_Mode_Without_AddBowireGitWorkspace_Has_No_Activation_Marker()
    {
        // The opt-in shape: a host that doesn't call
        // AddBowireGitWorkspace() must not resolve the marker, so the
        // workbench falls back to the legacy per-user store and never
        // touches FileSystemWatcher / lockfile / secret-overlay
        // machinery. This pins the "core stays unchanged" half of the
        // #196 architecture decision.
        var services = new ServiceCollection();
        using var sp = services.BuildServiceProvider();
        var marker = sp.GetService<BowireGitWorkspaceExtension>();
        Assert.Null(marker);
    }
}
