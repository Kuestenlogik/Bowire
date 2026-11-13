// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Maps the schema-upload endpoints used by the sidebar's "drop a file"
/// flow — <c>.proto</c> files (handed to <see cref="ProtoUploadStore"/>
/// and parsed by <see cref="ProtoFileParser"/>), compiled descriptor sets
/// (<see cref="ProtoSetUploadStore"/>, read by the gRPC plugin) and OpenAPI / Swagger
/// documents (stored as raw text in <see cref="OpenApiUploadStore"/> and
/// parsed by the REST plugin during discovery so the OpenAPI reader
/// dependency stays out of the core assembly).
/// </summary>
internal static class BowireUploadEndpoints
{
    public static IEndpointRouteBuilder MapBowireUploadEndpoints(
        this IEndpointRouteBuilder endpoints, BowireOptions options, string basePath)
    {
        // Upload .proto file content (standalone mode)
        endpoints.MapPost($"{basePath}/api/proto/upload", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);

            if (string.IsNullOrWhiteSpace(body))
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Empty proto content",
                    status: 400,
                    detail: "POST the raw .proto file contents as the request body (Content-Type: text/plain).",
                    instance: ctx.Request.Path);

            // #654 — the same ?name= the OpenAPI route already took. It is
            // what the document is stored under, and a schema named
            // routeguide.proto in a git-native workspace is the difference
            // between a reviewable diff and an opaque one.
            var services = ProtoUploadStore.AddAndParse(body, ctx.Request.Query["name"].FirstOrDefault());
            return Results.Json(new
            {
                imported = services.Count,
                services = services.Select(s => s.Name).ToList()
            }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        // Clear all uploaded proto files
        endpoints.MapDelete($"{basePath}/api/proto/upload", () =>
        {
            ProtoUploadStore.Clear();
            return Results.Json(new { cleared = true }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        // Upload a compiled protobuf descriptor set (#991): the kind a call
        // can be made through, where a .proto only gives names. Binary, so
        // the body is taken as bytes; the gRPC plugin reads it, because core
        // does not carry the protobuf runtime.
        endpoints.MapPost($"{basePath}/api/protoset/upload", async (HttpContext ctx) =>
        {
            using var buffer = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
            var bytes = buffer.ToArray();

            if (bytes.Length == 0)
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Empty descriptor set",
                    status: 400,
                    detail: "POST the descriptor set as the request body: the file "
                        + "`protoc --descriptor_set_out=api.protoset --include_imports` writes.",
                    instance: ctx.Request.Path);

            var registry = BowireEndpointHelpers.GetRegistry();
            var reader = ProtoSetUploadStore.ReaderIn(registry);
            if (reader is null)
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "No plugin reads descriptor sets",
                    status: 400,
                    detail: "A descriptor set is read by the gRPC plugin, and it is not loaded on this host.",
                    instance: ctx.Request.Path);

            IReadOnlyList<Models.BowireServiceInfo> described;
            try
            {
                described = reader.ReadUploadedSchema(bytes);
            }
            catch (FormatException ex)
            {
                // Refused before it is stored: a set that cannot be read would
                // sit in the workspace describing nothing, and the next call
                // would fail far from the upload that caused it.
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Not a descriptor set",
                    status: 400,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }

            ProtoSetUploadStore.Add(bytes, ctx.Request.Query["name"].FirstOrDefault());
            return Results.Json(new
            {
                imported = described.Count,
                services = described.Select(s => s.Name).ToList()
            }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/protoset/upload", () =>
        {
            ProtoSetUploadStore.Clear();
            return Results.Json(new { cleared = true }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        // Upload an OpenAPI / Swagger document (JSON or YAML). Stored raw and
        // parsed by the REST protocol plugin during discovery, so no OpenAPI
        // reader dependency lives in the core assembly.
        endpoints.MapPost($"{basePath}/api/openapi/upload", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);

            if (string.IsNullOrWhiteSpace(body))
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Empty OpenAPI document",
                    status: 400,
                    detail: "POST the raw .json / .yaml OpenAPI document as the request body.",
                    instance: ctx.Request.Path);

            // Source name for display in the UI — pass via ?name=foo.json query
            var name = ctx.Request.Query["name"].FirstOrDefault();
            var id = OpenApiUploadStore.Add(body, name);

            return Results.Json(new { uploaded = true, id }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/openapi/upload", () =>
        {
            OpenApiUploadStore.Clear();
            return Results.Json(new { cleared = true }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }
}
