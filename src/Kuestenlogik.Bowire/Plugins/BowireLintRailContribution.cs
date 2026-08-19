// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// Rail contribution for the design-time Lint surface (#189). Adds a "Lint"
/// icon to the rail strip in the <c>quality</c> group; the main pane
/// (registered JS-side as <c>lintMain</c>) runs the discovered services through
/// the shared <see cref="Linting.BowireSchemaLinter"/> via <c>POST /api/lint</c>
/// and lists the findings — the browser twin of <c>bowire lint</c>. No sidebar
/// (the findings live in the main pane), and it works without an active
/// workspace, so it stays reachable at all times.
/// </summary>
public sealed class BowireLintRailContribution : IBowireRailContribution
{
    public string Id => "lint";

    public string DisplayName => "Lint";

    // A magnifier over document lines: reading the API surface for design
    // smells. The plain checkmark it used before said nothing about what
    // the rail does, and the Contracts rail carried the same glyph.
    public string IconKey => "inspect";

    public int SortIndex => 900;

    public string Group => "quality";

    public string SidebarKind => "none";

    public string? MainPaneRendererKey => "lintMain";
}
