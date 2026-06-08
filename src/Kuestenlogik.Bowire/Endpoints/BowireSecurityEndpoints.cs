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
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Request body isn't valid JSON",
                    status: 400,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }

            if (req is null)
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Empty request body",
                    status: 400,
                    instance: ctx.Request.Path);
            if (string.IsNullOrWhiteSpace(req.Target))
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "'target' is required",
                    status: 400,
                    instance: ctx.Request.Path);
            if (string.IsNullOrWhiteSpace(req.Field))
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "'field' is required",
                    status: 400,
                    instance: ctx.Request.Path);
            var hasCustomPayloads = req.CustomPayloads is { Count: > 0 };
            if (!hasCustomPayloads && string.IsNullOrWhiteSpace(req.Category))
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Either 'category' or 'customPayloads' is required",
                    status: 400,
                    instance: ctx.Request.Path);
            // Cap server-side at 50 custom payloads — the workbench
            // already caps at 5 by default but a misbehaving client
            // shouldn't be able to fire a DOS-shaped volley either.
            if (hasCustomPayloads && req.CustomPayloads!.Count > 50)
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:security:payload-cap",
                    title: "customPayloads must be ≤ 50 entries",
                    status: 400,
                    detail: $"Server-side cap to prevent DoS-shaped volleys. You sent {req.CustomPayloads!.Count}. The workbench's default cap is 5 — anything above is a non-default override.",
                    instance: ctx.Request.Path,
                    extensions: new Dictionary<string, object?> { ["count"] = req.CustomPayloads.Count, ["maxCount"] = 50 });

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
                    CustomPayloads = req.CustomPayloads,
                    Force = req.Force,
                    Http = http,
                }, ctx.RequestAborted).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    return BowireEndpointHelpers.Problem(
                        type: "urn:bowire:security:fuzz-rejected",
                        title: "Fuzz request rejected",
                        status: 400,
                        detail: result.ErrorMessage,
                        instance: ctx.Request.Path);

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
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:security:fuzz-error",
                    title: "Fuzz execution failed",
                    status: 500,
                    detail: ex.Message,
                    instance: ctx.Request.Path,
                    extensions: new Dictionary<string, object?> { ["exceptionType"] = ex.GetType().Name });
            }
        }).ExcludeFromDescription();

        // #112 — heuristic threat-model. Deterministic, sub-millisecond,
        // no AI required. Mirrors the response shape /api/ai/threat-model
        // emits so the workbench can render either source with the same
        // code path; adds a `ruleTrace` per row explaining which rules
        // fired. Frontend Security drawer defaults to this and switches
        // to the AI path only when the operator opts in via the "Use
        // AI for ranking" toggle.
        endpoints.MapPost($"{basePath}/api/security/threat-model", async (HttpContext ctx) =>
        {
            ThreatHeuristicRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<ThreatHeuristicRequest>(
                    ctx.Request.Body, s_jsonOpts, ctx.RequestAborted).ConfigureAwait(false);
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

            if (req?.Endpoints is null || req.Endpoints.Length == 0)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "'endpoints' array is required and must be non-empty",
                    status: 400,
                    instance: ctx.Request.Path);
            }

            // Map the wire DTOs to the engine's record shape.
            var input = req.Endpoints.Select(e => new ThreatHeuristic.Endpoint(
                EndpointId: e.EndpointId ?? string.Empty,
                Path: e.Path ?? string.Empty,
                Verb: e.Verb,
                Protocol: e.Protocol,
                Service: e.Service,
                InputShape: e.InputShape,
                AuthState: e.AuthState)).ToArray();

            var topN = req.TopN ?? 10;
            var ranking = ThreatHeuristic.Rank(input, topN);

            return Results.Json(new
            {
                ranked = ranking.Ranked.Select(r => new
                {
                    endpointId = r.EndpointId,
                    risk = r.Risk,
                    why = r.Why,
                    suggestedTemplates = r.SuggestedTemplates,
                    ruleTrace = r.RuleTrace,
                }).ToArray(),
                inputCount = req.Endpoints.Length,
                truncated = req.Endpoints.Length > 200,
                modelId = "heuristic",
                source = "heuristic",
            }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }

    private static string? TrimBody(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        const int cap = 512;
        return body.Length <= cap ? body : body[..cap] + "…";
    }

    /// <summary>Heuristic threat-model request shape (#112). Same fields as the AI threat-model.</summary>
    private sealed class ThreatHeuristicRequest
    {
        public ThreatHeuristicEndpoint[]? Endpoints { get; init; }
        public int? TopN { get; init; }
    }

    private sealed class ThreatHeuristicEndpoint
    {
        public string? EndpointId { get; init; }
        public string? Path { get; init; }
        public string? Verb { get; init; }
        public string? Protocol { get; init; }
        public string? Service { get; init; }
        public string? InputShape { get; init; }
        public string? AuthState { get; init; }
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

        /// <summary>
        /// Caller-supplied payloads (#62). When set + non-empty, takes
        /// priority over <see cref="Category"/>; the value-shape skip
        /// guard is bypassed since the user picked them deliberately.
        /// Set by the AI fuzz-values frontend after the user picks
        /// ≤ 5 from the model's suggested 20.
        /// </summary>
        public List<string>? CustomPayloads { get; init; }

        public bool Force { get; init; }
        public bool AllowSelfSignedCerts { get; init; }
        public int TimeoutSeconds { get; init; } = 30;
    }
}
