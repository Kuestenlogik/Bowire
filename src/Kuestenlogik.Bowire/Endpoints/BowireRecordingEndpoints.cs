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
/// Maps the disk-backed recording endpoints. Recordings live at
/// <c>~/.bowire/recordings.json</c> via <see cref="RecordingStore"/>
/// so a captured "scenario" survives browser changes and CLI usage and
/// can be checked into a repo for sharing. The browser still keeps a
/// localStorage cache for instant updates without server round-trips —
/// these endpoints are the source of truth.
/// </summary>
internal static class BowireRecordingEndpoints
{
    public static IEndpointRouteBuilder MapBowireRecordingEndpoints(
        this IEndpointRouteBuilder endpoints, BowireOptions options, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/recordings", (HttpContext ctx) =>
        {
            // #144 Phase 1.6 — pass workspaceId so each workspace's
            // captures land in their own directory tree under
            // ~/.bowire/workspaces/<wsId>/recordings/. Hosts that
            // never adopt workspaces stay on the legacy unscoped
            // path. Frontend reads activeWorkspaceId from prologue
            // and adds it as ?workspaceId=… on every call.
            //
            // #144 Phase 1.8 — manifestOnly=1 returns the same shape
            // but with stepsManifest in place of inlined step
            // bodies. Frontend's 'disk-only' storage mode reads this
            // on init + lazy-fetches step bodies on demand.
            var wsId = ctx.Request.Query["workspaceId"].ToString();
            var manifestOnly = ctx.Request.Query["manifestOnly"].ToString() == "1";
            return Results.Content(
                ChunkedRecordingStore.LoadAll(
                    string.IsNullOrEmpty(wsId) ? null : wsId,
                    manifestOnly),
                "application/json");
        }).ExcludeFromDescription();

        endpoints.MapPut($"{basePath}/api/recordings", async (HttpContext ctx) =>
        {
            var json = await new StreamReader(ctx.Request.Body).ReadToEndAsync(ctx.RequestAborted);
            try
            {
                // Auto-enrich each recording with its source schema
                // from the discovery cache before persisting — closes
                // the mock-as-stand-in loop end-to-end (peer Bowire
                // pointing at a mock built from this recording sees
                // the original contract, not just the recorded slice).
                // Falls back to the raw JSON when enrichment fails so
                // a parse hiccup never blocks a save.
                var enriched = TryEnrichWithSourceSchema(json) ?? json;
                var wsId = ctx.Request.Query["workspaceId"].ToString();
                ChunkedRecordingStore.SaveAll(enriched, string.IsNullOrEmpty(wsId) ? null : wsId);
                return Results.Json(new { saved = true }, BowireEndpointHelpers.JsonOptions);
            }
            catch (JsonException ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Rejected invalid recordings JSON from PUT /api/recordings");
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Request body isn't valid JSON",
                    status: 400,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }
            catch (InvalidOperationException ex)
            {
                // ChunkedRecordingStore throws on per-recording size
                // cap. Surface as 413 Payload Too Large so the UI can
                // distinguish 'too big' from 'malformed'.
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Rejected oversized recording from PUT /api/recordings");
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:recording-too-large",
                    title: "Recording exceeds the configured size cap",
                    status: 413,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }
        }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/recordings", (HttpContext ctx) =>
        {
            var wsId = ctx.Request.Query["workspaceId"].ToString();
            ChunkedRecordingStore.DeleteAll(string.IsNullOrEmpty(wsId) ? null : wsId);
            return Results.Json(new { cleared = true }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        // #144 Phase 1.5 — per-step endpoints. Lets the workbench
        // capture path append one step at a time without rewriting
        // the whole document, and lets the detail-view / replay
        // path lazy-fetch a single step body on demand.
        endpoints.MapGet($"{basePath}/api/recordings/{{id}}/manifest", (string id, HttpContext ctx) =>
        {
            var wsId = ctx.Request.Query["workspaceId"].ToString();
            var json = ChunkedRecordingStore.LoadManifest(id, string.IsNullOrEmpty(wsId) ? null : wsId);
            if (json is null)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:recording-not-found",
                    title: "Recording not found",
                    status: 404,
                    detail: $"No recording with id '{id}' on disk.",
                    instance: null);
            }
            return Results.Content(json, "application/json");
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/recordings/{{id}}/step/{{n:int}}", (string id, int n, HttpContext ctx) =>
        {
            var wsId = ctx.Request.Query["workspaceId"].ToString();
            var json = ChunkedRecordingStore.LoadStep(id, n, string.IsNullOrEmpty(wsId) ? null : wsId);
            if (json is null)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:step-not-found",
                    title: "Recording step not found",
                    status: 404,
                    detail: $"No step {n} in recording '{id}'.",
                    instance: null);
            }
            return Results.Content(json, "application/json");
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/recordings/{{id}}/step", async (string id, HttpContext ctx) =>
        {
            string body;
            try
            {
                body = await new StreamReader(ctx.Request.Body).ReadToEndAsync(ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Failed to read request body",
                    status: 400,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }
            try
            {
                // Accept either { step: {...}, metadata?: {...} }
                // (preferred — explicit shape) or just a bare step
                // object (lenient). The bare form skips metadata
                // seeding which means the recording's name etc. must
                // come from a prior PUT.
                var doc = System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject
                          ?? throw new JsonException("Request body must be a JSON object.");
                System.Text.Json.Nodes.JsonObject step;
                System.Text.Json.Nodes.JsonObject? metadata = null;
                if (doc["step"] is System.Text.Json.Nodes.JsonObject explicitStep)
                {
                    step = explicitStep;
                    metadata = doc["metadata"] as System.Text.Json.Nodes.JsonObject;
                }
                else
                {
                    step = doc;
                }
                var wsId = ctx.Request.Query["workspaceId"].ToString();
                var idx = ChunkedRecordingStore.AppendStep(id, step, metadata,
                    string.IsNullOrEmpty(wsId) ? null : wsId);
                return Results.Json(new { appended = true, stepIndex = idx },
                    BowireEndpointHelpers.JsonOptions);
            }
            catch (JsonException ex)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Request body isn't valid JSON",
                    status: 400,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }
            catch (InvalidOperationException ex)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:recording-too-large",
                    title: "Recording exceeds the configured size cap",
                    status: 413,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// For every recording in the wrapper that doesn't already carry a
    /// <c>sourceSchema</c>, look one up in <see cref="SourceSchemaCache"/>
    /// by the first step's <c>serverUrl</c> and stamp it on the
    /// recording. Returns the enriched JSON when at least one stamp
    /// happened (or when shape is recognised but nothing matched —
    /// idempotent), or <c>null</c> on parse failure so the caller can
    /// fall back to persisting the raw bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Operates on the wire shape directly via <c>JsonNode</c>
    /// rather than round-tripping through the typed
    /// <see cref="BowireRecording"/> model, because the model trims a
    /// few unknown-field-tolerated paths (frame-semantics
    /// interpretations, future fields) that we'd silently lose on
    /// re-serialisation. The JsonNode walk preserves every byte the
    /// workbench sent and only adds the new <c>sourceSchema</c> key.
    /// </para>
    /// <para>
    /// Lookup strategy: take the recording's first
    /// <c>steps[].serverUrl</c> and consult the cache. We tried two
    /// keys at <c>SourceSchemaCache.Set</c> time (the discovery URL
    /// itself, and each resolved server URL for AsyncAPI sources),
    /// so a single exact-match lookup catches both REST and
    /// AsyncAPI-driven recordings.
    /// </para>
    /// </remarks>
    internal static string? TryEnrichWithSourceSchema(string json)
    {
        System.Text.Json.Nodes.JsonNode? root;
        try { root = System.Text.Json.Nodes.JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (root is not System.Text.Json.Nodes.JsonObject obj) return null;

        // Two shapes accepted: a single bare recording, or the
        // recordings-store wrapper { "recordings": [...] }.
        var changed = false;
        if (obj["recordings"] is System.Text.Json.Nodes.JsonArray arr)
        {
            foreach (var entry in arr)
            {
                if (entry is System.Text.Json.Nodes.JsonObject rec && StampSourceSchema(rec))
                    changed = true;
            }
        }
        else if (StampSourceSchema(obj))
        {
            changed = true;
        }
        return changed ? root.ToJsonString(EnrichedJsonOptions) : json;
    }

    private static readonly JsonSerializerOptions EnrichedJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static bool StampSourceSchema(System.Text.Json.Nodes.JsonObject recording)
    {
        // Don't overwrite a sourceSchema the workbench (or an older
        // capture pass) already wrote — most recent wins, but a
        // freshly-written entry from the JS side trumps the discovery
        // cache.
        if (recording["sourceSchema"] is not null) return false;

        if (recording["steps"] is not System.Text.Json.Nodes.JsonArray steps || steps.Count == 0)
            return false;

        // Walk the steps until we find the first non-empty serverUrl.
        // Most recordings carry one serverUrl across all steps; the
        // walk just guards against an early step missing the field.
        string? serverUrl = null;
        foreach (var step in steps)
        {
            if (step is System.Text.Json.Nodes.JsonObject so
                && so["serverUrl"]?.GetValue<string>() is { Length: > 0 } url)
            {
                serverUrl = url;
                break;
            }
        }
        if (string.IsNullOrEmpty(serverUrl)) return false;

        var schema = SourceSchemaCache.Get(serverUrl);
        if (schema is null) return false;

        recording["sourceSchema"] = new System.Text.Json.Nodes.JsonObject
        {
            ["format"] = schema.Format,
            ["content"] = schema.Content,
            ["sourceUrl"] = schema.SourceUrl,
        };
        return true;
    }
}
