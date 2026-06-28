// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// Contract a Bowire-sibling package implements to splice extra ASP.NET
/// endpoints into the workbench's auth-gated route group at
/// <c>MapBowire()</c> time (#325, v2.1).
/// </summary>
/// <remarks>
/// <para>
/// Introduced when Welle 2 of the v2.1 package cleanup lifted the
/// interceptor surface (the /api/intercepted/* + /api/tools/reverse-
/// proxy/* endpoints) out of Core into
/// <c>Kuestenlogik.Bowire.Interceptor</c>. Core no longer references
/// those endpoint types directly — instead it discovers every public
/// implementation of this interface from loaded
/// <c>Kuestenlogik.Bowire.*</c> assemblies and invokes
/// <see cref="MapEndpoints"/> on each so the package owns its own
/// mount.
/// </para>
/// <para>
/// Implementations must have a parameterless constructor; Core
/// instantiates them via <c>Activator.CreateInstance</c> during the
/// MapBowire pass. Failure to map (any exception escaping
/// <see cref="MapEndpoints"/>) is logged and swallowed so one
/// misbehaving package can't take down host startup — same posture as
/// the rail / module / protocol auto-discovery surfaces.
/// </para>
/// <para>
/// The endpoints land inside Core's auth-gated route group so a host
/// that opts into <c>AddBowireAuth(...)</c> automatically gates this
/// package's admin surface too.
/// </para>
/// </remarks>
public interface IBowireEndpointContribution
{
    /// <summary>
    /// Map this contribution's endpoints. Called once per host startup
    /// from <c>BowireApiEndpoints.MapBowire</c>.
    /// </summary>
    /// <param name="endpoints">
    /// The auth-gated <see cref="IEndpointRouteBuilder"/> group every
    /// workbench feature endpoint lives under.
    /// </param>
    /// <param name="basePath">
    /// The URL fragment every route is anchored under (e.g.
    /// <c>/bowire</c> for embedded hosts, empty for the standalone CLI).
    /// </param>
    void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath);
}
