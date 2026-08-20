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
                var requested = body?.From is { Count: > 0 } from ? from : [DefaultRoot];
                if (!TryConfine(requested, out var roots, out var rejected))
                {
                    return Results.Json(
                        new { error = $"Path is outside the workspace: {rejected}" },
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var rollup = await BowireReportReader.ReadAsync(roots, body?.Service, ct).ConfigureAwait(false);
                return Results.Ok(BowireRollupPayload.ToWirePayload(rollup));
            })
            .ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// Anchor every requested path under the workspace, or reject the request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The paths arrive in a POST body, so they are attacker-controlled
    /// wherever the workbench is reachable by more than its own operator —
    /// which is precisely the case <c>MapBowire()</c> exists for. Unconfined,
    /// <c>{"from":["/"]}</c> walks the host, reads every <c>.json</c>,
    /// <c>.sarif</c> and <c>.xml</c> it can open, and hands back both the paths
    /// and what could be parsed out of them.
    /// </para>
    /// <para>
    /// The CLI is deliberately not constrained this way: <c>bowire report
    /// rollup --from /var/ci</c> is an operator naming a directory with their
    /// own rights, and confining it would break the CI case the rollup was
    /// built for. The difference is the trust boundary, not the operation.
    /// </para>
    /// </remarks>
    private static bool TryConfine(
        IReadOnlyList<string> requested,
        out IReadOnlyList<string> confined,
        out string? rejected)
    {
        var root = Directory.GetCurrentDirectory();
        var result = new List<string>(requested.Count);

        foreach (var candidate in requested)
        {
            string safe;
            try
            {
                safe = SafePath.Combine(root, candidate);
            }
            catch (ArgumentException)
            {
                // Absolute paths and `../` escapes both land here. Naming the
                // offending entry beats a bare 400: the caller is usually the
                // workbench's own path field, and the operator needs to know
                // which one it objected to.
                confined = [];
                rejected = candidate;
                return false;
            }

            result.Add(safe);
        }

        confined = result;
        rejected = null;
        return true;
    }
}

/// <summary>Request body for the rollup endpoint (#587).</summary>
/// <param name="From">Files or directories to read; empty means this workspace's <c>.bowire/</c>.</param>
/// <param name="Service">Attribute every report to this service instead of inferring it.</param>
// IReadOnlyList rather than an array: CA1819 forbids array-returning
// properties on a public type, and System.Text.Json binds a JSON array to it
// just the same.
public sealed record BowireRollupRequest(IReadOnlyList<string>? From, string? Service);
