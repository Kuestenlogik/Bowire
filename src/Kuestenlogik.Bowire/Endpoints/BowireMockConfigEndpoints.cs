// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Disk-backed mock-configuration endpoints (#558). A schema mock's
/// refinement sidecar (per-field overrides + conditional rules + auth
/// requirement) is persisted per (workspace, mock) via
/// <see cref="MockConfigStore"/>; the workbench reads and writes it here so
/// the on-disk <c>mocks/&lt;mockId&gt;.json</c> is the source of truth that
/// survives browser resets, rides the workspace export, and syncs via git.
/// Mirrors <see cref="BowirePresetEndpoints"/>.
/// </summary>
internal static class BowireMockConfigEndpoints
{
    public static IEndpointRouteBuilder MapBowireMockConfigEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/mocks/{{mockId}}/config", (string mockId, HttpContext ctx) =>
        {
            var (workspaceId, storageRoot) = ReadWorkspace(ctx);
            try
            {
                var json = MockConfigStore.Load(workspaceId, storageRoot, mockId);
                return Results.Content(json, "application/json");
            }
            catch (ArgumentException ex)
            {
                return Problem(ctx, "Invalid mock-config request", 400, ex.Message);
            }
        }).ExcludeFromDescription();

        endpoints.MapPut($"{basePath}/api/mocks/{{mockId}}/config", async (string mockId, HttpContext ctx) =>
        {
            var (workspaceId, storageRoot) = ReadWorkspace(ctx);

            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            try
            {
                MockConfigStore.Save(workspaceId, storageRoot, mockId, body);
                return Results.Json(new { saved = true }, BowireEndpointHelpers.JsonOptions);
            }
            catch (JsonException ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Rejected invalid mock-config JSON for mock {MockId}",
                    BowireEndpointHelpers.SafeLog(mockId));
                return Problem(ctx, "Invalid JSON", 400, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Problem(ctx, "Invalid mock-config payload", 400, ex.Message);
            }
        }).ExcludeFromDescription();

        // #563: list a workspace's captured auth recordings for the auth-card
        // picker — credential-free summaries only (the token never leaves the
        // store). List never throws; a missing directory yields an empty list.
        endpoints.MapGet($"{basePath}/api/auth-recordings", (HttpContext ctx) =>
        {
            var (workspaceId, storageRoot) = ReadWorkspace(ctx);
            var recordings = AuthRecordingStore.List(workspaceId, storageRoot)
                .Select(r => new { id = r.Id, name = r.Name, scheme = r.Scheme, capturedAt = r.CapturedAt });
            return Results.Json(new { recordings }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        // #563: create/update a captured auth recording (static-credential
        // capture). The URL owns the id; Save rejects a missing/empty credential
        // so the gate can't be silently weakened to presence-only.
        endpoints.MapPut($"{basePath}/api/auth-recordings/{{id}}", async (string id, HttpContext ctx) =>
        {
            var (workspaceId, storageRoot) = ReadWorkspace(ctx);
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            AuthRecording rec;
            try
            {
                rec = AuthRecording.Parse(body);
            }
            catch (JsonException ex)
            {
                return Problem(ctx, "Invalid JSON", 400, ex.Message);
            }
            rec.Id = id; // the URL owns the id, not the body
            try
            {
                AuthRecordingStore.Save(workspaceId, storageRoot, rec);
                return Results.Json(new { saved = true, id }, BowireEndpointHelpers.JsonOptions);
            }
            catch (ArgumentException ex)
            {
                return Problem(ctx, "Invalid auth-recording payload", 400, ex.Message);
            }
        }).ExcludeFromDescription();

        // #563: delete a captured auth recording. Idempotent — reports whether a
        // file was actually removed.
        endpoints.MapDelete($"{basePath}/api/auth-recordings/{{id}}", (string id, HttpContext ctx) =>
        {
            var (workspaceId, storageRoot) = ReadWorkspace(ctx);
            var deleted = AuthRecordingStore.Delete(workspaceId, storageRoot, id);
            return Results.Json(new { deleted, id }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }

    private static (string workspaceId, string? storageRoot) ReadWorkspace(HttpContext ctx)
    {
        var workspaceId = ctx.Request.Query["workspaceId"].FirstOrDefault() ?? string.Empty;
        var storageRoot = ctx.Request.Query["storageRoot"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(storageRoot)) storageRoot = null;
        return (workspaceId, storageRoot);
    }

    private static IResult Problem(HttpContext ctx, string title, int status, string detail)
    {
        return Results.Json(
            new { error = title + ": " + detail },
            BowireEndpointHelpers.JsonOptions,
            statusCode: status);
    }
}
