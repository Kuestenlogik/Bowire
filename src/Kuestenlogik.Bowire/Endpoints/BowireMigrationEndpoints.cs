// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// The one-time offer to bring a single-user install's state into the calling
/// identity's slot (#97, #28 Phase E).
/// </summary>
/// <remarks>
/// Mapped whether or not the install is multi-tenant. A workbench that has to
/// know the deployment shape before it can ask a question is a workbench that
/// asks the question wrong; here it always asks, and an install that is not
/// multi-tenant answers <c>off</c>.
/// </remarks>
internal static class BowireMigrationEndpoints
{
    public static IEndpointRouteBuilder MapBowireMigrationEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/migration", (HttpContext http) =>
        {
            var plan = Plan(http);
            return plan is null ? Results.Ok(Off()) : Results.Ok(Describe(plan));
        });

        endpoints.MapPost($"{basePath}/api/migration/accept", (HttpContext http) =>
        {
            var plan = Plan(http);
            if (plan is null) return Results.Ok(Off());
            if (plan.State != BowireUserMigrationState.Available) return Stale(plan);

            var receipt = BowireUserMigrator.Apply(plan);
            return Results.Ok(new
            {
                state = nameof(BowireUserMigrationState.AlreadyDecided),
                outcome = receipt.Outcome.ToString(),
                files = receipt.Files,
                bytes = receipt.Bytes,
            });
        });

        endpoints.MapPost($"{basePath}/api/migration/decline", (HttpContext http) =>
        {
            var plan = Plan(http);
            if (plan is null) return Results.Ok(Off());
            if (plan.State != BowireUserMigrationState.Available) return Stale(plan);

            var receipt = BowireUserMigrator.Decline(plan);
            return Results.Ok(new
            {
                state = nameof(BowireUserMigrationState.AlreadyDecided),
                outcome = receipt.Outcome.ToString(),
            });
        });

        return endpoints;
    }

    /// <summary>
    /// The plan for whoever is being served, or <c>null</c> when there is no
    /// tenancy or no identity to plan for.
    /// </summary>
    private static BowireUserMigrationPlan? Plan(HttpContext http)
    {
        var options = http.RequestServices.GetService<BowireTenancyOptions>();
        if (options is null || !options.Enabled) return null;

        var subject = BowireTenancy.CurrentSubject;
        if (subject is null) return null;

        var tenancy = http.RequestServices.GetRequiredService<BowireTenancy>();
        return BowireUserMigrator.Plan(tenancy.StorageRoot, subject, options.Migration);
    }

    private static object Off() => new { state = "Off" };

    private static object Describe(BowireUserMigrationPlan plan) => new
    {
        state = plan.State.ToString(),
        // What the person is deciding about. A count and a size are the two
        // things that make "is this my data?" answerable without a file
        // browser — a migration of three files is somebody else's account,
        // and one of four hundred is a year of work.
        files = plan.Entries.Count,
        bytes = plan.Bytes,
        source = plan.StorageRoot,
        slot = plan.Slot,
        outcome = plan.Receipt?.Outcome.ToString(),
        decidedUtc = plan.Receipt?.DecidedUtc,
    };

    /// <summary>
    /// The client acted on a plan that no longer holds — another window
    /// decided first, or the state changed under it.
    /// </summary>
    private static IResult Stale(BowireUserMigrationPlan plan) => Results.Problem(
        title: "The migration is no longer on offer",
        detail: $"Nothing to decide: this identity's migration is {plan.State}.",
        statusCode: StatusCodes.Status409Conflict,
        type: "urn:bowire:migration:not-available");
}
