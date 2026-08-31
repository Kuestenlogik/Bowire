// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// The one answer to "may this caller administer the install" (#636).
/// </summary>
/// <remarks>
/// <para>
/// Some of what the workbench can do is not a preference but a change to the
/// install itself — installing a plugin, above all, which downloads a package
/// and puts assemblies where the next start loads them into the server
/// process. On a laptop the person at the keyboard owns the machine and there
/// is nothing to gate. On a shared install the least-privileged identity must
/// not be able to reach code execution as the server.
/// </para>
/// <para>
/// The authority is <see cref="IBowireUserDirectory"/>'s, the same source
/// impersonation reads, so "administrator" has one definition rather than one
/// per endpoint.
/// </para>
/// </remarks>
public static class BowireAdminGate
{
    /// <summary>
    /// <c>null</c> when the caller may administer the install, a refusal
    /// otherwise.
    /// </summary>
    /// <param name="http">The request being served.</param>
    /// <param name="action">
    /// What was being attempted, in the sentence "Only an administrator can
    /// …". Reaches the operator, so write it as prose.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Ungated without an auth provider.</b> That is the laptop and the
    /// embedded host that never configured identities: there is one person,
    /// gating them protects nobody, and it would break the ordinary case to
    /// answer a threat model that does not exist there. The condition is the
    /// same one <c>RequireBowireAuth</c> uses, so "this install has
    /// identities" is decided in one way.
    /// </para>
    /// <para>
    /// <b>The real caller decides, not the impersonated one.</b> While an
    /// administrator is looking at somebody else's workbench, the tenancy
    /// scope names the target; reading it here would strip an administrator
    /// of the authority they are exercising — including the authority to stop.
    /// A non-administrator cannot impersonate at all, so reading the actor
    /// never grants anything.
    /// </para>
    /// </remarks>
    public static IResult? RequireAdministrator(HttpContext http, string action)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (http.RequestServices.GetService<IBowireAuthProvider>() is null)
        {
            return null;
        }

        var actor = BowireImpersonation.Current?.Actor ?? BowireTenancy.CurrentSubject;
        if (actor is null)
        {
            return BowireEndpointHelpers.Problem(
                type: "urn:bowire:admin:unauthenticated",
                title: "There is no authenticated caller.",
                status: StatusCodes.Status401Unauthorized,
                instance: http.Request.Path);
        }

        var directory = http.RequestServices.GetService<IBowireUserDirectory>() ?? new ClaimsUserDirectory();
        if (!directory.Describe(http.User, actor).IsAdmin)
        {
            return BowireEndpointHelpers.Problem(
                type: "urn:bowire:admin:not-admin",
                title: $"Only an administrator can {action}.",
                status: StatusCodes.Status403Forbidden,
                instance: http.Request.Path);
        }

        return null;
    }

    /// <summary>
    /// Whether the caller may administer the install, for a reader that wants
    /// the fact rather than a refusal — the identity endpoint, so the
    /// workbench can leave out a control instead of offering one that will be
    /// refused.
    /// </summary>
    public static bool IsAdministrator(HttpContext http)
        => RequireAdministrator(http, "administer this install") is null;
}
