// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rails;

/// <summary>
/// Home rail contribution — cross-workflow landing surface (#306 Phase G;
/// folded into Core in v2.1, #325).
/// </summary>
/// <remarks>
/// The Home rail is descriptor-only — no per-rail JS fragment, no wwwroot
/// resources. Folded into <c>Kuestenlogik.Bowire</c> (Core) in v2.1 so
/// every workbench host gets the rail without an extra package reference;
/// the previous standalone <c>Kuestenlogik.Bowire.Rail.Home</c> package
/// has been retired. The <see cref="Id"/> string remains
/// <c>"home"</c> verbatim so operator-saved <c>railMode</c> +
/// <c>bowire_enabled_rails</c> values continue to dispatch correctly.
/// </remarks>
public sealed class BowireHomeRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "home";
    /// <inheritdoc />
    public string DisplayName => "Home";
    /// <inheritdoc />
    public string IconKey => "house";
    /// <inheritdoc />
    public int SortIndex => 100;
    /// <inheritdoc />
    /// <remarks>
    /// Own 'home' group so the rail-divider logic in render-sidebar.js
    /// draws a separator BELOW Home (the group-change boundary),
    /// visually anchoring it at the top the way Settings is anchored
    /// at the bottom. Operator feedback: 'home sollte ebenso abgesetzt
    /// sein wie einstellungen mit trennstrich'.
    /// </remarks>
    public string Group => "home";
    /// <inheritdoc />
    public string SidebarKind => "none";
    /// <inheritdoc />
    public bool AlwaysOn => true;
}
