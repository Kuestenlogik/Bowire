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
            // #537 — touch the override store FIRST. Resolving it is what
            // constructs it, and its ctor Load()s ~/.bowire/catalogue-config.json
            // and applies the persisted override to the accessor. Because both
            // are lazy TryAddSingletons, a workbench that only ever calls /info
            // + /entries (the boot path) never hydrated the override — the
            // operator's saved provider only lit up after someone opened the
            // Settings tab, which GETs /config. Doing it here makes first paint
            // honest.
            HydrateOverrideStore(ctx);

            var accessor = TryResolveAccessor(ctx, out var accessorError);
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
                // #537 — the vocabulary of providers that are actually
                // LOADED in this process. local / http / consul always
                // resolve (they live in core); kubernetes / agent only
                // appear once the operator installed the matching sibling
                // package. The Settings picker greys out the rest instead
                // of offering a row that fails at save time, and the
                // workbench can name the package to install.
                providers = DiscoverLoadedProviders(),
                // Non-null only when the configured provider id doesn't
                // resolve (typo, or a sibling package that isn't installed).
                // /info is documented as always-200, so a bad id degrades
                // into an explainable "no catalogue" instead of a 500 that
                // leaves the workbench with no catalogue AND no reason.
                error = accessorError,
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
                kubernetes = MaskKubernetes(current?.Kubernetes),
                agent = MaskAgent(current?.Agent),
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

    /// <summary>
    /// Wire shape of one row in <c>/api/catalogue/info</c>'s
    /// <c>providers</c> array (#537). Serialised through
    /// <see cref="BowireEndpointHelpers.JsonOptions"/>, so the property
    /// names land camel-cased.
    /// </summary>
    private sealed record LoadedProvider(string Id, string Name);

    /// <summary>
    /// Assembly-scan vocabulary of every catalogue provider present in
    /// this process. Deliberately independent of the accessor: a typo in
    /// <c>Bowire:Discovery:Catalogue:Provider</c> makes the accessor
    /// throw, but the UI still needs the list to explain WHY the
    /// configured id didn't resolve.
    /// </summary>
    private static LoadedProvider[] DiscoverLoadedProviders()
    {
        try
        {
            return [.. BowireCatalogueProviderRegistry.Discover()
                .Values
                .Select(p => new LoadedProvider(p.Id, p.Name))
                .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Discover already swallows per-assembly load failures; this
            // catch only covers a pathological AppDomain enumeration.
            // An empty vocabulary reads as "we can't tell" on the client,
            // which is the honest answer.
            return [];
        }
    }

    /// <summary>
    /// Construct the override store so its ctor re-applies the persisted
    /// <c>~/.bowire/catalogue-config.json</c>. Swallows everything: a host
    /// that never called <c>AddBowireCatalogue()</c> has no store, and a
    /// store whose override names a missing provider must not take the
    /// capability probe down with it.
    /// </summary>
    private static void HydrateOverrideStore(HttpContext ctx)
    {
        try { _ = ctx.RequestServices.GetService<BowireCatalogueOverrideStore>(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best-effort — the accessor resolution below reports the
            // actionable error.
        }
    }

    /// <summary>
    /// Resolve the accessor, converting a configuration error (unknown
    /// provider id — <see cref="BowireCatalogueProviderRegistry.Resolve"/>
    /// throws by design so a typo surfaces) into an out-param instead of
    /// a 500.
    /// </summary>
    private static BowireCatalogueProviderAccessor? TryResolveAccessor(HttpContext ctx, out string? error)
    {
        try
        {
            error = null;
            return ctx.RequestServices.GetService<BowireCatalogueProviderAccessor>();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.Message;
            return null;
        }
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

    private static BowireKubernetesCatalogueOverrideOptions? MaskKubernetes(BowireKubernetesCatalogueOverrideOptions? opts)
    {
        if (opts is null) return null;
        return new BowireKubernetesCatalogueOverrideOptions
        {
            ApiServerUrl = opts.ApiServerUrl,
            // Token + CA pem are the secret-sized fields; mask the
            // same way Consul masks its ACL token. Everything else is
            // a path / namespace / boolean — non-secret, surfaces as
            // typed.
            Token = string.IsNullOrEmpty(opts.Token) ? null : "__set__",
            KubeconfigPath = opts.KubeconfigPath,
            Namespace = opts.Namespace,
            LabelSelector = opts.LabelSelector,
            Scheme = opts.Scheme,
            CaCertificatePem = string.IsNullOrEmpty(opts.CaCertificatePem) ? null : "__set__",
            SkipTlsVerification = opts.SkipTlsVerification,
        };
    }

    private static BowireAgentCatalogueOverrideOptions? MaskAgent(BowireAgentCatalogueOverrideOptions? opts)
    {
        if (opts is null) return null;
        return new BowireAgentCatalogueOverrideOptions
        {
            HubUrl = opts.HubUrl,
            BootstrapToken = string.IsNullOrEmpty(opts.BootstrapToken) ? null : "__set__",
            // StubResponse is wire-shape sample data, not a secret —
            // surfaces verbatim.
            StubResponse = opts.StubResponse,
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
        if (incoming.Kubernetes is not null)
        {
            incoming.Kubernetes.Token = Reconcile(incoming.Kubernetes.Token, existing.Kubernetes?.Token);
            incoming.Kubernetes.CaCertificatePem = Reconcile(
                incoming.Kubernetes.CaCertificatePem, existing.Kubernetes?.CaCertificatePem);
        }
        if (incoming.Agent is not null)
        {
            incoming.Agent.BootstrapToken = Reconcile(
                incoming.Agent.BootstrapToken, existing.Agent?.BootstrapToken);
        }
        return incoming;
    }

    private static async Task<IResult> FetchAndRespondAsync(HttpContext ctx)
    {
        // #537 — same hydration reason as /info: /entries is the second
        // call the workbench makes on boot and may well be the first one
        // that reaches a given process (a reload skips /info's cache).
        HydrateOverrideStore(ctx);
        var provider = TryResolveAccessor(ctx, out var accessorError)?.Provider;
        if (provider is null && accessorError is not null)
        {
            return BowireEndpointHelpers.Problem(
                type: "urn:bowire:catalogue:provider-unresolved",
                title: "Catalogue provider not loaded",
                status: 502,
                detail: accessorError,
                instance: ctx.Request.Path);
        }
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
