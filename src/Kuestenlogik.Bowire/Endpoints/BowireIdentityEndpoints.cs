// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Who the workbench is serving (#98, #28 Phase F).
/// </summary>
/// <remarks>
/// <para>
/// Everything below the surface already knows: the tenancy scope, the storage
/// slot, the SCIM record. The workbench does not, and until it does, signing
/// in changes nothing anybody can see — which is exactly the complaint an
/// operator has when they cannot point at the screen and say "that is me, and
/// these recordings are mine".
/// </para>
/// <para>
/// Mapped unconditionally. A workbench that has to know the deployment shape
/// before it can ask who it is serving asks the question wrong; a single-user
/// install answers <c>multiTenant: false</c> and the chip stays away.
/// </para>
/// </remarks>
internal static class BowireIdentityEndpoints
{
    public static IEndpointRouteBuilder MapBowireIdentityEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/me", (HttpContext http) =>
        {
            var options = http.RequestServices.GetService<BowireTenancyOptions>();
            var subject = BowireTenancy.CurrentSubject;

            // Single-user, or nobody signed in yet. Not an error and not an
            // empty profile: there is genuinely nobody to identify, and the
            // difference matters to the caller.
            if (options is null || !options.Enabled || subject is null)
            {
                return Results.Ok(new { multiTenant = false });
            }

            var directory = http.RequestServices.GetService<IBowireUserDirectory>()
                ?? new ClaimsUserDirectory();

            // While impersonating, the tenancy scope names the target — which
            // is right for storage and wrong for "who am I". The chip has to
            // show the administrator, and the banner has to name whose
            // workbench they are looking at.
            var acting = BowireImpersonation.Current;
            var profile = directory.Describe(http.User, acting?.Actor ?? subject);

            return Results.Ok(new
            {
                multiTenant = true,
                subject = profile.Subject,
                displayName = profile.DisplayName,
                email = profile.Email,
                picture = profile.Picture,
                isAdmin = profile.IsAdmin,
                // Only when the configured provider actually has somewhere to
                // send them; a sign-out link that clears nothing is worse than
                // none, because people believe it.
                signOutUrl = http.RequestServices.GetService<IBowireAuthProvider>()?.SignOutUrl,
                // What the chip renders when there is no name and no picture.
                // Computed here rather than in the browser so every surface
                // that wants them agrees on what they are.
                initials = Initials(profile),
                actingAs = acting is null ? null : Describe(directory, http, acting.ActingAs),
            });
        });

        return endpoints;
    }

    /// <summary>
    /// The identity an administrator is currently looking at, for the banner.
    /// </summary>
    private static object Describe(
        IBowireUserDirectory directory, HttpContext http, string subject)
    {
        // Not http.User: those are the administrator's claims, and using them
        // would label the banner with the administrator's own name.
        var profile = directory.Describe(null, subject);
        return new
        {
            subject = profile.Subject,
            displayName = profile.DisplayName,
            email = profile.Email,
            initials = Initials(profile),
        };
    }

    /// <summary>
    /// One or two letters for the avatar fallback.
    /// </summary>
    /// <remarks>
    /// Taken from the display name when there is one, then the local part of
    /// the e-mail address, and only then the subject — which for most
    /// providers is a GUID, and two characters of a GUID identify nobody.
    /// That is still better than an empty circle.
    /// </remarks>
    internal static string Initials(BowireUserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var source = !string.IsNullOrWhiteSpace(profile.DisplayName)
            ? profile.DisplayName
            : !string.IsNullOrWhiteSpace(profile.Email)
                ? profile.Email.Split('@')[0]
                : profile.Subject;

        var words = source.Split([' ', '.', '_', '-', '+'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0]))
            .ToList();

        return words.Count switch
        {
            0 => "?",
            1 => Up(words[0], 2),
            _ => Up(words[0], 1) + Up(words[^1], 1),
        };
    }

    private static string Up(string word, int count)
        => word[..Math.Min(count, word.Length)].ToUpperInvariant();
}
