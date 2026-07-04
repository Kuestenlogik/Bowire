// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Recordings;

/// <summary>
/// Recordings rail contribution (#306 Phase G).
/// </summary>
public sealed class BowireRecordingsRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "recordings";
    /// <inheritdoc />
    public string DisplayName => "Recordings";
    /// <inheritdoc />
    public string IconKey => "recording";
    /// <inheritdoc />
    public int SortIndex => 600;
    /// <inheritdoc />
    public string Group => "scenarios";
    /// <inheritdoc />
    public string SidebarKind => "recordings";

    // #306 / #314 — renderer-key seam: core resolves these from
    // window.__bowireRailRenderers (registered by recording.js, which now
    // owns the sidebar + main renderers) instead of hardcoded railMode /
    // switch arms.
    /// <inheritdoc />
    public string? SidebarRendererKey => "recordingsSidebar";
    /// <inheritdoc />
    public string? MainPaneRendererKey => "recordingsMain";

    /// <inheritdoc />
    // Recordings persist into the active workspace; without one the
    // rail has nowhere to store its artefacts.
    public bool RequiresWorkspace => true;
}
