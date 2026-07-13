// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Monitoring;

/// <summary>
/// Monitoring rail contribution (#102) — the read-only workbench surface
/// over the probe outcome ledger: live status per probe, sparkline strip,
/// historical outcome table. Probes are authored as files and run by
/// <c>bowire monitor run</c>; the rail never mutates the ledger.
/// </summary>
public sealed class BowireMonitoringRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "monitoring";
    /// <inheritdoc />
    public string DisplayName => "Monitoring";
    /// <inheritdoc />
    public string IconKey => "pulse";
    /// <inheritdoc />
    // Between Benchmarks (1000, quality) and Security (1100, hardening) —
    // passive health sits with the quality surfaces.
    public int SortIndex => 1050;
    /// <inheritdoc />
    public string Group => "quality";
    /// <inheritdoc />
    public string SidebarKind => "monitoring";

    /// <inheritdoc />
    public string? SidebarRendererKey => "monitoringSidebar";
    /// <inheritdoc />
    public string? MainPaneRendererKey => "monitoringMain";

    /// <inheritdoc />
    // The ledger root is machine-global (~/.bowire/monitoring) — probes
    // run and render without any workspace.
    public bool RequiresWorkspace => false;
}
