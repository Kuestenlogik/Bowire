// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// Intercept rail contribution (v2.2 rail-IA refactor). Replaces the
/// previous Mocks + Traffic rails (plus the already-hidden Intercepted +
/// Proxy descriptors) with a single rail whose four sub-tabs cover the
/// whole "what do I do with live traffic" surface.
/// </summary>
/// <remarks>
/// <para>
/// Sub-tabs (locked order):
/// </para>
/// <list type="bullet">
/// <item><description><b>Captured</b> — passive observation of flows
/// captured by <c>UseBowireInterceptor()</c> (was Traffic → "Flows").</description></item>
/// <item><description><b>Live overrides</b> — selective response substitution
/// inside the interceptor pipeline (was Traffic → "Mock Rules").</description></item>
/// <item><description><b>Mock servers</b> — standalone mock-server-from-recording
/// hosts (was the entire Mocks rail). Rendered by the
/// <c>Kuestenlogik.Bowire.Mock</c> package's JS fragment when present;
/// degrades to an empty "Mock package not loaded" state otherwise.</description></item>
/// <item><description><b>Settings</b> — interceptor / proxy config
/// (was Traffic → "Settings").</description></item>
/// </list>
/// <para>
/// A given Bowire process is NEVER both Standalone AND Embedded at the
/// same time — deployment shape is fixed by how Bowire was launched.
/// The Settings sub-tab reads <c>__BOWIRE_CONFIG__.embeddedMode</c> on
/// every render and surfaces whichever config (loopback / external
/// proxy URL vs. UseBowireInterceptor middleware status) makes sense
/// for the active mode.
/// </para>
/// <para>
/// <see cref="SortIndex"/> inherits the slot Traffic occupied (950) so
/// the merged rail's icon lands where operators already trained their
/// muscle memory. The boot-migration block in <c>prologue.js</c>
/// rewrites legacy ids (<c>mocks</c>, <c>traffic</c>, <c>intercepted</c>,
/// <c>proxy</c>) to <c>intercept</c> on first paint.
/// </para>
/// </remarks>
public sealed class BowireInterceptRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "intercept";
    /// <inheritdoc />
    public string DisplayName => "Intercept";
    /// <inheritdoc />
    /// <remarks>
    /// Inherits the trafficLight glyph the operator already learned for
    /// the Traffic rail in the same slot. The Mocks-only glyph is dropped
    /// — Mock servers becomes one sub-tab inside this rail, not a
    /// peer-level surface.
    /// </remarks>
    public string IconKey => "trafficLight";
    /// <inheritdoc />
    public int SortIndex => 950;
    /// <inheritdoc />
    public string Group => "quality";
    /// <inheritdoc />
    public string SidebarKind => "intercept";
}
