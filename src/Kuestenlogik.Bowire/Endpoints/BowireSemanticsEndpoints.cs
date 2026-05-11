// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
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
        // Manual user override — Phase 4
        //
        // POST /api/semantics/annotation writes a single user-tier
        // annotation to the named storage tier (session / user /
        // project). DELETE removes it from the named tier only — the
        // annotation may still survive at a lower tier (auto-detector,
        // plugin hint) and the response reflects the post-write
        // effective tag so the UI can update its badge atomically.
        //
        // Tier mapping:
        //   - "session"  → LayeredAnnotationStore.UserSessionLayer
        //                  (always present, never null)
        //   - "user"     → LayeredAnnotationStore.UserFileLayer
        //                  (null when SchemaHintsPath == "" — opt-out
        //                  by hardened deployments → 404)
        //   - "project"  → LayeredAnnotationStore.ProjectFileLayer
        //                  (null when bowire.schema-hints.json doesn't
        //                  exist in CWD → 404; users can create it via
        //                  a v1.4 "promote to project" flow)
        // ----------------------------------------------------------------
        endpoints.MapPost($"{basePath}/api/semantics/annotation",
            async (HttpContext ctx) =>
        {
            var req = await ReadAnnotationRequestAsync<AnnotationWriteRequest>(ctx);
            if (req is null)
            {
                return Results.BadRequest(new { error = "Request body must be valid JSON." });
            }
            if (!ValidateWriteRequest(req, out var validationError))
            {
                return Results.BadRequest(new { error = validationError });
            }

            var store = ctx.RequestServices.GetService<LayeredAnnotationStore>();
            if (store is null)
            {
                // Bowire wired without AddBowire — same graceful-degrade
                // path the GET takes, but for a write that's a 404
                // ("no store available") so the UI knows the action
                // didn't land.
                return Results.NotFound(new { error = "Annotation store is not available." });
            }

            var key = new AnnotationKey(req.Service!, req.Method!, req.MessageType ?? AnnotationKey.Wildcard, req.JsonPath!);
            var tag = new SemanticTag(req.Semantic!);

            var tier = ParseScope(req.Scope!);
            switch (tier)
            {
                case AnnotationTier.Session:
                    store.UserSessionLayer.Set(key, tag);
                    break;
                case AnnotationTier.User:
                    if (store.UserFileLayer is null)
                    {
                        return Results.NotFound(new
                        {
                            error = "User-tier persistence is disabled (SchemaHintsPath is empty).",
                        });
                    }
                    await EnsureFileLayerLoadedAsync(store.UserFileLayer, ctx.RequestAborted);
                    store.UserFileLayer.Set(key, tag);
                    await store.UserFileLayer.SaveAsync(ctx.RequestAborted);
                    break;
                case AnnotationTier.Project:
                    if (store.ProjectFileLayer is null)
                    {
                        return Results.NotFound(new
                        {
                            error = "Project-tier persistence is disabled (no bowire.schema-hints.json in CWD).",
                        });
                    }
                    await EnsureFileLayerLoadedAsync(store.ProjectFileLayer, ctx.RequestAborted);
                    store.ProjectFileLayer.Set(key, tag);
                    await store.ProjectFileLayer.SaveAsync(ctx.RequestAborted);
                    break;
                default:
                    return Results.BadRequest(new
                    {
                        error = "Field 'scope' must be one of 'session', 'user', 'project'.",
                    });
            }

            return Results.Ok(BuildEffectiveAfterWriteResponse(store, key));
        }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/semantics/annotation",
            async (HttpContext ctx) =>
        {
            var req = await ReadAnnotationRequestAsync<AnnotationDeleteRequest>(ctx);
            if (req is null)
            {
                return Results.BadRequest(new { error = "Request body must be valid JSON." });
            }
            if (!ValidateDeleteRequest(req, out var validationError))
            {
                return Results.BadRequest(new { error = validationError });
            }

            var store = ctx.RequestServices.GetService<LayeredAnnotationStore>();
            if (store is null)
            {
                return Results.NotFound(new { error = "Annotation store is not available." });
            }

            var key = new AnnotationKey(req.Service!, req.Method!, req.MessageType ?? AnnotationKey.Wildcard, req.JsonPath!);

            var tier = ParseScope(req.Scope!);
            switch (tier)
            {
                case AnnotationTier.Session:
                    store.UserSessionLayer.Remove(key);
                    break;
                case AnnotationTier.User:
                    if (store.UserFileLayer is null)
                    {
                        return Results.NotFound(new
                        {
                            error = "User-tier persistence is disabled (SchemaHintsPath is empty).",
                        });
                    }
                    await EnsureFileLayerLoadedAsync(store.UserFileLayer, ctx.RequestAborted);
                    store.UserFileLayer.Remove(key);
                    await store.UserFileLayer.SaveAsync(ctx.RequestAborted);
                    break;
                case AnnotationTier.Project:
                    if (store.ProjectFileLayer is null)
                    {
                        return Results.NotFound(new
                        {
                            error = "Project-tier persistence is disabled (no bowire.schema-hints.json in CWD).",
                        });
                    }
                    await EnsureFileLayerLoadedAsync(store.ProjectFileLayer, ctx.RequestAborted);
                    store.ProjectFileLayer.Remove(key);
                    await store.ProjectFileLayer.SaveAsync(ctx.RequestAborted);
                    break;
                default:
                    return Results.BadRequest(new
                    {
                        error = "Field 'scope' must be one of 'session', 'user', 'project'.",
                    });
            }

            // Cross-tier survival — a DELETE at the session tier still
            // resolves to plugin / auto if those tiers carry an
            // annotation at the same key. The response reflects the
            // post-delete effective tag so the UI badge updates to
            // "(plugin)" / "(auto)" instead of disappearing.
            return Results.Ok(BuildEffectiveAfterWriteResponse(store, key));
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

    /// <summary>
    /// Wire shape for <c>POST /api/semantics/annotation</c>. All four
    /// addressing dimensions plus the new semantic value and the tier
    /// the write should land in.
    /// </summary>
    private sealed record AnnotationWriteRequest
    {
        [JsonPropertyName("service")] public string? Service { get; init; }
        [JsonPropertyName("method")] public string? Method { get; init; }
        [JsonPropertyName("messageType")] public string? MessageType { get; init; }
        [JsonPropertyName("jsonPath")] public string? JsonPath { get; init; }
        [JsonPropertyName("semantic")] public string? Semantic { get; init; }
        [JsonPropertyName("scope")] public string? Scope { get; init; }
    }

    /// <summary>
    /// Wire shape for <c>DELETE /api/semantics/annotation</c> —
    /// identical to the write shape minus the <c>semantic</c> value.
    /// Modelled as a separate type so the validator's "semantic is
    /// required" rule lives in one obvious place.
    /// </summary>
    private sealed record AnnotationDeleteRequest
    {
        [JsonPropertyName("service")] public string? Service { get; init; }
        [JsonPropertyName("method")] public string? Method { get; init; }
        [JsonPropertyName("messageType")] public string? MessageType { get; init; }
        [JsonPropertyName("jsonPath")] public string? JsonPath { get; init; }
        [JsonPropertyName("scope")] public string? Scope { get; init; }
    }

    /// <summary>
    /// Response shape for the POST/DELETE endpoints — same as
    /// <see cref="EffectiveAnnotation"/>'s row but for a single key,
    /// plus a nullable semantic to encode "no annotation survived the
    /// delete" without overloading the empty-string sentinel.
    /// </summary>
    private sealed record AnnotationWriteResponse(
        string Service,
        string Method,
        string MessageType,
        string JsonPath,
        string? Semantic,
        string Source);

    private static async Task<T?> ReadAnnotationRequestAsync<T>(HttpContext ctx)
        where T : class
    {
        try
        {
            return await ctx.Request.ReadFromJsonAsync<T>(JsonOpts, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ValidateWriteRequest(AnnotationWriteRequest req, out string error)
    {
        if (string.IsNullOrWhiteSpace(req.Service))
        {
            error = "Field 'service' is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(req.Method))
        {
            error = "Field 'method' is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(req.JsonPath))
        {
            error = "Field 'jsonPath' is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(req.Semantic))
        {
            error = "Field 'semantic' is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(req.Scope))
        {
            error = "Field 'scope' is required.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool ValidateDeleteRequest(AnnotationDeleteRequest req, out string error)
    {
        if (string.IsNullOrWhiteSpace(req.Service))
        {
            error = "Field 'service' is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(req.Method))
        {
            error = "Field 'method' is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(req.JsonPath))
        {
            error = "Field 'jsonPath' is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(req.Scope))
        {
            error = "Field 'scope' is required.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Three persistence tiers the right-click UI can target. Names
    /// match the JSON scope-field literal values from the wire shape.
    /// Kept as a typed enum so the switch downstream stays exhaustive
    /// without re-stringifying the comparison.
    /// </summary>
    private enum AnnotationTier
    {
        Unknown = 0,
        Session = 1,
        User = 2,
        Project = 3,
    }

    private static AnnotationTier ParseScope(string scope)
    {
        if (string.Equals(scope, "session", StringComparison.OrdinalIgnoreCase)) return AnnotationTier.Session;
        if (string.Equals(scope, "user", StringComparison.OrdinalIgnoreCase)) return AnnotationTier.User;
        if (string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase)) return AnnotationTier.Project;
        return AnnotationTier.Unknown;
    }

    /// <summary>
    /// Load the layer's on-disk cache lazily on first write. The
    /// constructor doesn't call <see cref="JsonFileAnnotationLayer.LoadAsync"/>
    /// — Phase-3 reads tolerated the lazy load by treating an empty
    /// cache as the empty file. A Phase-4 write needs the existing
    /// entries to be in the cache first, otherwise the
    /// <see cref="JsonFileAnnotationLayer.SaveAsync"/> that follows
    /// would overwrite the file with just the one new entry and silently
    /// drop everything else the user persisted earlier.
    /// </summary>
    private static async Task EnsureFileLayerLoadedAsync(JsonFileAnnotationLayer layer, CancellationToken ct)
    {
        if (layer.IsLoaded) return;
        await layer.LoadAsync(ct);
    }

    private static AnnotationWriteResponse BuildEffectiveAfterWriteResponse(
        LayeredAnnotationStore store, AnnotationKey key)
    {
        var tag = store.GetEffective(key);
        var source = store.GetEffectiveSource(key);
        return new AnnotationWriteResponse(
            Service: key.ServiceId,
            Method: key.MethodId,
            MessageType: key.MessageType,
            JsonPath: key.JsonPath,
            Semantic: tag?.Kind,
            Source: SourceToString(source));
    }
}
