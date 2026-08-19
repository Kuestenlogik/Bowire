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
            // Shared wire shape — same JSON the CLI's --json and the MCP
            // tool emit, so a script can treat every surface alike.
            return Results.Ok(BowireContractMatrix.ToWirePayload(matrix));
        })
        .ExcludeFromDescription();

        return endpoints;
    }
}
