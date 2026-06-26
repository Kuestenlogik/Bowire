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
/// HTTP surface for the catalogue-provider seam (#136 / #309). Endpoints:
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
///   <item><c>GET /api/catalogue/config</c> — read the persisted
///         UI override (#309). Body shape mirrors the request body of
///         <c>POST</c>; returns <c>{ hasOverride: false, ... }</c>
///         when no UI override is set.</item>
///   <item><c>POST /api/catalogue/config</c> — hot-swap the active
///         provider with a UI-supplied config + persist to
///         <c>~/.bowire/catalogue-config.json</c>.</item>
///   <item><c>DELETE /api/catalogue/config</c> — clear the override
///         and fall back to appsettings.</item>
/// </list>
/// </summary>
internal static class BowireCatalogueEndpoints
{
    public static IEndpointRouteBuilder MapBowireCatalogueEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/catalogue/info", (HttpContext ctx) =>
        {
            var accessor = ctx.RequestServices.GetService<BowireCatalogueProviderAccessor>();
            var provider = accessor?.Provider;
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
                // #309 — surface whether a UI-driven override is
                // active so the Settings UI can render "Workspace
                // override" vs "appsettings fallback" without a
                // separate fetch.
                hasOverride = accessor?.HasOverride ?? false,
                defaultProviderId = accessor?.DefaultProvider?.Id,
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

        // #309 — UI-driven override surface. Mirrors the AI-config
        // pattern (POST/DELETE persisted to disk + hot-swapped via
        // accessor). The store is registered by
        // AddBowireCatalogue(); a host that doesn't call it gets a
        // 404 here, same as for the rest of the catalogue surface.
        endpoints.MapGet($"{basePath}/api/catalogue/config", (HttpContext ctx) =>
        {
            var store = ctx.RequestServices.GetService<BowireCatalogueOverrideStore>();
            if (store is null)
            {
                return Results.Json(new { hasOverride = false }, BowireEndpointHelpers.JsonOptions);
            }
            var current = store.Current;
            return Results.Json(new
            {
                hasOverride = current is not null,
                provider = current?.Provider,
                local = current?.Local,
                http = MaskHttp(current?.Http),
                consul = MaskConsul(current?.Consul),
            }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/catalogue/config", async (HttpContext ctx) =>
        {
            var store = ctx.RequestServices.GetService<BowireCatalogueOverrideStore>();
            if (store is null)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:catalogue:no-store",
                    title: "Catalogue not wired",
                    status: 404,
                    detail: "AddBowireCatalogue() is not registered on this host. Falling back to appsettings only.",
                    instance: ctx.Request.Path);
            }
            BowireCatalogueOverride? payload;
            try
            {
                payload = await ctx.Request.ReadFromJsonAsync<BowireCatalogueOverride>(
                    cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:catalogue:bad-config",
                    title: "Invalid catalogue config",
                    status: 400,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }
            if (payload is null)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:catalogue:bad-config",
                    title: "Missing body",
                    status: 400,
                    detail: "POST /api/catalogue/config requires a JSON body.",
                    instance: ctx.Request.Path);
            }
            // Merge persisted secrets back in when the UI sent an
            // empty / masked sentinel. The Settings form leaves
            // password fields blank to "keep existing" so a save
            // doesn't accidentally wipe a previously-set token.
            payload = MergeSecrets(payload, store.Current);
            store.Save(payload);
            var accessor = ctx.RequestServices.GetService<BowireCatalogueProviderAccessor>();
            return Results.Json(new
            {
                hasOverride = true,
                providerId = accessor?.Provider?.Id,
                providerName = accessor?.Provider?.Name,
            }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/catalogue/config", (HttpContext ctx) =>
        {
            var store = ctx.RequestServices.GetService<BowireCatalogueOverrideStore>();
            if (store is null)
            {
                return Results.Json(new { hasOverride = false }, BowireEndpointHelpers.JsonOptions);
            }
            store.Clear();
            var accessor = ctx.RequestServices.GetService<BowireCatalogueProviderAccessor>();
            return Results.Json(new
            {
                hasOverride = false,
                providerId = accessor?.Provider?.Id,
                providerName = accessor?.Provider?.Name,
            }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }

    private static BowireHttpCatalogueOptions? MaskHttp(BowireHttpCatalogueOptions? opts)
    {
        if (opts is null) return null;
        return new BowireHttpCatalogueOptions
        {
            Url = opts.Url,
            // Mask the Authorization header — the UI shows "set" vs
            // "not set" and prompts for re-entry only on edit.
            Authorization = string.IsNullOrEmpty(opts.Authorization) ? null : "__set__",
            Timeout = opts.Timeout,
        };
    }

    private static BowireConsulCatalogueOptions? MaskConsul(BowireConsulCatalogueOptions? opts)
    {
        if (opts is null) return null;
        return new BowireConsulCatalogueOptions
        {
            Address = opts.Address,
            Token = string.IsNullOrEmpty(opts.Token) ? null : "__set__",
            Datacenter = opts.Datacenter,
            Tag = opts.Tag,
            Scheme = opts.Scheme,
            Timeout = opts.Timeout,
        };
    }

    private static BowireCatalogueOverride MergeSecrets(
        BowireCatalogueOverride incoming, BowireCatalogueOverride? existing)
    {
        if (existing is null) return incoming;
        // Treat the "__keep__" sentinel + empty string as "keep
        // existing"; any other value (including a single space) is
        // an explicit overwrite. "__clear__" wipes the stored secret.
        static string? Reconcile(string? sent, string? stored)
        {
            if (sent is null) return stored;
            if (sent == "__keep__") return stored;
            if (sent == "__clear__") return null;
            if (sent.Length == 0) return stored;
            return sent;
        }
        if (incoming.Http is not null)
        {
            incoming.Http.Authorization = Reconcile(incoming.Http.Authorization, existing.Http?.Authorization);
        }
        if (incoming.Consul is not null)
        {
            incoming.Consul.Token = Reconcile(incoming.Consul.Token, existing.Consul?.Token);
        }
        return incoming;
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
