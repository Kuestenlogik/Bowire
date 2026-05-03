// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Maps the schema-upload endpoints used by the sidebar's "drop a file"
/// flow — both <c>.proto</c> files (handed to <see cref="ProtoUploadStore"/>
/// and parsed by <see cref="ProtoFileParser"/>) and OpenAPI / Swagger
/// documents (stored as raw text in <see cref="OpenApiUploadStore"/> and
/// parsed by the REST plugin during discovery so the OpenAPI reader
/// dependency stays out of the core assembly).
/// </summary>
internal static class BowireUploadEndpoints
{
    public static IEndpointRouteBuilder MapBowireUploadEndpoints(
        this IEndpointRouteBuilder endpoints, BowireOptions options, string prefix)
    {
        // Upload .proto file content (standalone mode)
        endpoints.MapPost($"/{prefix}/api/proto/upload", async (HttpContext ctx) =>
        {
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync(ctx.RequestAborted);

            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "Empty proto content." });

            var services = ProtoUploadStore.AddAndParse(body);
            return Results.Json(new
            {
                imported = services.Count,
                services = services.Select(s => s.Name).ToList()
            }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        // Clear all uploaded proto files
        endpoints.MapDelete($"/{prefix}/api/proto/upload", () =>
        {
            ProtoUploadStore.Clear();
            return Results.Json(new { cleared = true }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        // Upload an OpenAPI / Swagger document (JSON or YAML). Stored raw and
        // parsed by the REST protocol plugin during discovery, so no OpenAPI
        // reader dependency lives in the core assembly.
        endpoints.MapPost($"/{prefix}/api/openapi/upload", async (HttpContext ctx) =>
        {
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync(ctx.RequestAborted);

            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "Empty OpenAPI document." });

            // Source name for display in the UI — pass via ?name=foo.json query
            var name = ctx.Request.Query["name"].FirstOrDefault();
            var id = OpenApiUploadStore.Add(body, name);

            return Results.Json(new { uploaded = true, id }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapDelete($"/{prefix}/api/openapi/upload", () =>
        {
            OpenApiUploadStore.Clear();
            return Results.Json(new { cleared = true }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }
}
