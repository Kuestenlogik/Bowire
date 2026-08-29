// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Starting and ending a support session on somebody else's behalf (#98,
/// #28 Phase F).
/// </summary>
/// <remarks>
/// Every one of these re-derives the real caller rather than trusting the
/// scope in force. While an administrator is impersonating, the tenancy scope
/// names the <em>target</em> — so a check written against it would decide that
/// an administrator acting as an ordinary user is an ordinary user, and they
/// could not even end their own session.
/// </remarks>
internal static class BowireImpersonationEndpoints
{
    public static IEndpointRouteBuilder MapBowireImpersonationEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/users", (HttpContext http) =>
        {
            var refused = RequireAdmin(http, out var directory, out _);
            if (refused is not null) return refused;

            var term = http.Request.Query["q"].ToString();
            var limit = int.TryParse(http.Request.Query["limit"].ToString(), out var parsed)
                ? Math.Clamp(parsed, 1, 100)
                : 20;

            return Results.Ok(directory!.Search(term, limit).Select(p => new
            {
                subject = p.Subject,
                displayName = p.DisplayName,
                email = p.Email,
                isAdmin = p.IsAdmin,
            }));
        });

        endpoints.MapPost($"{basePath}{BowireImpersonation.EndpointPath}", async (HttpContext http) =>
        {
            var refused = RequireAdmin(http, out var directory, out var actor);
            if (refused is not null) return refused;

            string? target;
            try
            {
                var request = await http.Request.ReadFromJsonAsync<ImpersonationRequest>(
                    http.RequestAborted).ConfigureAwait(false);
                target = request?.Subject?.Trim();
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                // A body that is not JSON is the caller's mistake, and saying
                // so is more useful than a 500 that reads as a server fault.
                _ = ex;
                return Problem(StatusCodes.Status400BadRequest, "urn:bowire:impersonation:no-subject",
                    "Send a JSON body naming the identity to act as.");
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                return Problem(StatusCodes.Status400BadRequest, "urn:bowire:impersonation:no-subject",
                    "Name the identity to act as.");
            }

            if (string.Equals(target, actor, StringComparison.Ordinal))
            {
                // Not an error, and not a session either — they are already
                // themselves. Clearing is what they meant.
                Clear(http);
                return Results.NoContent();
            }

            // Only somebody the directory actually knows. Without this an
            // administrator could open a slot for a subject nobody has, which
            // would look like an empty workbench rather than like a typo.
            if (!directory!.Search(target, 100).Any(p =>
                    string.Equals(p.Subject, target, StringComparison.Ordinal)))
            {
                return Problem(StatusCodes.Status404NotFound, "urn:bowire:impersonation:unknown-subject",
                    $"'{target}' is not an identity this instance knows about.");
            }

            http.Response.Cookies.Append(BowireImpersonation.CookieName, target, new CookieOptions
            {
                HttpOnly = true,
                // Not readable by script, and not sent from another site: the
                // cookie cannot start a session on its own, but there is no
                // reason to let a cross-site request carry one either.
                SameSite = SameSiteMode.Strict,
                Secure = http.Request.IsHttps,
                Path = "/",
            });

            http.RequestServices.GetService<BowireAuditLog>()?
                .Record("begin", actor!, target);

            return Results.NoContent();
        });

        endpoints.MapDelete($"{basePath}{BowireImpersonation.EndpointPath}", (HttpContext http) =>
        {
            // Deliberately not admin-gated. Ending a session is always safe,
            // and an administrator who lost the role mid-session must still be
            // able to get back to their own workbench.
            var scope = BowireImpersonation.Current;
            Clear(http);

            if (scope is not null)
            {
                http.RequestServices.GetService<BowireAuditLog>()?
                    .Record("end", scope.Actor, scope.ActingAs);
            }

            return Results.NoContent();
        });

        return endpoints;
    }

    /// <summary>
    /// <c>null</c> when the caller may act on somebody else's behalf, a
    /// refusal otherwise.
    /// </summary>
    private static IResult? RequireAdmin(
        HttpContext http, out IBowireUserDirectory? directory, out string? actor)
    {
        directory = null;
        actor = null;

        var options = http.RequestServices.GetService<BowireTenancyOptions>();
        if (options is null || !options.Enabled)
        {
            return Problem(StatusCodes.Status404NotFound, "urn:bowire:impersonation:single-user",
                "This instance serves one person, so there is nobody to act as.");
        }

        // The real caller. While impersonating, the tenancy scope names the
        // target — reading it here would let an administrator acting as an
        // ordinary user lose the ability to do anything, including stop.
        actor = BowireImpersonation.Current?.Actor ?? BowireTenancy.CurrentSubject;
        if (actor is null)
        {
            return Problem(StatusCodes.Status401Unauthorized, "urn:bowire:impersonation:unauthenticated",
                "There is no authenticated caller to act on behalf of anyone.");
        }

        directory = http.RequestServices.GetService<IBowireUserDirectory>() ?? new ClaimsUserDirectory();
        if (!directory.Describe(http.User, actor).IsAdmin)
        {
            return Problem(StatusCodes.Status403Forbidden, "urn:bowire:impersonation:not-admin",
                "Only an administrator can act on somebody else's behalf.");
        }

        return null;
    }

    private static void Clear(HttpContext http)
        => http.Response.Cookies.Delete(BowireImpersonation.CookieName, new CookieOptions
        {
            // The delete has to match the attributes the cookie was written
            // with, or the browser keeps it and the session never ends.
            Path = "/",
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
        });

    private static IResult Problem(int status, string type, string detail)
        => Results.Problem(detail: detail, statusCode: status, type: type,
            title: "Impersonation");

    private sealed class ImpersonationRequest
    {
        public string? Subject { get; set; }
    }
}
