// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// HTTP surface for the catalogue-provider seam (#136). Three endpoints:
/// <list type="bullet">
///   <item><c>GET /api/catalogue/info</c> — capability probe:
///         which provider is active (id + name) + the visibility
///         + refresh interval the workbench should respect. Always
///         200 — empty body when no provider is configured.</item>
///   <item><c>GET /api/catalogue/entries</c> — the current
///         snapshot from the active provider. Empty list when no
///         provider is configured. Problem-details on fetch failure.</item>
///   <item><c>POST /api/catalogue/refresh</c> — explicit refresh
///         trigger. Returns the freshly-fetched snapshot.</item>
/// </list>
/// </summary>
internal static class BowireCatalogueEndpoints
{
    public static IEndpointRouteBuilder MapBowireCatalogueEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/catalogue/info", (HttpContext ctx) =>
        {
            var provider = ctx.RequestServices.GetService<BowireCatalogueProviderAccessor>()?.Provider;
            var options = ctx.RequestServices.GetService<IOptions<BowireCatalogueOptions>>()?.Value
                          ?? new BowireCatalogueOptions();
            return Results.Json(new
            {
                available = provider is not null,
                providerId = provider?.Id,
                providerName = provider?.Name,
                // CA1308: the wire contract is lowercase ("editable",
                // "readonly", "hidden") — that matches the
                // appsettings.json enum binding shape the operator
                // already uses. Use the lower-case form explicitly
                // with InvariantCulture instead of ToLowerInvariant
                // so the analyzer is happy with the call shape.
                visibility = options.Visibility switch
                {
                    BowireCatalogueVisibility.Editable => "editable",
                    BowireCatalogueVisibility.Readonly => "readonly",
                    BowireCatalogueVisibility.Hidden => "hidden",
                    _ => "editable",
                },
                refreshIntervalSeconds = (int)Math.Max(0, options.RefreshInterval.TotalSeconds),
            }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/catalogue/entries", async (HttpContext ctx) =>
        {
            return await FetchAndRespondAsync(ctx).ConfigureAwait(false);
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/catalogue/refresh", async (HttpContext ctx) =>
        {
            return await FetchAndRespondAsync(ctx).ConfigureAwait(false);
        }).ExcludeFromDescription();

        return endpoints;
    }

    private static async Task<IResult> FetchAndRespondAsync(HttpContext ctx)
    {
        var provider = ctx.RequestServices.GetService<BowireCatalogueProviderAccessor>()?.Provider;
        if (provider is null)
        {
            // No provider configured — return an empty list (200) so
            // the workbench can treat "no catalogue" and "empty
            // catalogue" symmetrically.
            return Results.Json(new
            {
                providerId = (string?)null,
                entries = Array.Empty<BowireCatalogueEntry>()
            }, BowireEndpointHelpers.JsonOptions);
        }

        try
        {
            var entries = await provider.FetchAsync(ctx.RequestAborted).ConfigureAwait(false);
            return Results.Json(new
            {
                providerId = provider.Id,
                providerName = provider.Name,
                entries
            }, BowireEndpointHelpers.JsonOptions);
        }
        // Provider FetchAsync can throw anything a 3rd-party transport
        // throws (HttpRequestException, SocketException, JsonException,
        // IOException, ...). Surface as problem-details so the UI can
        // render an actionable error.
#pragma warning disable CA1031 // Do not catch general exception types
        catch (OperationCanceledException)
#pragma warning restore CA1031
        {
            return BowireEndpointHelpers.Problem(
                type: "urn:bowire:catalogue:canceled",
                title: "Catalogue fetch canceled",
                status: 499,
                detail: "The client aborted the catalogue fetch before it completed.",
                instance: ctx.Request.Path);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return BowireEndpointHelpers.Problem(
                type: "urn:bowire:catalogue:fetch-failed",
                title: "Catalogue fetch failed",
                status: 502,
                detail: $"{provider.Name} ({provider.Id}): {ex.Message}",
                instance: ctx.Request.Path,
                extensions: new Dictionary<string, object?>
                {
                    ["providerId"] = provider.Id,
                    ["providerName"] = provider.Name,
                });
        }
    }
}
