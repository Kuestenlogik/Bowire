// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Rail contribution for the Security workbench surface (#294 Phase D).
/// </summary>
/// <remarks>
/// <para>
/// First rail extracted out of the core <c>Kuestenlogik.Bowire</c> assembly
/// and into its own NuGet package — proves the pluggable-workbench path
/// end to end. Embedded hosts that don't reference
/// <c>Kuestenlogik.Bowire.Security.Scanner</c> simply don't get the
/// shield icon in the rail strip and don't get the Security pane in
/// Settings → Rail modes (the descriptor was never discovered, so the
/// rail isn't in the catalogue at all).
/// </para>
/// <para>
/// The actual sidebar / main-pane code still lives in <c>render-sidebar.js</c>
/// / <c>render-main.js</c> for now — only the descriptor moved. Phase G
/// (follow-up ticket) will hoist the JS too, but doing it descriptor-first
/// already gives embedded hosts the package-drop ergonomics.
/// </para>
/// </remarks>
public sealed class BowireSecurityRailContribution : IBowireRailContribution
{
    public string Id => "security";
    public string DisplayName => "Security";
    public string IconKey => "shield";
    public int SortIndex => 1100;
    public string Group => "hardening";
    public string SidebarKind => "security";
}
