// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
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
            // Stamp the capture time when the client didn't (the workbench form
            // doesn't send it) — parity with the CLI / MCP / flow-capture paths.
            if (rec.CapturedAt == 0) rec.CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

        // #563: flow-capture — run an auth flow (the request body is the flow
        // definition JSON) and store the captured credential as a recording.
        // Optional seam: a host with no IAuthFlowCapturer registered reports 501
        // (static-credential capture via PUT still works). The capturer makes
        // OUTBOUND calls, so this only ever runs on an explicit operator action.
        endpoints.MapPost($"{basePath}/api/auth-recordings/{{id}}/capture", async (string id, HttpContext ctx) =>
        {
            var (workspaceId, storageRoot) = ReadWorkspace(ctx);
            var capturer = ctx.RequestServices.GetService<IAuthFlowCapturer>();
            if (capturer is null)
            {
                return Results.Json(
                    new { error = "Flow-capture isn't available on this host — capture a static credential via PUT instead." },
                    BowireEndpointHelpers.JsonOptions, statusCode: 501);
            }

            using var reader = new StreamReader(ctx.Request.Body);
            var flowJson = await reader.ReadToEndAsync(ctx.RequestAborted);
            if (string.IsNullOrWhiteSpace(flowJson))
                return Problem(ctx, "Missing flow", 400, "The request body must carry the auth-flow definition JSON.");

            AuthFlowCaptureResult captured;
            try
            {
                captured = await capturer.CaptureAsync(flowJson, ctx.RequestAborted);
            }
            catch (AuthFlowCaptureException ex)
            {
                return Problem(ctx, "Auth flow failed", 502, ex.Message);
            }

            var name = ctx.Request.Query["name"].FirstOrDefault();
            var recording = new AuthRecording
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? null : name,
                Scheme = captured.Scheme,
                Header = captured.Header,
                Credential = captured.Credential,
                CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            try
            {
                AuthRecordingStore.Save(workspaceId, storageRoot, recording);
                return Results.Json(new { captured = true, id, scheme = captured.Scheme }, BowireEndpointHelpers.JsonOptions);
            }
            catch (ArgumentException ex)
            {
                return Problem(ctx, "Invalid captured recording", 500, ex.Message);
            }
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// The validated workspace scope for this request.
    /// </summary>
    /// <remarks>
    /// Throws rather than returning a flag so every caller here is covered
    /// without each one growing a branch it could forget — ASP.NET turns a
    /// <see cref="BadHttpRequestException"/> into the 400 this deserves.
    /// Both values reach a file path, and both come off the query string
    /// (cs/path-injection).
    /// </remarks>
    private static (string workspaceId, string? storageRoot) ReadWorkspace(HttpContext ctx)
    {
        var scope = WorkspaceScopeQuery.From(ctx);
        if (scope.IsInvalid) throw new BadHttpRequestException(scope.Error!, StatusCodes.Status400BadRequest);
        return (scope.WorkspaceId ?? string.Empty, scope.StorageRoot);
    }

    private static IResult Problem(HttpContext ctx, string title, int status, string detail)
    {
        return Results.Json(
            new { error = title + ": " + detail },
            BowireEndpointHelpers.JsonOptions,
            statusCode: status);
    }
}
