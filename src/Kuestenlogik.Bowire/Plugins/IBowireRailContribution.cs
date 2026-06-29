// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// Descriptor contributed by a package that wants to add a rail (a left-strip
/// activity-bar icon + its associated sidebar / main-pane view) to the Bowire
/// workbench.
/// </summary>
/// <remarks>
/// <para>
/// Rails are the top-level navigation primitives in the workbench shell:
/// Discover, Workspaces, Recordings, Mocks, Flows, Proxy, Benchmarks, Security,
/// &amp;c. Each rail used to be hardcoded into the JS bundle. With #294 every
/// rail goes through this descriptor so embedded hosts can pick the rails
/// they ship — drop the Security NuGet, the Security rail disappears from
/// the strip; drop AI, the Assistant module unwires itself.
/// </para>
/// <para>
/// The descriptor is intentionally narrow. It carries enough metadata for the
/// JS renderer (id, label, icon, sort, group, default-enabled) to draw the
/// rail icon + register the route, but the actual sidebar / main-pane DOM
/// stays inside the existing per-feature JS module the rail bundles with —
/// the descriptor only opts the rail into the strip + Settings → Rail modes.
/// </para>
/// <para>
/// Implementations are auto-discovered by <see cref="BowireRailRegistry.Discover"/>
/// when the host invokes <see cref="BowireServiceCollectionExtensions.AddBowire(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>;
/// hosts can also register a descriptor explicitly via
/// <c>services.AddBowireRail&lt;TRail&gt;()</c>.
/// </para>
/// </remarks>
public interface IBowireRailContribution
{
    /// <summary>
    /// Stable identifier (e.g. <c>"discover"</c>, <c>"recordings"</c>,
    /// <c>"security"</c>). Must match the rail-mode id the JS bundle
    /// uses for routing — operators' <c>localStorage.bowire_rail_mode</c>
    /// values + deep links key off this. Case-sensitive (snake-lower).
    /// </summary>
    string Id { get; }

    /// <summary>Human-readable label shown in the rail tooltip + Settings.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Icon key from the workbench's SVG-icon catalogue (e.g.
    /// <c>"house"</c>, <c>"discover"</c>, <c>"shield"</c>). Resolved
    /// JS-side via <c>svgIcon(key)</c>. Unknown keys fall back to a
    /// generic square placeholder.
    /// </summary>
    string IconKey { get; }

    /// <summary>
    /// Sort priority. Lower values appear higher in the rail strip.
    /// The built-in catalogue uses 100-step intervals so third-party
    /// rails can wedge between two built-ins without re-numbering
    /// everything.
    /// </summary>
    int SortIndex { get; }

    /// <summary>
    /// Visual group the rail belongs to. Adjacent rails with different
    /// group values get a divider between them. Built-in groups:
    /// <c>"work"</c>, <c>"scenarios"</c>, <c>"quality"</c>,
    /// <c>"hardening"</c>. New groups land at the bottom of the rail
    /// by default.
    /// </summary>
    string Group { get; }

    /// <summary>
    /// Sidebar template the rail renders. Recognised values: <c>"none"</c>,
    /// <c>"services"</c>, <c>"collections"</c>, <c>"environments"</c>,
    /// <c>"recordings"</c>, <c>"mocks"</c>, <c>"workspaces"</c>,
    /// <c>"sources"</c>, <c>"benchmarks"</c>, <c>"flows"</c>,
    /// <c>"proxy"</c>, <c>"security"</c>, <c>"library"</c>. Adding a
    /// new value requires a matching arm in render-sidebar.js's
    /// dispatcher — see the <c>sidebar.kind</c> comment block in that
    /// file.
    /// </summary>
    string SidebarKind { get; }

    /// <summary>
    /// Whether the rail is part of the always-on minimum set. Always-on
    /// rails cannot be disabled via Settings → Rail modes (the checkbox
    /// renders greyed with a "Built-in" badge). Set <c>false</c> for
    /// every rail the operator might reasonably want to switch off.
    /// </summary>
    bool DefaultEnabled => true;

    /// <summary>
    /// Whether the rail is part of the locked always-on set. Differs
    /// from <see cref="DefaultEnabled"/>: an always-on rail also can't
    /// be disabled at all. Defaults to <c>false</c>; only the four core
    /// rails (Home, Discover, Compose, Workspaces) ship as always-on.
    /// </summary>
    bool AlwaysOn => false;

    /// <summary>
    /// When <c>true</c>, the rail is in the catalogue (so routing still
    /// works when another rail's tree dispatches into it — e.g.
    /// Collections / Environments dispatched from a workspace) but no
    /// dedicated rail-strip icon renders. Defaults to <c>false</c>.
    /// </summary>
    bool HideFromRail => false;

    /// <summary>
    /// When <c>true</c>, the rail's view only makes sense inside an active
    /// workspace (Recordings / Mocks / Collections / Flows / Benchmarks /
    /// Compose — anything that persists artefacts to a workspace folder).
    /// The rail button stays clickable (so tour spotlighting + the rail
    /// strip's tab semantics keep working) but a click without an active
    /// workspace redirects the operator to the Home rail, where the
    /// "Create your first workspace" CTA lives, and fires an explanatory
    /// toast. Defaults to <c>false</c> — rails that work standalone
    /// (Home, Discover, Traffic, Workspaces, Security, Settings) stay
    /// reachable at all times.
    /// </summary>
    bool RequiresWorkspace => false;

    /// <summary>
    /// Identifier of a JS-side function the rail package's JS fragment
    /// registers on <c>window.__bowireRailRenderers</c> at load time.
    /// The core <c>renderSidebar</c> dispatcher looks up the renderer by
    /// id and invokes it instead of hard-coding the per-rail branch.
    /// Empty / <c>null</c> means "no rail-owned renderer; fall back to
    /// the core dispatcher arm" — so the slice can be moved
    /// incrementally rail-by-rail without breaking the bundle.
    /// </summary>
    /// <remarks>
    /// Convention: <c>railId + 'Sidebar'</c> (e.g. <c>"recordingsSidebar"</c>,
    /// <c>"proxySidebar"</c>). The hosted JS fragment writes
    /// <c>window.__bowireRailRenderers["recordingsSidebar"] = function () { ... };</c>
    /// inside the shared IIFE. The renderer takes no arguments and
    /// returns the DOM root to mount as the sidebar — same contract as
    /// the legacy <c>renderRecordingsSidebar()</c> &amp;c. arms.
    /// </remarks>
    string? SidebarRendererKey => null;

    /// <summary>
    /// Identifier of a JS-side function the rail package's JS fragment
    /// registers on <c>window.__bowireRailRenderers</c> at load time.
    /// The core <c>renderMain</c> dispatcher looks up the renderer by
    /// id and invokes it instead of hard-coding the per-rail branch.
    /// Empty / <c>null</c> means "no rail-owned renderer; fall back to
    /// the core dispatcher arm".
    /// </summary>
    /// <remarks>
    /// Convention: <c>railId + 'Main'</c> (e.g. <c>"recordingsMain"</c>,
    /// <c>"benchmarksMain"</c>). The hosted JS fragment writes
    /// <c>window.__bowireRailRenderers["recordingsMain"] = function () { ... };</c>
    /// inside the shared IIFE. The renderer takes no arguments and
    /// returns the DOM root for the main pane.
    /// </remarks>
    string? MainPaneRendererKey => null;
}
