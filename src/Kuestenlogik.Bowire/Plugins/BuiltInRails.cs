// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Plugins;

// Built-in rail contributions. Each rail that historically lived in the
// hardcoded `_railModes` array in render-sidebar.js now ships as a
// descriptor here. The C# registry is the single source of truth — the
// JS bundle reads __BOWIRE_CONFIG__.rails to populate its catalogue.
//
// Order of definition doesn't matter — BowireRailRegistry sorts by
// SortIndex. The numeric values use 100-step intervals so third-party
// rails can wedge in between two built-ins without renumbering anything
// existing.
//
// Phase D / G will move individual rails (Security first) into their
// own NuGet packages. The class structure here is intentionally the
// same shape a per-package descriptor will take, so the move is a
// `mv RailHome.cs -> Kuestenlogik.Bowire.Rail.Home/` — no code change.

/// <summary>Home rail (cross-workflow landing surface).</summary>
public sealed class BowireHomeRailContribution : IBowireRailContribution
{
    public string Id => "home";
    public string DisplayName => "Home";
    public string IconKey => "house";
    public int SortIndex => 100;
    public string Group => "work";
    public string SidebarKind => "none";
    public bool AlwaysOn => true;
}

/// <summary>Discover rail (schema-driven services tree).</summary>
public sealed class BowireDiscoverRailContribution : IBowireRailContribution
{
    public string Id => "discover";
    public string DisplayName => "Discover";
    public string IconKey => "discover";
    public int SortIndex => 200;
    public string Group => "work";
    public string SidebarKind => "services";
    public bool AlwaysOn => true;
}

/// <summary>
/// Compose rail — home for the ad-hoc Request Builder (#293).
/// </summary>
public sealed class BowireComposeRailContribution : IBowireRailContribution
{
    public string Id => "compose";
    public string DisplayName => "Compose";
    public string IconKey => "drill";
    public int SortIndex => 300;
    public string Group => "work";
    public string SidebarKind => "none";
    public bool AlwaysOn => true;
}

/// <summary>
/// Collections rail — hidden from the rail strip (collections are
/// managed inside their workspace) but kept in the catalogue so the
/// workspace tree can dispatch into the existing collections UI.
/// </summary>
public sealed class BowireCollectionsRailContribution : IBowireRailContribution
{
    public string Id => "collections";
    public string DisplayName => "Collections";
    public string IconKey => "folder";
    public int SortIndex => 400;
    public string Group => "work";
    public string SidebarKind => "collections";
    public bool HideFromRail => true;
}

/// <summary>
/// Environments rail — same hideFromRail story as Collections.
/// </summary>
public sealed class BowireEnvironmentsRailContribution : IBowireRailContribution
{
    public string Id => "environments";
    public string DisplayName => "Environments";
    public string IconKey => "globe";
    public int SortIndex => 500;
    public string Group => "work";
    public string SidebarKind => "environments";
    public bool HideFromRail => true;
}

/// <summary>Recordings rail.</summary>
public sealed class BowireRecordingsRailContribution : IBowireRailContribution
{
    public string Id => "recordings";
    public string DisplayName => "Recordings";
    public string IconKey => "recording";
    public int SortIndex => 600;
    public string Group => "scenarios";
    public string SidebarKind => "recordings";
}

/// <summary>Mocks rail.</summary>
public sealed class BowireMocksRailContribution : IBowireRailContribution
{
    public string Id => "mocks";
    public string DisplayName => "Mocks";
    public string IconKey => "mock";
    public int SortIndex => 700;
    public string Group => "scenarios";
    public string SidebarKind => "mocks";
}

/// <summary>Flows rail.</summary>
public sealed class BowireFlowsRailContribution : IBowireRailContribution
{
    public string Id => "flows";
    public string DisplayName => "Flows";
    public string IconKey => "flow";
    public int SortIndex => 800;
    public string Group => "scenarios";
    public string SidebarKind => "flows";
}

/// <summary>Proxy / MITM rail.</summary>
public sealed class BowireProxyRailContribution : IBowireRailContribution
{
    public string Id => "proxy";
    public string DisplayName => "Proxy / MITM";
    public string IconKey => "disconnect";
    public int SortIndex => 900;
    public string Group => "quality";
    public string SidebarKind => "proxy";
}

/// <summary>Benchmarks rail.</summary>
public sealed class BowireBenchmarksRailContribution : IBowireRailContribution
{
    public string Id => "benchmarks";
    public string DisplayName => "Benchmarks";
    public string IconKey => "chart";
    public int SortIndex => 1000;
    public string Group => "quality";
    public string SidebarKind => "benchmarks";
}

/// <summary>
/// Workspaces rail — always-on (the workspace switcher is the closest
/// thing to a file tree in Bowire and operators expect it on by default).
/// </summary>
public sealed class BowireWorkspacesRailContribution : IBowireRailContribution
{
    public string Id => "workspaces";
    public string DisplayName => "Workspaces";
    public string IconKey => "layers";
    public int SortIndex => 1200;
    public string Group => "hardening";
    public string SidebarKind => "workspaces";
    public bool AlwaysOn => true;
}
