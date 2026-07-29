// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Recordings.Correlation;

/// <summary>
/// The workbench half of the correlated timeline (#539) — one stateless
/// <c>POST {basePath}/api/recordings/correlate</c>.
///
/// <para>
/// Stateless on purpose. The chunked recording store is internal to
/// core and this package is not on its <c>InternalsVisibleTo</c> list,
/// so a load-by-id variant is not available without widening core's
/// surface; and posting the document also covers the case the id form
/// never could — an in-progress capture that has not been flushed to
/// disk yet.
/// </para>
/// </summary>
internal static class RecordingCorrelationEndpoints
{
    // Mirrors BowireEndpointHelpers.JsonOptions (internal to core):
    // camelCase, nulls dropped, relaxed escaping so non-ASCII payload
    // values stay readable in the response.
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IEndpointRouteBuilder MapBowireRecordingCorrelationEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost($"{basePath}/api/recordings/correlate", async (HttpContext ctx) =>
        {
            CorrelateRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<CorrelateRequest>(
                    ctx.Request.Body, s_json, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Problem(
                    "urn:bowire:invalid-input",
                    "Request body isn't valid JSON",
                    400,
                    ex.Message,
                    ctx.Request.Path);
            }

            if (request?.Recording is null)
            {
                return Problem(
                    "urn:bowire:invalid-input",
                    "Request body must carry a `recording` object",
                    400,
                    "POST { \"recording\": <recording>, \"key\": { \"name\", \"value\" } | null }.",
                    ctx.Request.Path);
            }

            var key = request.Key is null
                      || string.IsNullOrWhiteSpace(request.Key.Name)
                      || string.IsNullOrWhiteSpace(request.Key.Value)
                ? null
                : new RecordingCorrelationKey(
                    request.Key.Name,
                    request.Key.Value,
                    RecordingCorrelationAnalyzer.ResolveSource(request.Key.Name));

            var timeline = RecordingCorrelationAnalyzer.Analyze(request.Recording, key);
            return Results.Json(timeline, s_json);
        }).ExcludeFromDescription();

        return endpoints;
    }

    // RFC7807-shaped, matching the body every other Bowire endpoint
    // emits. Hand-rolled because core's Problem helper is internal.
    private static IResult Problem(string type, string title, int status, string? detail, string instance)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["title"] = title,
            ["status"] = status,
            ["detail"] = detail,
            ["instance"] = instance,
        };
        return Results.Json(body, s_json, contentType: "application/problem+json", statusCode: status);
    }

    private sealed class CorrelateRequest
    {
        public BowireRecording? Recording { get; set; }
        public CorrelateKey? Key { get; set; }
    }

    private sealed class CorrelateKey
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
    }
}
