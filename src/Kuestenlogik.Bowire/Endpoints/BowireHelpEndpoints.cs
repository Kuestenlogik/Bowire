// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Help;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// HTTP surface for the in-app help system (#154). Four endpoints:
/// <list type="bullet">
///   <item><c>GET /api/help/available</c> — capability probe: did the host
///         register an <see cref="IBowireHelpProvider"/>?</item>
///   <item><c>GET /api/help/topics</c> — flat list of topic summaries
///         for the drawer's topic tree.</item>
///   <item><c>GET /api/help/topic/{id}</c> — full markdown body of one topic.</item>
///   <item><c>GET /api/help/search?q=...</c> — ranked hits across all topics.</item>
/// </list>
/// When no <see cref="IBowireHelpProvider"/> is in DI, the three
/// content endpoints return <c>501 Not Implemented</c> (distinct from
/// 404 — "no provider installed" vs "topic missing"). The capability
/// probe always answers <c>200</c> with <c>{ available: bool }</c> so
/// the workbench can gate UI affordances at boot in one round-trip.
/// </summary>
internal static class BowireHelpEndpoints
{
    public static IEndpointRouteBuilder MapBowireHelpEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/help/available", (HttpContext ctx) =>
        {
            var provider = ctx.RequestServices.GetService<IBowireHelpProvider>();
            return Results.Ok(new { available = provider is not null });
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/help/topics", (HttpContext ctx) =>
        {
            var provider = ctx.RequestServices.GetService<IBowireHelpProvider>();
            if (provider is null) return NotImplemented(ctx);
            var topics = provider.ListTopics();
            return Results.Ok(new { topics });
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/help/topic/{{id}}",
            (HttpContext ctx, string id) =>
        {
            var provider = ctx.RequestServices.GetService<IBowireHelpProvider>();
            if (provider is null) return NotImplemented(ctx);
            var topic = provider.GetTopic(id);
            if (topic is null)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:help:topic-not-found",
                    title: "Help topic not found",
                    status: 404,
                    detail: $"No help topic registered with id '{id}'.",
                    instance: ctx.Request.Path,
                    extensions: new Dictionary<string, object?> { ["topicId"] = id });
            }
            return Results.Ok(topic);
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/help/search",
            (HttpContext ctx, string? q, int? limit) =>
        {
            var provider = ctx.RequestServices.GetService<IBowireHelpProvider>();
            if (provider is null) return NotImplemented(ctx);
            var hits = provider.Search(q ?? string.Empty, limit ?? 20);
            return Results.Ok(new { hits });
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// 501 Not Implemented + a problem-details body that tells the UI
    /// to surface the "install the Help package" hint. The discriminator
    /// is the <c>type</c> URN — the workbench keys on it to render the
    /// disabled-state copy with the package id.
    /// </summary>
    private static IResult NotImplemented(HttpContext ctx) =>
        BowireEndpointHelpers.Problem(
            type: "urn:bowire:help:provider-not-registered",
            title: "Help provider not registered",
            status: 501,
            detail: "No IBowireHelpProvider is registered with this host. Install the Kuestenlogik.Bowire.Help NuGet package and call builder.AddBowireHelp() to enable in-app docs.",
            instance: ctx.Request.Path,
            extensions: new Dictionary<string, object?>
            {
                ["packageId"] = "Kuestenlogik.Bowire.Help",
                ["addExtension"] = "AddBowireHelp"
            });
}
