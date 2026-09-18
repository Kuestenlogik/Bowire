// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Flows.Expectations;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Flows;

/// <summary>
/// Snapshot baselines for the in-browser runner (#171): what drifted, and
/// making the new answer the baseline.
/// </summary>
/// <remarks>
/// <para>
/// Re-baselining was a CLI flag — <c>bowire test --update-snapshots</c> —
/// which meant the person who could see the drift on screen was not the one
/// who could act on it. They had to leave the workbench, find the flow file,
/// and re-run everything to accept one line.
/// </para>
/// <para>
/// The work happens here rather than in JavaScript because the browser can
/// neither read nor write <c>__snapshots__/</c>, and because the comparison
/// has to be the one the CLI makes. A second diff in JavaScript would drift
/// from it, and the drift would show up as a workbench that says a snapshot
/// holds while CI says it does not.
/// </para>
/// </remarks>
public sealed class BowireFlowSnapshotEndpoints : IBowireEndpointContribution
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost($"{basePath}/api/flows/snapshot/compare", async (HttpContext ctx) =>
        {
            var (request, failure) = await ReadAsync(ctx);
            if (failure is not null) return failure;

            var dir = FlowSnapshotStore.DirectoryForWorkspace(
                request!.WorkspaceId!, request.StorageRoot, request.FlowId!);
            var baseline = await FlowSnapshotStore
                .ReadAsync(dir, request.StepId!, ctx.RequestAborted).ConfigureAwait(false);

            if (baseline is null)
            {
                // Nothing captured yet. Said plainly rather than as "no
                // differences": the two look the same on screen and mean
                // opposite things — one is a snapshot that holds, the other
                // is a snapshot that has never guarded anything.
                return Results.Json(
                    new { captured = false, diffs = Array.Empty<string>(), file = FlowSnapshotStore.FileFor(dir, request.StepId!) },
                    JsonOptions);
            }

            var diffs = FlowSnapshotComparer.Compare(
                baseline, request.Actual ?? string.Empty, request.Mode, request.Ignore);

            return Results.Json(
                new
                {
                    captured = true,
                    diffs,
                    baseline,
                    file = FlowSnapshotStore.FileFor(dir, request.StepId!),
                },
                JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/flows/snapshot/approve", async (HttpContext ctx) =>
        {
            var (request, failure) = await ReadAsync(ctx);
            if (failure is not null) return failure;

            var dir = FlowSnapshotStore.DirectoryForWorkspace(
                request!.WorkspaceId!, request.StorageRoot, request.FlowId!);

            try
            {
                var file = await FlowSnapshotStore
                    .WriteAsync(dir, request.StepId!, request.Actual ?? string.Empty, ctx.RequestAborted)
                    .ConfigureAwait(false);

                // The path travels back so the workbench can say where the
                // file went. "Approved" without a location is a claim the
                // person cannot check, and this one ends up in their commit.
                return Results.Json(new { approved = true, file }, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.Json(
                    new { error = "Could not write the baseline: " + ex.Message },
                    JsonOptions,
                    statusCode: 500);
            }
        }).ExcludeFromDescription();
    }

    /// <summary>
    /// The request body, or the refusal to send back instead.
    /// </summary>
    /// <remarks>
    /// The workspace pair is checked by <see cref="WorkspaceScopeQuery"/>,
    /// the same rule the core endpoints apply: both values end up in a file
    /// path and both arrive from a browser, and this route writes.
    /// </remarks>
    private static async Task<(SnapshotRequest? Request, IResult? Failure)> ReadAsync(HttpContext ctx)
    {
        SnapshotRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<SnapshotRequest>(
                ctx.Request.Body, JsonOptions, ctx.RequestAborted);
        }
        catch (JsonException ex)
        {
            return (null, Results.Json(
                new { error = "Invalid JSON: " + ex.Message }, JsonOptions, statusCode: 400));
        }

        if (request is null)
            return (null, Bad("A request body is required."));

        if (string.IsNullOrWhiteSpace(request.FlowId))
            return (null, Bad("flowId is required: a baseline belongs to one flow."));
        if (string.IsNullOrWhiteSpace(request.StepId))
            return (null, Bad("stepId is required: a baseline belongs to one step."));
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            return (null, Bad("workspaceId is required: baselines live beside the workspace's flows."));

        var scope = WorkspaceScopeQuery.Validate(request.WorkspaceId, request.StorageRoot);
        if (scope.IsInvalid) return (null, Bad(scope.Error!));

        return (request with { WorkspaceId = scope.WorkspaceId, StorageRoot = scope.StorageRoot }, null);
    }

    private static IResult Bad(string error)
        => Results.Json(new { error }, JsonOptions, statusCode: 400);

    /// <summary>What the workbench sends about one step's snapshot.</summary>
    private sealed record SnapshotRequest
    {
        /// <summary>The workspace the flow was saved in.</summary>
        [JsonPropertyName("workspaceId")]
        public string? WorkspaceId { get; init; }

        /// <summary>The checkout, for a git-native workspace.</summary>
        [JsonPropertyName("storageRoot")]
        public string? StorageRoot { get; init; }

        /// <summary>The flow, which is what the baseline directory is keyed by.</summary>
        [JsonPropertyName("flowId")]
        public string? FlowId { get; init; }

        /// <summary>The step, which is what the baseline file is named after.</summary>
        [JsonPropertyName("stepId")]
        public string? StepId { get; init; }

        /// <summary>The response body just received.</summary>
        [JsonPropertyName("actual")]
        public string? Actual { get; init; }

        /// <summary>How strictly to compare. Ignored by approve.</summary>
        [JsonPropertyName("mode")]
        public FlowSnapshotMode Mode { get; init; } = FlowSnapshotMode.Exact;

        /// <summary>Paths whose values are exempt. Ignored by approve.</summary>
        [JsonPropertyName("ignore")]
        public IReadOnlyList<string>? Ignore { get; init; }
    }
}
