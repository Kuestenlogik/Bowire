// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mock.Management;

/// <summary>
/// HTTP API surface for managing UI-driven mock servers, under
/// <c>{basePath}/api/mocks</c>:
/// <list type="bullet">
///   <item><c>POST /api/mocks</c> — start a mock. Accepts either an
///     inline recording payload (<c>{ recording, name?, port? }</c> —
///     the legacy embedded-host shape) OR a recording-id lookup
///     (<c>{ recordingId, label? }</c> — the "Use as mock" shape).</item>
///   <item><c>GET /api/mocks</c> — list running mocks.</item>
///   <item><c>GET /api/mocks/{id}</c> — single mock detail.</item>
///   <item><c>DELETE /api/mocks/{id}</c> — stop a mock.</item>
///   <item><c>GET /api/mocks/{id}/requests</c> — request-log tail (#57).</item>
///   <item><c>GET /api/mocks/{id}/requests/unmatched</c> — near-miss listing (#409).</item>
///   <item><c>POST /api/mocks/{id}/verify</c> — verify / findAll over the journal (#409).</item>
///   <item><c>GET</c>/<c>PUT /api/mocks/{id}/faults</c> — live fault rules (#170).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>One owner — <see cref="BowireMockHostManager"/>. Replaces the
/// v1.x split with a separate <c>MockRegistry</c> + the v2.x
/// host-manager-only <c>/api/mock/*</c> surface (#223).</para>
/// <para>Opt-in: embedded hosts that pull in <c>Kuestenlogik.Bowire.Mock</c>
/// call <c>builder.Services.AddBowireMockManagement()</c> +
/// <c>app.MapBowireMockManagement()</c> alongside the usual
/// <c>AddBowire()</c> / <c>MapBowire()</c>. Standalone <c>bowire</c>
/// CLI wires both automatically.</para>
/// </remarks>
public static class BowireMockManagementEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Mount the mock-management endpoints under <paramref name="basePath"/>
    /// (typically <c>"/bowire"</c> for the workbench mount).
    /// </summary>
    public static IEndpointRouteBuilder MapBowireMockManagement(
        this IEndpointRouteBuilder endpoints, string basePath = "/bowire")
    {
        endpoints.MapGet($"{basePath}/api/mocks", (BowireMockHostManager manager) =>
        {
            var snapshot = manager.List()
                .OrderByDescending(h => h.StartedAtUtc)
                .Select(MockSummary.From)
                .ToArray();
            return Results.Json(new { mocks = snapshot }, JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/mocks/{{mockId}}",
            (string mockId, BowireMockHostManager manager) =>
        {
            var handle = manager.Get(mockId);
            return handle is null
                ? Results.NotFound(new { error = $"Mock {mockId} not running." })
                : Results.Json(MockSummary.From(handle), JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/mocks",
            async (HttpContext ctx, BowireMockHostManager manager) =>
        {
            StartMockRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<StartMockRequest>(
                    ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid JSON: " + ex.Message },
                    JsonOptions, statusCode: 400);
            }
            if (req is null)
            {
                return Results.Json(new { error = "Request body is required." },
                    JsonOptions, statusCode: 400);
            }

            // Two start shapes; pick based on which field arrived.
            //   { recording, name?, port? }    — inline payload (legacy + embedded)
            //   { recordingId, label? }        — lookup via IRecordingJsonProvider ("Use as mock")
            string? recordingJson;
            string recordingId;
            string label;

            if (!string.IsNullOrWhiteSpace(req.RecordingId))
            {
                var lookup = ctx.RequestServices.GetService<IRecordingJsonProvider>();
                if (lookup is null)
                {
                    return Results.Json(
                        new { error = "No IRecordingJsonProvider registered — embedded hosts must pass `recording` inline." },
                        JsonOptions, statusCode: 500);
                }
                try { recordingJson = lookup.TryGetRecordingJson(req.RecordingId); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    return Results.Json(
                        new { error = "Couldn't read the recording: " + ex.Message },
                        JsonOptions, statusCode: 500);
                }
                if (recordingJson is null)
                {
                    return Results.Json(
                        new { error = $"No recording with id '{req.RecordingId}'" },
                        JsonOptions, statusCode: 404);
                }
                recordingId = req.RecordingId;
                label = string.IsNullOrWhiteSpace(req.Label) ? req.RecordingId : req.Label!;
            }
            else if (!string.IsNullOrWhiteSpace(req.Recording))
            {
                recordingJson = req.Recording;
                recordingId = string.Empty;
                label = string.IsNullOrWhiteSpace(req.Name) ? "unnamed" : req.Name!;
            }
            else
            {
                return Results.Json(
                    new { error = "Body must carry either `recording` (inline JSON) or `recordingId` (lookup)." },
                    JsonOptions, statusCode: 400);
            }

            var port = req.Port ?? 0; // 0 = OS-assigned via the rolling allocator

#pragma warning disable CA1031 // mock-start spawns the full host pipeline; unbounded failure surface
            try
            {
                var handle = await manager.StartAsync(recordingJson!, recordingId, label, port, ctx.RequestAborted);
                return Results.Json(MockSummary.From(handle), JsonOptions, statusCode: 201);
            }
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ctx.RequestServices.GetService<ILoggerFactory>()
                    ?.CreateLogger("BowireMockManagement")
                    .LogError(ex, "Failed to start mock from POST /api/mocks");
                return Results.Json(new { error = ex.Message },
                    JsonOptions, statusCode: 500);
            }
        }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/mocks/{{mockId}}",
            async (string mockId, BowireMockHostManager manager, HttpContext ctx) =>
        {
            var stopped = await manager.StopAsync(mockId, ctx.RequestAborted);
            return stopped
                ? Results.NoContent()
                : Results.NotFound(new { error = $"Mock {mockId} not running." });
        }).ExcludeFromDescription();

        // #57: per-mock request log. Query params:
        //   limit         -> max entries (default 100, capped at log capacity)
        //   since         -> minimum sequence number (>0 means "give me only
        //                    entries newer than the cursor I last saw")
        endpoints.MapGet($"{basePath}/api/mocks/{{mockId}}/requests",
            (string mockId, int? limit, long? since, BowireMockHostManager manager) =>
        {
            var log = manager.GetRequestLog(mockId);
            if (log is null)
            {
                return Results.NotFound(new { error = $"Mock {mockId} not running." });
            }
            var entries = log.Snapshot(limit ?? 100, since ?? 0);
            return Results.Json(new
            {
                mockId,
                total = log.TotalRequests,
                capacity = log.Capacity,
                entries
            }, JsonOptions);
        }).ExcludeFromDescription();

        // #409: near-miss listing — requests that matched no stub.
        endpoints.MapGet($"{basePath}/api/mocks/{{mockId}}/requests/unmatched",
            (string mockId, int? limit, BowireMockHostManager manager) =>
        {
            var log = manager.GetRequestLog(mockId);
            if (log is null)
            {
                return Results.NotFound(new { error = $"Mock {mockId} not running." });
            }
            var entries = log.Unmatched(limit);
            return Results.Json(new { mockId, count = entries.Count, entries }, JsonOptions);
        }).ExcludeFromDescription();

        // #409: verify / findAll — assert how many journalled requests match a
        // predicate (method + path/regex/glob + query/header/cookie). Returns
        // { satisfied, count, matches }. `satisfied` defaults to "at least one"
        // when no count expectation is supplied.
        endpoints.MapPost($"{basePath}/api/mocks/{{mockId}}/verify",
            async (string mockId, HttpContext ctx, BowireMockHostManager manager) =>
        {
            var log = manager.GetRequestLog(mockId);
            if (log is null)
            {
                return Results.NotFound(new { error = $"Mock {mockId} not running." });
            }
            MockVerification? verification;
            try
            {
                verification = await JsonSerializer.DeserializeAsync<MockVerification>(
                    ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid JSON: " + ex.Message }, JsonOptions, statusCode: 400);
            }
            if (verification is null)
            {
                return Results.Json(new { error = "Request body is required." }, JsonOptions, statusCode: 400);
            }

            var result = log.Verify(verification);
            return Results.Json(new
            {
                mockId,
                satisfied = result.Satisfied,
                count = result.Count,
                matches = result.Matches,
            }, JsonOptions);
        }).ExcludeFromDescription();

        // #404: per-stub CRUD on a RUNNING mock. A "stub" is a recording step
        // (BowireRecordingStep) — it carries the #402/#403 `match` predicates +
        // the response fields. Lets an operator author / edit / remove
        // individual stubs at runtime instead of restarting the mock.
        endpoints.MapGet($"{basePath}/api/mocks/{{mockId}}/stubs",
            (string mockId, BowireMockHostManager manager) =>
        {
            var stubs = manager.GetStubs(mockId);
            return stubs is null
                ? Results.NotFound(new { error = $"Mock {mockId} not running." })
                : Results.Json(new { mockId, count = stubs.Count, stubs }, JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/mocks/{{mockId}}/stubs/{{stubId}}",
            (string mockId, string stubId, BowireMockHostManager manager) =>
        {
            if (manager.Get(mockId) is null)
                return Results.NotFound(new { error = $"Mock {mockId} not running." });
            var stub = manager.GetStub(mockId, stubId);
            return stub is null
                ? Results.NotFound(new { error = $"No stub '{stubId}'." })
                : Results.Json(stub, JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/mocks/{{mockId}}/stubs",
            async (string mockId, HttpContext ctx, BowireMockHostManager manager) =>
        {
            if (manager.Get(mockId) is null)
                return Results.NotFound(new { error = $"Mock {mockId} not running." });
            var stub = await ReadStubAsync(ctx);
            if (stub is null)
                return Results.Json(new { error = "Body must be a stub (recording step) JSON object." }, JsonOptions, statusCode: 400);
            var created = manager.AddStub(mockId, stub);
            return Results.Json(created, JsonOptions, statusCode: 201);
        }).ExcludeFromDescription();

        endpoints.MapPut($"{basePath}/api/mocks/{{mockId}}/stubs/{{stubId}}",
            async (string mockId, string stubId, HttpContext ctx, BowireMockHostManager manager) =>
        {
            if (manager.Get(mockId) is null)
                return Results.NotFound(new { error = $"Mock {mockId} not running." });
            var stub = await ReadStubAsync(ctx);
            if (stub is null)
                return Results.Json(new { error = "Body must be a stub (recording step) JSON object." }, JsonOptions, statusCode: 400);
            return manager.UpdateStub(mockId, stubId, stub)
                ? Results.Json(manager.GetStub(mockId, stubId), JsonOptions)
                : Results.NotFound(new { error = $"No stub '{stubId}'." });
        }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/mocks/{{mockId}}/stubs/{{stubId}}",
            (string mockId, string stubId, BowireMockHostManager manager) =>
        {
            if (manager.Get(mockId) is null)
                return Results.NotFound(new { error = $"Mock {mockId} not running." });
            return manager.RemoveStub(mockId, stubId)
                ? Results.NoContent()
                : Results.NotFound(new { error = $"No stub '{stubId}'." });
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/mocks/{{mockId}}/stubs/reset",
            (string mockId, BowireMockHostManager manager) =>
        {
            return manager.ResetStubs(mockId)
                ? Results.Json(new { mockId, stubs = manager.GetStubs(mockId) }, JsonOptions)
                : Results.NotFound(new { error = $"Mock {mockId} not running." });
        }).ExcludeFromDescription();

        // #408: named-scenario state on a RUNNING mock.
        endpoints.MapGet($"{basePath}/api/mocks/{{mockId}}/scenarios",
            (string mockId, BowireMockHostManager manager) =>
        {
            var states = manager.GetScenarioStates(mockId);
            return states is null
                ? Results.NotFound(new { error = $"Mock {mockId} not running." })
                : Results.Json(new { mockId, scenarios = states }, JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/mocks/{{mockId}}/scenarios/{{name}}/state",
            async (string mockId, string name, HttpContext ctx, BowireMockHostManager manager) =>
        {
            if (manager.Get(mockId) is null)
                return Results.NotFound(new { error = $"Mock {mockId} not running." });
            ScenarioStateRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<ScenarioStateRequest>(
                    ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid JSON: " + ex.Message }, JsonOptions, statusCode: 400);
            }
            if (string.IsNullOrEmpty(body?.State))
                return Results.Json(new { error = "Body must be { \"state\": \"...\" }." }, JsonOptions, statusCode: 400);
            return manager.SetScenarioState(mockId, name, body.State)
                ? Results.Json(new { mockId, scenarios = manager.GetScenarioStates(mockId) }, JsonOptions)
                : Results.NotFound(new { error = $"No scenario '{name}'." });
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/mocks/{{mockId}}/scenarios/reset",
            (string mockId, BowireMockHostManager manager) =>
        {
            return manager.ResetScenarios(mockId)
                ? Results.Json(new { mockId, scenarios = manager.GetScenarioStates(mockId) }, JsonOptions)
                : Results.NotFound(new { error = $"Mock {mockId} not running." });
        }).ExcludeFromDescription();

        // #170: per-method fault-injection rules of a RUNNING mock.
        // GET returns the live rule set in the exact mock-faults.json
        // shape (kebab-case enums); PUT replaces it — an empty rules
        // array clears injection. The body is validated by
        // FaultRuleSet.LoadJson, so a bad rule comes back as a 400 with
        // the offending rule named, never as a half-applied set.
        endpoints.MapGet($"{basePath}/api/mocks/{{mockId}}/faults",
            (string mockId, BowireMockHostManager manager) =>
        {
            var faults = manager.GetFaults(mockId);
            return faults is null
                ? Results.NotFound(new { error = $"Mock {mockId} not running." })
                : Results.Text(faults.ToJson(), "application/json");
        }).ExcludeFromDescription();

        endpoints.MapPut($"{basePath}/api/mocks/{{mockId}}/faults",
            async (string mockId, HttpRequest request, BowireMockHostManager manager) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted).ConfigureAwait(false);
            Chaos.FaultRuleSet faults;
            try
            {
                faults = Chaos.FaultRuleSet.LoadJson(body, allowEmpty: true);
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            return manager.TrySetFaults(mockId, faults)
                ? Results.Text(faults.ToJson(), "application/json")
                : Results.NotFound(new { error = $"Mock {mockId} not running." });
        }).ExcludeFromDescription();

        return endpoints;
    }

    private static async Task<Mocking.BowireRecordingStep?> ReadStubAsync(HttpContext ctx)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<Mocking.BowireRecordingStep>(
                ctx.Request.Body, JsonOptions, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ScenarioStateRequest(string? State);

    private sealed record StartMockRequest(
        string? Recording,
        string? Name,
        int? Port,
        string? RecordingId,
        string? Label);

    /// <summary>
    /// JSON-friendly projection of a <see cref="MockHostHandle"/>. Same
    /// shape the workbench rail consumes from <c>GET /api/mocks</c>.
    /// </summary>
    internal sealed record MockSummary(
        string MockId,
        string RecordingId,
        string RecordingName,
        string Label,
        int Port,
        string Url,
        DateTime StartedAt)
    {
        public static MockSummary From(MockHostHandle handle) => new(
            handle.MockId,
            handle.RecordingId,
            handle.Label,
            handle.Label,
            handle.Port,
            handle.Url,
            handle.StartedAtUtc);
    }
}
