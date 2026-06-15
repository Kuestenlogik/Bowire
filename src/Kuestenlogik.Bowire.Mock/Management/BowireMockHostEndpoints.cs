// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Mock.Management;

/// <summary>
/// "Use as mock" one-click endpoints (#94). Takes a recording id,
/// boots a <see cref="BowireMockHostManager"/>-managed
/// <see cref="MockServer"/> on a free local port, returns the URL the
/// caller can paste into another tool.
/// <para>
/// Recording lookup is dialled through <see cref="IRecordingJsonProvider"/>
/// so this package doesn't depend on the workbench's
/// <c>RecordingStore</c> (internal in core). The standalone tool
/// registers a tiny adapter at startup.
/// </para>
/// </summary>
public static class BowireMockHostEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapBowireMockHostEndpoints(this IEndpointRouteBuilder endpoints, string basePath)
    {
        // GET /api/mock/hosts — list active mocks (for the recordings pane).
        endpoints.MapGet($"{basePath}/api/mock/hosts", (HttpContext ctx) =>
        {
            var mgr = ctx.RequestServices.GetRequiredService<BowireMockHostManager>();
            var hosts = mgr.List().Select(h => new
            {
                mockId = h.MockId,
                recordingId = h.RecordingId,
                label = h.Label,
                port = h.Port,
                url = h.Url,
                startedAt = h.StartedAtUtc,
            }).ToArray();
            return Results.Json(new { hosts }, JsonOptions);
        }).ExcludeFromDescription();

        // POST /api/mock/from-recording — body: { recordingId, label? }.
        // Returns { mockId, url, port } on success.
        endpoints.MapPost($"{basePath}/api/mock/from-recording", async (HttpContext ctx) =>
        {
            FromRecordingRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<FromRecordingRequest>(
                    ctx.Request.Body, JsonOptions, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return ProblemResult("urn:bowire:invalid-input",
                    "Request body isn't valid JSON", 400, ex.Message, ctx.Request.Path);
            }

            if (req is null || string.IsNullOrWhiteSpace(req.RecordingId))
            {
                return ProblemResult("urn:bowire:invalid-input",
                    "'recordingId' is required", 400, null, ctx.Request.Path);
            }

            // Look up the recording via the injected provider. Standalone
            // tool registers an adapter that reads from the workbench's
            // RecordingStore; embedded hosts can plug their own.
            var lookup = ctx.RequestServices.GetService<IRecordingJsonProvider>();
            if (lookup is null)
            {
                return ProblemResult("urn:bowire:mock:no-recording-provider",
                    "No IRecordingJsonProvider registered", 500,
                    "Mock host endpoints require a recording lookup. Standalone tool registers one automatically.",
                    ctx.Request.Path);
            }

            string? recordingJson;
            try { recordingJson = lookup.TryGetRecordingJson(req.RecordingId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
            {
                return ProblemResult("urn:bowire:mock:recording-lookup-failed",
                    "Couldn't read the recording", 500, ex.Message, ctx.Request.Path);
            }

            if (recordingJson is null)
            {
                return ProblemResult("urn:bowire:mock:recording-not-found",
                    $"No recording with id '{req.RecordingId}'", 404, null, ctx.Request.Path);
            }

            var mgr = ctx.RequestServices.GetRequiredService<BowireMockHostManager>();
            try
            {
                var handle = await mgr.StartFromJson(
                    recordingJson,
                    req.RecordingId,
                    req.Label ?? req.RecordingId,
                    ctx.RequestAborted).ConfigureAwait(false);

                return Results.Json(new
                {
                    mockId = handle.MockId,
                    recordingId = handle.RecordingId,
                    label = handle.Label,
                    port = handle.Port,
                    url = handle.Url,
                    startedAt = handle.StartedAtUtc,
                }, JsonOptions);
            }
            catch (IOException ex)
            {
                return ProblemResult("urn:bowire:mock:no-free-port",
                    "No free local port for a new mock host", 503, ex.Message, ctx.Request.Path);
            }
            // Mock host start: ASP.NET host startup + plugin transports.
            // Unbounded failure surface as in the management endpoint.
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return ProblemResult("urn:bowire:mock:start-failed",
                    "Couldn't start the mock host", 500, ex.Message, ctx.Request.Path,
                    extensions: new Dictionary<string, object?> { ["exceptionType"] = ex.GetType().Name });
            }
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/mock/{{mockId}}/stop", async (string mockId, HttpContext ctx) =>
        {
            var mgr = ctx.RequestServices.GetRequiredService<BowireMockHostManager>();
            var ok = await mgr.StopAsync(mockId, ctx.RequestAborted).ConfigureAwait(false);
            return ok
                ? Results.Ok(new { mockId, stopped = true })
                : ProblemResult("urn:bowire:mock:host-not-found",
                    $"No mock host with id '{mockId}'", 404, null, ctx.Request.Path);
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// RFC 7807 result builder local to this file — keeps the Mock
    /// package free of a hard dep on the core helper. Carries the
    /// back-compat `error` alias so legacy frontend code still reads.
    /// </summary>
    private static IResult ProblemResult(string type, string title, int status, string? detail, PathString instance, IDictionary<string, object?>? extensions = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["title"] = title,
            ["status"] = status,
            ["detail"] = detail,
            ["instance"] = instance.HasValue ? instance.Value : null,
            ["error"] = title,
        };
        if (extensions != null)
        {
            foreach (var kv in extensions) body[kv.Key] = kv.Value;
        }
        return Results.Json(body, JsonOptions, contentType: "application/problem+json", statusCode: status);
    }

    private sealed class FromRecordingRequest
    {
        public string? RecordingId { get; init; }
        public string? Label { get; init; }
    }
}

/// <summary>
/// Adapter the mock-host endpoint uses to look up recordings without
/// taking a direct reference on the workbench's internal RecordingStore.
/// Implementations: the standalone tool registers an adapter that
/// reads the canonical <c>~/.bowire/recordings.json</c> envelope;
/// embedded hosts can plug their own.
/// </summary>
public interface IRecordingJsonProvider
{
    /// <summary>Return the verbatim JSON for a single recording, or null when not found.</summary>
    string? TryGetRecordingJson(string recordingId);
}
