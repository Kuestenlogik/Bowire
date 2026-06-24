// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Recording;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// REST + SSE surface for the server-side <see cref="BowireRecordingSession"/>
/// (#285). The workbench's recorder UI observes session-state transitions
/// via the SSE channel and drives lifecycle changes through the REST
/// endpoints; the MCP <c>bowire.record.start / stop / replay</c> tools
/// drive the same session directly (no HTTP hop — they take a DI
/// dependency on the singleton).
///
/// <para>
/// Routes:
/// </para>
/// <list type="bullet">
///   <item><c>POST {basePath}/api/recording/session/start</c> — body:
///   <c>{ workspaceId, mode, name?, recordingId? }</c></item>
///   <item><c>POST {basePath}/api/recording/session/stop</c> — flushes
///   the buffer into the recording store, returns the persisted
///   <c>recordingId</c> + step count.</item>
///   <item><c>POST {basePath}/api/recording/session/replay</c> — switches
///   the active session into replay mode.</item>
///   <item><c>GET {basePath}/api/recording/session/status</c> — returns
///   the current session snapshot (or null).</item>
///   <item><c>GET {basePath}/api/recording/session/events</c> — SSE stream
///   of <see cref="BowireRecordingSessionEvent"/> transitions for the
///   workbench's badge updates.</item>
/// </list>
/// </summary>
internal static class BowireRecordingSessionEndpoints
{
    /// <summary>
    /// JSON serialiser used for both REST responses and SSE event payloads.
    /// Camel case so the JS layer can read fields naturally; nulls dropped
    /// to keep the wire small; enum-as-string so a workbench reading the
    /// SSE channel can branch on <c>"capture" / "proxy" / "replay"</c>
    /// without an integer-to-name lookup.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };

    public static IEndpointRouteBuilder MapBowireRecordingSessionEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapPost($"{basePath}/api/recording/session/start", async (HttpContext ctx) =>
        {
            var session = ctx.RequestServices.GetRequiredService<BowireRecordingSession>();
            StartRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<StartRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
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
            if (req is null)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Missing request body",
                    status: 400,
                    detail: "POST /api/recording/session/start requires a JSON body { workspaceId, mode }.",
                    instance: ctx.Request.Path);
            }

            try
            {
                var state = session.Start(
                    workspaceId: req.WorkspaceId ?? string.Empty,
                    mode: req.Mode,
                    name: req.Name,
                    recordingId: req.RecordingId);
                return Results.Json(SnapshotShape(state), JsonOpts);
            }
            catch (InvalidOperationException ex)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:recording-session:already-active",
                    title: "Recording session already active",
                    status: 409,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/recording/session/stop", (HttpContext ctx) =>
        {
            var session = ctx.RequestServices.GetRequiredService<BowireRecordingSession>();
            var recording = session.Stop(flush: rec =>
            {
                PersistRecording(rec);
                return rec;
            });
            if (recording is null)
            {
                return Results.Json(new { stopped = false, reason = "no-active-session" }, JsonOpts);
            }
            return Results.Json(new
            {
                stopped = true,
                recordingId = recording.Id,
                stepCount = recording.Steps.Count,
                name = recording.Name,
            }, JsonOpts);
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/recording/session/replay", (HttpContext ctx) =>
        {
            var session = ctx.RequestServices.GetRequiredService<BowireRecordingSession>();
            try
            {
                var state = session.SwitchToReplay();
                return Results.Json(SnapshotShape(state), JsonOpts);
            }
            catch (InvalidOperationException ex)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:recording-session:not-active",
                    title: "No active recording session",
                    status: 409,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/recording/session/status", (HttpContext ctx) =>
        {
            var session = ctx.RequestServices.GetRequiredService<BowireRecordingSession>();
            var state = session.Active;
            if (state is null)
            {
                return Results.Json(new { active = false, session = (object?)null }, JsonOpts);
            }
            return Results.Json(new { active = true, session = SnapshotShape(state) }, JsonOpts);
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/recording/session/events", async (HttpContext ctx) =>
        {
            var session = ctx.RequestServices.GetRequiredService<BowireRecordingSession>();
            var (reader, subscription) = session.Subscribe();
            using var _ = subscription;

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            // Flush headers immediately so the browser's EventSource
            // transitions CONNECTING → OPEN before the first event lands —
            // matches the pattern in BowireGitWorkspaceEndpoints.
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            // Emit current state on connect so a reconnecting client
            // doesn't have to hit /status separately to know whether a
            // session is open.
            var current = session.Active;
            if (current is not null)
            {
                var payload = JsonSerializer.Serialize(SnapshotShape(current), JsonOpts);
                await ctx.Response.WriteAsync($"event: snapshot\ndata: {payload}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            try
            {
                await foreach (var evt in reader.ReadAllAsync(ctx.RequestAborted))
                {
                    var payload = JsonSerializer.Serialize(EventShape(evt), JsonOpts);
                    var eventName = evt.Kind switch
                    {
                        BowireRecordingSessionEventKind.Started => "started",
                        BowireRecordingSessionEventKind.StepAppended => "step",
                        BowireRecordingSessionEventKind.ModeSwitched => "mode",
                        BowireRecordingSessionEventKind.Stopped => "stopped",
                        _ => "event",
                    };
                    await ctx.Response.WriteAsync($"event: {eventName}\ndata: {payload}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — normal SSE close path.
            }
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// Shape returned on REST + SSE — keeps the wire stable across the
    /// internal <see cref="BowireRecordingSessionState"/> implementation.
    /// </summary>
    internal static object SnapshotShape(BowireRecordingSessionState state) => new
    {
        recordingId = state.RecordingId,
        workspaceId = state.WorkspaceId,
        startedAt = state.StartedAt,
        mode = state.Mode,
        name = state.Name,
        stepCount = state.StepCount,
    };

    private static object EventShape(BowireRecordingSessionEvent evt) => new
    {
        kind = evt.Kind,
        session = evt.Session is null ? null : SnapshotShape(evt.Session),
        stepIndex = evt.StepIndex,
        recordingId = evt.Recording?.Id,
    };

    /// <summary>
    /// Persist a stopped recording into the recording store wrapper file
    /// at <c>~/.bowire/recordings.json</c>. The workbench's existing
    /// localStorage cache stays the fast read path for the UI; this is
    /// the flush sink. Failures are swallowed so a write-permission
    /// hiccup never blocks the stop transition itself.
    /// </summary>
    private static void PersistRecording(BowireRecording recording)
    {
        try
        {
            var current = RecordingStore.Load();
            using var doc = JsonDocument.Parse(current);
            var recordingsArray = new List<JsonElement>();
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("recordings", out var existing)
                && existing.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in existing.EnumerateArray())
                {
                    recordingsArray.Add(r.Clone());
                }
            }

            // Serialize the recording then re-parse so we land inside the
            // same JsonElement universe as the rest of the wrapper. Costs
            // one round-trip but keeps the on-disk shape uniform.
            var serialized = JsonSerializer.Serialize(recording, JsonOpts);
            using var newDoc = JsonDocument.Parse(serialized);

            var output = new
            {
                recordings = recordingsArray.Append(newDoc.RootElement.Clone()).ToArray(),
            };
            RecordingStore.Save(JsonSerializer.Serialize(output, JsonOpts));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Best-effort flush. The session has already been closed; a
            // disk failure can't unwind that. The recording is still
            // returned to the caller via Stop(), so the agent can re-try
            // persistence via a separate PUT if needed.
            _ = ex;
        }
    }

    /// <summary>Incoming body for <c>POST /api/recording/session/start</c>.</summary>
    internal sealed class StartRequest
    {
        public string? WorkspaceId { get; set; }
        public BowireRecordingMode Mode { get; set; } = BowireRecordingMode.Capture;
        public string? Name { get; set; }
        public string? RecordingId { get; set; }
    }
}
