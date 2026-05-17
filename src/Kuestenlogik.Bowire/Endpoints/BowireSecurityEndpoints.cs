// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Security-testing endpoints — the server-side surface the workbench's
/// right-click "Fuzz this field" menu calls into. Mirrors the
/// <c>bowire fuzz</c> CLI subcommand but takes its inputs over HTTP +
/// returns the result as JSON instead of writing a console table.
/// </summary>
/// <remarks>
/// <para>
/// Single endpoint today: <c>POST /api/security/fuzz</c>. Future
/// neighbours (e.g. <c>/api/security/scan</c> for in-workbench scan
/// runs, <c>/api/security/probe</c> for one-off check probing) land
/// here too.
/// </para>
/// </remarks>
internal static class BowireSecurityEndpoints
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5400:HttpClient may be created without enabling CheckCertificateRevocationList",
        Justification = "CRL toggle is set explicitly inside the conditional below based on the operator's --allow-self-signed-certs choice.")]
    public static IEndpointRouteBuilder MapBowireSecurityEndpoints(this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapPost($"{basePath}/api/security/fuzz", async (HttpContext ctx) =>
        {
            FuzzApiRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<FuzzApiRequest>(ctx.Request.Body, s_jsonOpts, ctx.RequestAborted)
                       .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"Could not parse request body: {ex.Message}" });
            }

            if (req is null)
                return Results.BadRequest(new { error = "Empty request body." });
            if (string.IsNullOrWhiteSpace(req.Target))
                return Results.BadRequest(new { error = "target is required." });
            if (string.IsNullOrWhiteSpace(req.Field))
                return Results.BadRequest(new { error = "field is required." });
            if (string.IsNullOrWhiteSpace(req.Category))
                return Results.BadRequest(new { error = "category is required." });

            // Lightweight HttpClient per call — AllowAutoRedirect off
            // for the same scope-safety reason ScanCommand uses; the
            // workbench-side caller already validated the target.
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            if (req.AllowSelfSignedCerts)
            {
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            }
            else
            {
                handler.CheckCertificateRevocationList = true;
            }
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(req.TimeoutSeconds > 0 ? req.TimeoutSeconds : 30) };

            try
            {
                var result = await FuzzExecutor.RunAsync(new FuzzExecutorRequest
                {
                    Target = req.Target,
                    HttpVerb = req.HttpVerb,
                    HttpPath = req.HttpPath,
                    Body = req.Body ?? "",
                    Headers = req.Headers,
                    Field = req.Field,
                    Category = req.Category,
                    Force = req.Force,
                    Http = http,
                }, ctx.RequestAborted).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    return Results.BadRequest(new { error = result.ErrorMessage });

                // Project the result down to a JSON-friendly DTO so the
                // workbench-side renderer doesn't have to know about
                // AttackProbeResponse internals (headers map, body text
                // size — we just send a body excerpt).
                return Results.Json(new
                {
                    baselineStatus = result.BaselineStatus,
                    baselineLatencyMs = result.BaselineLatencyMs,
                    baselineBodySize = result.BaselineBodySize,
                    rows = result.Rows.Select(r => new
                    {
                        payload = r.Payload,
                        outcome = r.Outcome.ToString(),
                        detail = r.Detail,
                        status = r.Response?.Status,
                        latencyMs = r.Response?.LatencyMs,
                        bodySize = r.Response?.Body.Length,
                        bodyExcerpt = TrimBody(r.Response?.Body),
                    }).ToArray(),
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).ExcludeFromDescription();

        return endpoints;
    }

    private static string? TrimBody(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        const int cap = 512;
        return body.Length <= cap ? body : body[..cap] + "…";
    }

    /// <summary>JSON DTO for the POST body. Matches the workbench-side request shape byte-for-byte.</summary>
    private sealed class FuzzApiRequest
    {
        public string Target { get; init; } = "";
        public string? HttpVerb { get; init; }
        public string? HttpPath { get; init; }
        public string? Body { get; init; }
        public Dictionary<string, string>? Headers { get; init; }
        public string Field { get; init; } = "";
        public string Category { get; init; } = "";
        public bool Force { get; init; }
        public bool AllowSelfSignedCerts { get; init; }
        public int TimeoutSeconds { get; init; } = 30;
    }
}
