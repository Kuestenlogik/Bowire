// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Contracts;

/// <summary>
/// Discoverable endpoint-mount entry point for the Contracts rail (#364).
/// Picked up by Core's <c>BowireApiEndpoints</c> scan via the
/// <see cref="IBowireEndpointContribution"/> seam, so the matrix endpoint
/// inherits the auth-gated route group and the host's base path without
/// core knowing this package exists.
/// </summary>
public sealed class BowireContractsEndpointContribution : IBowireEndpointContribution
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapBowireContractMatrixEndpoints(basePath);
    }
}

/// <summary>
/// The Contracts rail's HTTP surface: the consumer × provider matrix
/// assembled from stored verification results.
/// </summary>
public static class BowireContractsEndpoints
{
    /// <summary>
    /// Map <c>GET {basePath}/api/contracts/matrix</c>. Reads the results
    /// <c>bowire contract verify</c> stored (see
    /// <see cref="ContractResultStore"/>) and projects them through
    /// <see cref="BowireContractMatrix"/> — a read-only surface that never
    /// reaches out to a provider itself, keeping outbound calls opt-in.
    /// </summary>
    public static IEndpointRouteBuilder MapBowireContractMatrixEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet($"{basePath}/api/contracts/matrix", async (HttpContext ctx, CancellationToken ct) =>
        {
            var reports = await ContractResultStore.LoadAllAsync(rootPath: null, ct).ConfigureAwait(false);
            var matrix = BowireContractMatrix.Build(reports);
            return Results.Ok(ToPayload(matrix));
        })
        .ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// Wire shape for the rail JS. Enum values are lower-cased ("pass" /
    /// "fail" / "notRun") to match the strings <c>contract-matrix.js</c>
    /// switches on, and the drill-in report is carried inline so opening a
    /// cell needs no second round-trip.
    /// </summary>
    internal static object ToPayload(ContractMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        return new
        {
            consumers = matrix.Consumers,
            providers = matrix.Providers,
            passedCells = matrix.PassedCells,
            failedCells = matrix.FailedCells,
            cells = matrix.Cells.Select(c => new
            {
                consumer = c.Consumer,
                provider = c.Provider,
                status = StatusText(c.Status),
                lastRun = c.LastRun,
                passedInteractions = c.PassedInteractions,
                totalInteractions = c.TotalInteractions,
                report = c.Report is null ? null : new
                {
                    consumer = c.Report.Consumer,
                    provider = c.Report.Provider,
                    startedAt = c.Report.StartedAt,
                    durationMs = c.Report.DurationMs,
                    passed = c.Report.Passed,
                    interactions = c.Report.Interactions.Select(i => new
                    {
                        description = i.Description,
                        method = i.Method,
                        status = i.Status,
                        error = i.Error,
                        durationMs = i.DurationMs,
                        passed = i.Passed,
                        assertions = i.Assertions.Select(a => new
                        {
                            path = a.Path,
                            op = a.Op,
                            expected = a.Expected,
                            actualText = a.ActualText,
                            passed = a.Passed,
                            error = a.Error,
                        }),
                    }),
                },
            }),
        };
    }

    private static string StatusText(ContractCellStatus status) => status switch
    {
        ContractCellStatus.Pass => "pass",
        ContractCellStatus.Fail => "fail",
        _ => "notRun",
    };
}
