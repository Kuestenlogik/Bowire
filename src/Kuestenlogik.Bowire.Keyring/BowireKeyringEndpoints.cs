// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Keyring;

/// <summary>
/// HTTP surface for the optional <c>Kuestenlogik.Bowire.Keyring</c>
/// package (#208 Phase 5). Two endpoints, both under the workbench's
/// auth-gated route group:
/// <list type="bullet">
///   <item><c>GET {base}/api/vars/keyring/status</c> — whether the store
///     read is enabled and which backend is active, so the vars UI can
///     render a "keyring available / disabled" affordance without leaking
///     any values.</item>
///   <item><c>POST {base}/api/vars/keyring</c> — batch-resolve the
///     <c>keyring.*</c> refs a template touched, mirroring the AI
///     package's <c>prefetch</c> shape. The response carries resolved
///     values (for the current in-memory substitution only) plus a
///     redaction-safe error map for refs the store couldn't satisfy.</item>
/// </list>
/// </summary>
public static class BowireKeyringEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>Map the keyring endpoints under <paramref name="basePath"/>.</summary>
    public static IEndpointRouteBuilder MapBowireKeyringEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath = "/bowire")
    {
        // [FromServices] is explicit on the resolver so minimal-API never
        // tries to infer it as a body parameter. Without it, a host that
        // maps these endpoints but hasn't registered the keyring services
        // (e.g. MapBowire without the matching AddBowire service pass)
        // fails to *materialise* the whole endpoint group — the GET would
        // infer a body, which GET forbids — 500-ing every route including
        // the index. With the annotation the binding source is fixed to
        // services, so an absent resolver only errors if this endpoint is
        // actually hit.
        endpoints.MapGet($"{basePath}/api/vars/keyring/status",
            ([FromServices] KeyringResolver resolver) => Results.Json(new
            {
                enabled = resolver.Enabled,
                backend = resolver.BackendId,
            }, JsonOpts)).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/vars/keyring",
            async (HttpContext ctx, [FromServices] KeyringResolver resolver) =>
        {
            if (!resolver.Enabled)
            {
                return Results.Json(new KeyringBatchResponse(
                    false, resolver.BackendId,
                    new Dictionary<string, string>(), new Dictionary<string, string>()), JsonOpts);
            }

            KeyringBatchRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<KeyringBatchRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid JSON: " + ex.Message },
                    JsonOpts, statusCode: 400);
            }

            var refs = body?.Refs;
            if (refs is null || refs.Count == 0)
            {
                return Results.Json(new KeyringBatchResponse(
                    true, resolver.BackendId,
                    new Dictionary<string, string>(), new Dictionary<string, string>()), JsonOpts);
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var errors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var reference in refs)
            {
                if (string.IsNullOrWhiteSpace(reference) || values.ContainsKey(reference)
                    || errors.ContainsKey(reference))
                {
                    continue;
                }
                var result = resolver.Resolve(reference);
                switch (result.Status)
                {
                    case KeyringReadStatus.Found:
                        values[reference] = result.Value ?? string.Empty;
                        break;
                    case KeyringReadStatus.NotFound:
                        errors[reference] = "not found";
                        break;
                    default:
                        // Never echo the backend's raw error verbatim to the
                        // client beyond a short reason — it may name paths.
                        errors[reference] = result.Error ?? "error";
                        break;
                }
            }

            return Results.Json(
                new KeyringBatchResponse(true, resolver.BackendId, values, errors), JsonOpts);
        }).ExcludeFromDescription();

        return endpoints;
    }

    private sealed record KeyringBatchRequest(
        [property: JsonPropertyName("refs")] IReadOnlyList<string>? Refs);

    private sealed record KeyringBatchResponse(
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("backend")] string Backend,
        [property: JsonPropertyName("values")] IReadOnlyDictionary<string, string> Values,
        [property: JsonPropertyName("errors")] IReadOnlyDictionary<string, string> Errors);
}
