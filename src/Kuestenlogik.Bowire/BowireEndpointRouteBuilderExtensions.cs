// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire;

/// <summary>
/// ASP.NET routing extensions for mounting the Bowire multi-protocol API
/// workbench onto an <see cref="IEndpointRouteBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>MapBowire()</c> adds the workbench's UI assets and JSON API under a
/// single route prefix. Pair it with
/// <see cref="BowireServiceCollectionExtensions.AddBowire(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>
/// in <c>Program.cs</c> so installed protocol plugins are auto-registered
/// before the endpoint is mapped.
/// </para>
/// <para>
/// The workbench supports gRPC, REST/OpenAPI, SignalR, WebSocket, SSE,
/// MCP, MQTT, GraphQL, OData and Socket.IO — every protocol whose plugin
/// NuGet is referenced in your project.
/// </para>
/// </remarks>
/// <seealso cref="BowireServiceCollectionExtensions"/>
/// <seealso cref="BowireOptions"/>
public static class BowireEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the Bowire workbench at the given route prefix.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to attach to.</param>
    /// <param name="pattern">
    /// URL path prefix for the workbench. Defaults to <c>/bowire</c>.
    /// Must start with a slash. A trailing slash is optional.
    /// </param>
    /// <param name="configure">
    /// Optional callback to customise the <see cref="BowireOptions"/>
    /// (title, theme, discovery URLs, proto sources, …).
    /// </param>
    /// <returns>The same <paramref name="endpoints"/> instance, so calls can be chained.</returns>
    /// <remarks>
    /// The value of <see cref="BowireOptions.RoutePrefix"/> set inside
    /// <paramref name="configure"/> is overwritten by <paramref name="pattern"/>
    /// — the pattern parameter is always authoritative.
    /// </remarks>
    /// <example>
    /// Zero-config mount (gRPC server with Server Reflection enabled):
    /// <code>
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.Services.AddBowire();
    /// var app = builder.Build();
    /// app.MapBowire();           // UI + API at /bowire
    /// </code>
    ///
    /// With proto import (no reflection needed):
    /// <code>
    /// app.MapBowire(options =>
    /// {
    ///     options.ProtoSources.Add(ProtoSource.FromFile("protos/weather.proto"));
    /// });
    /// </code>
    ///
    /// Custom route, title and theme:
    /// <code>
    /// app.MapBowire("/tools/api", options =>
    /// {
    ///     options.Title = "Payments API";
    ///     options.Theme = BowireTheme.Light;
    /// });
    /// </code>
    /// </example>
    public static IEndpointRouteBuilder MapBowire(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/bowire",
        Action<BowireOptions>? configure = null)
    {
        var options = new BowireOptions();
        configure?.Invoke(options);
        options.RoutePrefix = pattern.TrimStart('/');

        BowireApiEndpoints.Map(endpoints, options);

        return endpoints;
    }

    /// <summary>
    /// Maps the Bowire workbench at the default <c>/bowire</c> route,
    /// with the given configuration callback.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to attach to.</param>
    /// <param name="configure">
    /// Callback to customise the <see cref="BowireOptions"/>. Required
    /// overload — use <see cref="MapBowire(IEndpointRouteBuilder, string, Action{BowireOptions})"/>
    /// if you don't need to configure anything.
    /// </param>
    /// <returns>The same <paramref name="endpoints"/> instance, so calls can be chained.</returns>
    /// <example>
    /// <code>
    /// app.MapBowire(options =>
    /// {
    ///     options.Title = "Orders service";
    ///     options.ServerUrls.Add("https://orders:443");
    /// });
    /// </code>
    /// </example>
    public static IEndpointRouteBuilder MapBowire(
        this IEndpointRouteBuilder endpoints,
        Action<BowireOptions> configure)
        => endpoints.MapBowire("/bowire", configure);
}
