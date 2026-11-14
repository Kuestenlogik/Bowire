// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Maps the schema-upload endpoints used by the sidebar's "drop a file"
/// flow.
/// </summary>
/// <remarks>
/// <para>
/// <c>POST /api/schema/upload</c> takes any schema file and routes it by
/// kind (#994): a <c>.proto</c> to <see cref="ProtoUploadStore"/>, an
/// OpenAPI document to <see cref="OpenApiUploadStore"/> for the REST plugin,
/// and every other format to the plugin that declares it through
/// <see cref="IBowireUploadedSchemaReader"/>. <c>GET /api/schema/kinds</c>
/// lists what this host imports, which is what the file picker accepts.
/// </para>
/// <para>
/// The three older routes (<c>/api/proto/upload</c>,
/// <c>/api/protoset/upload</c>, <c>/api/openapi/upload</c>) name the kind in
/// the path instead of leaving it to the file name. They run the same import
/// with the kind fixed.
/// </para>
/// </remarks>
internal static class BowireUploadEndpoints
{
    public static IEndpointRouteBuilder MapBowireUploadEndpoints(
        this IEndpointRouteBuilder endpoints, BowireOptions options, string basePath)
    {
        // What this host imports: the picker's filter and the drop zone's hint.
        endpoints.MapGet($"{basePath}/api/schema/kinds", () =>
        {
            var kinds = UploadedSchemaImports.Kinds(BowireEndpointHelpers.GetRegistry());
            return Results.Json(new
            {
                kinds = kinds.Select(k => new
                {
                    kind = k.Kind.Kind,
                    displayName = k.Kind.DisplayName,
                    extensions = k.Kind.Extensions,
                    plugin = k.PluginId,
                }).ToList()
            }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        // Upload a schema file of any kind this host imports (#994). The
        // body is the file as it lies on disk; ?name= is its file name, which
        // is both what it is stored under and what its kind is read from.
        endpoints.MapPost($"{basePath}/api/schema/upload",
            async (HttpContext ctx) =>
            {
                // A block with a return, not an expression body: that one
                // also converts to RequestDelegate, which is then chosen and
                // drops the result, leaving an empty 200.
                return await ImportAsync(ctx, fixedKind: null);
            }).ExcludeFromDescription();

        // Remove every uploaded schema of every kind.
        endpoints.MapDelete($"{basePath}/api/schema/upload", () =>
        {
            UploadedSchemaImports.ClearAll();
            return Results.Json(new { cleared = true }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        // Upload .proto file content (standalone mode)
        endpoints.MapPost($"{basePath}/api/proto/upload",
            async (HttpContext ctx) =>
            {
                return await ImportAsync(ctx, SchemaUploadStore.ProtoKind);
            }).ExcludeFromDescription();

        // Clear all uploaded proto files
        endpoints.MapDelete($"{basePath}/api/proto/upload", () =>
        {
            ProtoUploadStore.Clear();
            return Results.Json(new { cleared = true }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        // Upload a compiled protobuf descriptor set (#991): the kind a call
        // can be made through, where a .proto only gives names.
        endpoints.MapPost($"{basePath}/api/protoset/upload",
            async (HttpContext ctx) =>
            {
                return await ImportAsync(ctx, SchemaUploadStore.ProtoSetKind);
            }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/protoset/upload", () =>
        {
            UploadedSchemaImports.Clear(SchemaUploadStore.ProtoSetKind);
            return Results.Json(new { cleared = true }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        // Upload an OpenAPI / Swagger document (JSON or YAML). Stored raw and
        // parsed by the REST protocol plugin during discovery, so no OpenAPI
        // reader dependency lives in the core assembly.
        endpoints.MapPost($"{basePath}/api/openapi/upload",
            async (HttpContext ctx) =>
            {
                return await ImportAsync(ctx, SchemaUploadStore.OpenApiKind);
            }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/openapi/upload", () =>
        {
            OpenApiUploadStore.Clear();
            return Results.Json(new { cleared = true }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// Read the body, decide its kind, have it read once and store it.
    /// </summary>
    /// <param name="ctx">The upload request.</param>
    /// <param name="fixedKind">
    /// The kind the route names, or null to route by file name and content.
    /// </param>
    private static async Task<IResult> ImportAsync(HttpContext ctx, string? fixedKind)
    {
        using var buffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
        var bytes = buffer.ToArray();

        // #654 — the name is what the document is stored under, and a schema
        // named routeguide.proto in a git-native workspace is the difference
        // between a reviewable diff and an opaque one.
        var name = ctx.Request.Query["name"].FirstOrDefault();
        var registry = BowireEndpointHelpers.GetRegistry();

        if (bytes.Length == 0 || IsBlankText(bytes))
            return Refused(ctx, EmptyTitle(fixedKind), EmptyDetail(fixedKind, registry));

        UploadedSchemaImports.Importable? kind;
        if (fixedKind is null)
        {
            kind = UploadedSchemaImports.Route(registry, name, bytes, out var refusal);
            if (kind is null) return Refused(ctx, "Not a schema this host imports", refusal);
        }
        else
        {
            kind = UploadedSchemaImports.Find(registry, fixedKind) ?? CoreKind(fixedKind);
            if (kind is null)
                return Refused(ctx, "No plugin reads descriptor sets",
                    "A descriptor set is read by the gRPC plugin, and it is not loaded on this host.");
        }

        if (string.Equals(kind.Kind.Kind, SchemaUploadStore.ProtoKind, StringComparison.OrdinalIgnoreCase))
        {
            var services = ProtoUploadStore.AddAndParse(Text(bytes), name);
            return Imported(kind, services.Count, services.Select(s => s.Name));
        }

        if (kind.Reader is null)
        {
            // OpenAPI: stored raw, read by the REST plugin while it
            // discovers, so there is no service count to report yet.
            var id = OpenApiUploadStore.Add(Text(bytes), name);
            return Results.Json(new
            {
                kind = kind.Kind.Kind,
                displayName = kind.Kind.DisplayName,
                plugin = kind.PluginId,
                uploaded = true,
                id,
            }, BowireEndpointHelpers.JsonOptions);
        }

        IReadOnlyList<BowireServiceInfo> described;
        try
        {
            described = kind.Reader.ReadUploadedSchema(kind.Kind.Kind, bytes, name ?? "uploaded");
        }
        catch (FormatException ex)
        {
            // Refused before it is stored: a document that cannot be read
            // would sit in the workspace describing nothing, and the next
            // call would fail far from the upload that caused it.
            return Refused(ctx, NotATitle(kind), ex.Message);
        }

        UploadedSchemaImports.Add(kind, bytes, name);
        return Imported(kind, described.Count, described.Select(s => s.Name));
    }

    private static IResult Imported(UploadedSchemaImports.Importable kind, int count, IEnumerable<string> services)
        => Results.Json(new
        {
            kind = kind.Kind.Kind,
            displayName = kind.Kind.DisplayName,
            plugin = kind.PluginId,
            imported = count,
            services = services.ToList(),
        }, BowireEndpointHelpers.JsonOptions);

    private static IResult Refused(HttpContext ctx, string title, string? detail)
        => BowireEndpointHelpers.Problem(
            type: "urn:bowire:invalid-input",
            title: title,
            status: 400,
            detail: detail,
            instance: ctx.Request.Path);

    /// <summary>
    /// The OpenAPI kind for the route that names it, on a host without the
    /// REST plugin: the document is stored as before and read once the
    /// plugin is there.
    /// </summary>
    private static UploadedSchemaImports.Importable? CoreKind(string kind)
        => string.Equals(kind, SchemaUploadStore.OpenApiKind, StringComparison.OrdinalIgnoreCase)
            ? new UploadedSchemaImports.Importable(
                new BowireUploadedSchemaKind(SchemaUploadStore.OpenApiKind, "OpenAPI / Swagger", [".json", ".yaml", ".yml"]),
                "rest", null)
            : null;

    private static string EmptyTitle(string? fixedKind) => fixedKind switch
    {
        SchemaUploadStore.ProtoKind => "Empty proto content",
        SchemaUploadStore.ProtoSetKind => "Empty descriptor set",
        SchemaUploadStore.OpenApiKind => "Empty OpenAPI document",
        _ => "Empty schema file",
    };

    private static string EmptyDetail(string? fixedKind, BowireProtocolRegistry registry) => fixedKind switch
    {
        SchemaUploadStore.ProtoKind =>
            "POST the raw .proto file contents as the request body (Content-Type: text/plain).",
        SchemaUploadStore.ProtoSetKind =>
            "POST the descriptor set as the request body: the file "
            + "`protoc --descriptor_set_out=api.protoset --include_imports` writes.",
        SchemaUploadStore.OpenApiKind =>
            "POST the raw .json / .yaml OpenAPI document as the request body.",
        _ => "POST the schema file as the request body and its file name as ?name=. This host reads: "
            + UploadedSchemaImports.Describe(UploadedSchemaImports.Kinds(registry)) + ".",
    };

    private static string NotATitle(UploadedSchemaImports.Importable kind)
        => string.Equals(kind.Kind.Kind, SchemaUploadStore.ProtoSetKind, StringComparison.OrdinalIgnoreCase)
            ? "Not a descriptor set"
            : "Not " + kind.Kind.DisplayName;

    /// <summary>
    /// Whether the body is text made of nothing but white space. A binary
    /// document never is: its first bytes are tags, not blanks.
    /// </summary>
    private static bool IsBlankText(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return false;
        }
        return true;
    }

    /// <summary>The body as text, without the byte order mark an editor may have written.</summary>
    private static string Text(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
