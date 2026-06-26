// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rail.Traffic;

/// <summary>
/// Traffic rail contribution (#315). Unifies the previous Proxy rail
/// (standalone <c>bowire proxy</c> sidecar) and Intercepted rail
/// (in-process <c>UseBowireInterceptor()</c> middleware) into one
/// activity-rail descriptor.
/// </summary>
/// <remarks>
/// <para>
/// A given Bowire process is NEVER both Standalone AND Embedded at the same
/// time — the deployment shape is fixed by how Bowire was launched. The
/// Traffic rail reads <c>BowireOptions.Mode</c> (surfaced into the JS bundle
/// as <c>__BOWIRE_CONFIG__.embeddedMode</c>) on render and adapts:
/// </para>
/// <list type="bullet">
/// <item><description>Standalone: header reads "Standalone proxy mode";
/// the Settings sub-tab exposes the loopback / external proxy URL.</description></item>
/// <item><description>Embedded: header reads "Embedded middleware mode";
/// the Settings sub-tab surfaces the in-process middleware status (was
/// <c>UseBowireInterceptor()</c> called? which routes does it cover?).</description></item>
/// </list>
/// <para>
/// The Flows + Mock Rules sub-tabs render identically across deployments —
/// both read from the same in-process <c>InterceptedFlowStore</c> +
/// <c>InterceptorMockStore</c> backing the
/// <c>/api/intercepted/*</c> (alias <c>/api/traffic/*</c>) endpoints.
/// </para>
/// <para>
/// <see cref="Id"/> deliberately lives at <c>"traffic"</c>; existing
/// installs with <c>localStorage.bowire_rail_mode='proxy'</c> or
/// <c>'intercepted'</c> are rewritten to <c>'traffic'</c> on first paint
/// by the boot-migration block in <c>prologue.js</c>.
/// </para>
/// </remarks>
public sealed class BowireTrafficRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "traffic";
    /// <inheritdoc />
    public string DisplayName => "Traffic";
    /// <inheritdoc />
    public string IconKey => "globe";
    /// <inheritdoc />
    /// <remarks>
    /// Sort index inherited from the old Intercepted rail (950) so the
    /// new Traffic icon lands in the same rail-strip slot the operator
    /// already learned. Proxy used to live at 900 — it slides up into
    /// the same Traffic slot, which is fine because there is no longer
    /// a separate visible Proxy icon to displace.
    /// </remarks>
    public int SortIndex => 950;
    /// <inheritdoc />
    public string Group => "quality";
    /// <inheritdoc />
    public string SidebarKind => "traffic";
}
