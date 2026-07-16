// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Registers the workbench OAST session (#486) — the long-lived interaction
/// session behind the Security rail's manual pen-test panel. Auto-discovered by
/// Core's <c>AddBowire</c> assembly scan.
/// </summary>
/// <remarks>
/// The server URL + token are read at resolution time from
/// <c>Bowire:Oast:Server</c> / <c>Bowire:Oast:Token</c>, so an operator enables
/// the panel with <c>--oast-server</c> (bridged into the host config) or the
/// <c>Bowire__Oast__Server</c> environment variable — the same value the
/// scanner's <c>--oast-server</c> takes.
/// </remarks>
public sealed class BowireOastWorkbenchServiceContribution : IBowireServiceContribution
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            return new OastWorkbenchSession(
                config["Bowire:Oast:Server"], config["Bowire:Oast:Token"]);
        });
    }
}

/// <summary>
/// Read + control endpoints for the manual OAST panel (#486), mounted into the
/// auth-gated workbench group via the <see cref="IBowireEndpointContribution"/>
/// seam.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><c>GET  {base}/api/security/oast/status</c> — is a server configured, and which.</item>
///   <item><c>POST {base}/api/security/oast/allocate</c> — hand out a fresh callback host to plant.</item>
///   <item><c>GET  {base}/api/security/oast/poll</c> — the accumulated callback feed.</item>
/// </list>
/// </remarks>
public sealed class BowireOastWorkbenchEndpointContribution : IBowireEndpointContribution
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet($"{basePath}/api/security/oast/status", (HttpContext ctx) =>
        {
            var session = ctx.RequestServices.GetService<OastWorkbenchSession>();
            return Results.Json(new
            {
                configured = session?.Configured ?? false,
                server = session?.ServerDomain,
                error = session?.ConfigError,
            });
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/security/oast/allocate", async (HttpContext ctx) =>
        {
            var session = ctx.RequestServices.GetService<OastWorkbenchSession>();
            if (session is null || !session.Configured)
            {
                return Results.Json(
                    new { error = "No OAST interaction server is configured. Start one with `bowire oast serve` and set --oast-server (or Bowire__Oast__Server)." },
                    statusCode: StatusCodes.Status409Conflict);
            }
            try
            {
                var host = await session.AllocateAsync(ctx.RequestAborted).ConfigureAwait(false);
                return Results.Json(new { host });
            }
            catch (Exception ex) when (ex is Oast.OastException or HttpRequestException or TaskCanceledException)
            {
                // Register failed (server unreachable / rejected) — a 502 says
                // "the server, not your request" so the panel can tell the
                // operator to check the interaction server.
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/security/oast/poll", async (HttpContext ctx) =>
        {
            var session = ctx.RequestServices.GetService<OastWorkbenchSession>();
            if (session is null || !session.Configured)
            {
                return Results.Json(new { interactions = Array.Empty<OastCallback>() });
            }
            try
            {
                var interactions = await session.PollAsync(ctx.RequestAborted).ConfigureAwait(false);
                return Results.Json(new { interactions });
            }
            catch (Exception ex) when (ex is Oast.OastException or HttpRequestException or TaskCanceledException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        }).ExcludeFromDescription();
    }
}
