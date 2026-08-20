// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Reporting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// The rollup's HTTP surface (#587) — a thin adapter over
/// <see cref="BowireReportReader"/>, exactly as the lint endpoint is over the
/// linter.
/// </summary>
public static class BowireReportEndpoints
{
    /// <summary>Where the rollup looks when the caller names no paths.</summary>
    private const string DefaultRoot = ".bowire";

    /// <summary>
    /// Map <c>POST {basePath}/api/report/rollup</c>. The body carries the
    /// paths to read; an empty body rolls up this workspace's own
    /// <c>.bowire/</c> folder, which is the useful default when the workbench
    /// is open on a single service.
    /// <para>
    /// POST rather than GET because the paths are input, and a rollup over an
    /// arbitrary directory tree is not something to encode in a URL that
    /// could end up in a log or a browser history.
    /// </para>
    /// </summary>
    public static IEndpointRouteBuilder MapBowireReportEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost($"{basePath}/api/report/rollup",
            async (BowireRollupRequest? body, CancellationToken ct) =>
            {
                var roots = body?.From is { Count: > 0 } from ? from : [DefaultRoot];
                var rollup = await BowireReportReader.ReadAsync(roots, body?.Service, ct).ConfigureAwait(false);
                return Results.Ok(BowireRollupPayload.ToWirePayload(rollup));
            })
            .ExcludeFromDescription();

        return endpoints;
    }
}

/// <summary>Request body for the rollup endpoint (#587).</summary>
/// <param name="From">Files or directories to read; empty means this workspace's <c>.bowire/</c>.</param>
/// <param name="Service">Attribute every report to this service instead of inferring it.</param>
// IReadOnlyList rather than an array: CA1819 forbids array-returning
// properties on a public type, and System.Text.Json binds a JSON array to it
// just the same.
public sealed record BowireRollupRequest(IReadOnlyList<string>? From, string? Service);
