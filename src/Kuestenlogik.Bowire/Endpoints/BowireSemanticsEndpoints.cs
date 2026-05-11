// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Semantics;
using Kuestenlogik.Bowire.Semantics.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// HTTP endpoints for the frame-semantics framework — effective-schema
/// queries and UI-extension descriptor / asset serving. Phase 3 of the
/// frame-semantics rollout.
/// </summary>
/// <remarks>
/// <para>
/// Two endpoint groups:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>GET /api/semantics/effective?service={id}&amp;method={id}</c> —
/// returns the effective annotations (post-resolution) for the given
/// service + method pair. Walks
/// <see cref="IAnnotationStore.EnumerateEffective"/> and filters by the
/// query pair. Consumed by the JS-side extension router to pick which
/// viewers to mount for the active method.
/// </description></item>
/// <item><description>
/// <c>GET /api/ui/extensions</c> + <c>GET /api/ui/extensions/{id}/{name}</c>
/// — lists installed UI extensions and serves their embedded bundle /
/// stylesheet assets. The JS-side loader fetches the listing once at
/// boot and treats <c>{ kind: ... }</c> entries as available routes;
/// the per-asset endpoint exists so a future dynamic-import flow can
/// pull lazy bundles without ever leaving the local origin.
/// </description></item>
/// </list>
/// </remarks>
internal static class BowireSemanticsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Cached extension registry. The registry only inspects already-loaded
    /// assemblies, so a single discovery sweep at first endpoint touch is
    /// enough — new plugins arriving later in the request pipeline are
    /// unusual and not supported in v1.0 (matches protocol-plugin
    /// behaviour).
    /// </summary>
    private static BowireExtensionRegistry? s_extensionRegistry;
    private static readonly Lock s_extensionRegistryLock = new();

    public static IEndpointRouteBuilder MapBowireSemanticsEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        // ----------------------------------------------------------------
        // Effective-schema lookup
        // ----------------------------------------------------------------
        endpoints.MapGet($"{basePath}/api/semantics/effective", (HttpContext ctx) =>
        {
            var service = ctx.Request.Query["service"].ToString();
            var method = ctx.Request.Query["method"].ToString();

            if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(method))
            {
                return Results.BadRequest(new
                {
                    error = "Query parameters 'service' and 'method' are required.",
                });
            }

            var store = ctx.RequestServices.GetService<IAnnotationStore>();
            if (store is null)
            {
                // Bowire is wired without AddBowire(...) (unusual — tests
                // that mount only a subset of endpoints can hit this).
                // Return an empty payload rather than 500 so the JS-side
                // router degrades gracefully to "no widgets to mount".
                return Results.Ok(new EffectiveSchemaResponse(service, method, []));
            }

            var annotations = new List<EffectiveAnnotation>();
            foreach (var annotation in store.EnumerateEffective())
            {
                if (!string.Equals(annotation.Key.ServiceId, service, StringComparison.Ordinal)) continue;
                if (!string.Equals(annotation.Key.MethodId, method, StringComparison.Ordinal)) continue;

                annotations.Add(new EffectiveAnnotation(
                    MessageType: annotation.Key.MessageType,
                    JsonPath: annotation.Key.JsonPath,
                    Semantic: annotation.Semantic.Kind,
                    Source: SourceToString(annotation.Source)));
            }

            return Results.Ok(new EffectiveSchemaResponse(service, method, annotations));
        }).ExcludeFromDescription();

        // ----------------------------------------------------------------
        // UI-extension catalogue
        // ----------------------------------------------------------------
        endpoints.MapGet($"{basePath}/api/ui/extensions", () =>
        {
            var registry = GetExtensionRegistry();
            var extensions = new List<ExtensionDescriptor>(registry.UiExtensions.Count);
            foreach (var ext in registry.UiExtensions)
            {
                extensions.Add(new ExtensionDescriptor(
                    Id: ext.Id,
                    BowireApi: ext.BowireApiRange,
                    Kinds: ext.Kinds,
                    Capabilities: CapabilitiesToStrings(ext.Capabilities),
                    BundleUrl: $"{basePath}/api/ui/extensions/{ext.Id}/{Path.GetFileName(ext.BundleResourceName)}",
                    StylesUrl: ext.StylesResourceName is null
                        ? null
                        : $"{basePath}/api/ui/extensions/{ext.Id}/{Path.GetFileName(ext.StylesResourceName)}"));
            }
            return Results.Json(new { extensions }, JsonOpts);
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/ui/extensions/{{id}}/{{name}}",
            (string id, string name) =>
        {
            var registry = GetExtensionRegistry();
            var ext = registry.GetUiExtension(id);
            if (ext is null) return Results.NotFound();

            var assembly = registry.GetDeclaringAssembly(id);
            if (assembly is null) return Results.NotFound();

            // Resolve the asset name against the descriptor's declared
            // resource list. Anything else is rejected — the endpoint
            // doesn't serve arbitrary file paths out of the plugin
            // assembly.
            var resourceName = ResolveAssetResourceName(ext, name);
            if (resourceName is null) return Results.NotFound();

            using var stream = EmbeddedExtensionAsset.OpenRead(assembly, ext, resourceName);
            if (stream is null) return Results.NotFound();

            var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            return Results.File(
                fileContents: ms.ToArray(),
                contentType: EmbeddedExtensionAsset.GuessContentType(name));
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// Map a request "name" against the descriptor's declared resource
    /// names. Matches by the leaf filename so the URL stays short
    /// (<c>/api/ui/extensions/{id}/map.js</c>) while the descriptor can
    /// keep the full embedded-resource path
    /// (<c>wwwroot/js/widgets/map.js</c>).
    /// </summary>
    private static string? ResolveAssetResourceName(IBowireUiExtension ext, string name)
    {
        // Hot path: bundle / styles.
        if (LeafEquals(ext.BundleResourceName, name)) return ext.BundleResourceName;
        if (ext.StylesResourceName is not null
            && LeafEquals(ext.StylesResourceName, name))
        {
            return ext.StylesResourceName;
        }

        // Extras declared by extensions that ship more than the two
        // hot-path assets (the built-in MapLibre extension's vendored
        // maplibre-gl.js + LICENSE).
        if (ext is MapLibreExtension ml)
        {
            foreach (var extra in ml.AdditionalAssetNames)
            {
                if (LeafEquals(extra, name)) return extra;
            }
        }

        return null;
    }

    private static bool LeafEquals(string resourceName, string requestName)
        => string.Equals(
            Path.GetFileName(resourceName),
            requestName,
            StringComparison.Ordinal);

    private static BowireExtensionRegistry GetExtensionRegistry()
    {
        var cached = s_extensionRegistry;
        if (cached is not null) return cached;
        lock (s_extensionRegistryLock)
        {
            cached = s_extensionRegistry;
            if (cached is null)
            {
                cached = BowireExtensionRegistry.Discover();
                s_extensionRegistry = cached;
            }
        }
        return cached;
    }

    /// <summary>
    /// Test seam — reset the cached registry so the next endpoint call
    /// re-runs <see cref="BowireExtensionRegistry.Discover"/>. Used by
    /// tests that load synthetic extension assemblies after the host
    /// has already been spun up.
    /// </summary>
    internal static void ResetCachedRegistryForTests()
    {
        lock (s_extensionRegistryLock)
        {
            s_extensionRegistry = null;
        }
    }

    private static string SourceToString(AnnotationSource source) => source switch
    {
        AnnotationSource.User => "user",
        AnnotationSource.Plugin => "plugin",
        AnnotationSource.Auto => "auto",
        _ => "none",
    };

    private static List<string> CapabilitiesToStrings(ExtensionCapabilities caps)
    {
        var list = new List<string>(2);
        if (caps.HasFlag(ExtensionCapabilities.Viewer)) list.Add("viewer");
        if (caps.HasFlag(ExtensionCapabilities.Editor)) list.Add("editor");
        return list;
    }

    private sealed record EffectiveSchemaResponse(
        string Service,
        string Method,
        IReadOnlyList<EffectiveAnnotation> Annotations);

    private sealed record EffectiveAnnotation(
        string MessageType,
        string JsonPath,
        string Semantic,
        string Source);

    private sealed record ExtensionDescriptor(
        string Id,
        string BowireApi,
        IReadOnlyList<string> Kinds,
        IReadOnlyList<string> Capabilities,
        string BundleUrl,
        string? StylesUrl);
}
