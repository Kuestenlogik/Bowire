// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Flows;

/// <summary>
/// Row expansion for a data-driven Flow step (#174), so the in-browser
/// runner gets the same rows <c>bowire test</c> would.
/// </summary>
/// <remarks>
/// <para>
/// The step's <c>data</c> block is authored in the workbench and was
/// consumed only by the CLI: the in-browser runner ignored it, so a
/// parameterised step ran once with its placeholders unresolved and
/// looked like it had passed. Expanding here rather than in JavaScript
/// keeps one implementation of what a row is — the generator's arithmetic,
/// the CSV dialect, the label column, the 100k ceiling — instead of a
/// second one that drifts.
/// </para>
/// <para>
/// A CSV source resolves relative to the flow file, and a workbench flow
/// has none: it lives inside the workspace's <c>flows.json</c>. Rather
/// than invent a base directory, a CSV row source is refused here with
/// that reason; inline rows and generators, which are self-contained, work
/// everywhere. Resolving CSV needs the file-per-flow layout that #97 is
/// about, and #365 is waiting on the same thing.
/// </para>
/// </remarks>
public sealed class BowireFlowDataEndpoints : IBowireEndpointContribution
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost($"{basePath}/api/flows/data/expand", async (HttpContext ctx) =>
        {
            FlowDataSource? data;
            try
            {
                data = await JsonSerializer.DeserializeAsync<FlowDataSource>(
                    ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(
                    new { error = "Invalid JSON: " + ex.Message }, JsonOptions, statusCode: 400);
            }

            if (data is null)
                return Results.Json(new { error = "A data source is required." }, JsonOptions, statusCode: 400);

            if (!string.IsNullOrEmpty(data.Csv))
            {
                return Results.Json(
                    new
                    {
                        error = "A CSV row source resolves relative to the flow file, which a "
                            + "workbench flow does not have. Run this flow with `bowire test "
                            + "<flow.json>`, or use inline rows / a generator here.",
                    },
                    JsonOptions,
                    statusCode: 400);
            }

            try
            {
                var rows = FlowDataSourceExpander.Expand(data, baseDirectory: ".");
                return Results.Json(
                    new
                    {
                        rows = rows.Select(r => new { label = r.Label, values = r.Values }).ToList(),
                    },
                    JsonOptions);
            }
            catch (InvalidDataException ex)
            {
                // The expander's own vocabulary for a misconfigured source
                // (none set, several set, inverted range, zero rows). It
                // reads the same in the workbench as it does in CI.
                return Results.Json(new { error = ex.Message }, JsonOptions, statusCode: 400);
            }
        }).ExcludeFromDescription();
    }
}
