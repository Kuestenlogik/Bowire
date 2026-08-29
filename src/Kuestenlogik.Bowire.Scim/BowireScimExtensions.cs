// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Scim;

/// <summary>Wires SCIM provisioning into a host (#96).</summary>
public static class BowireScimExtensions
{
    /// <summary>
    /// Register the provisioning services. Reads <c>Bowire:Scim</c>;
    /// <paramref name="configure"/> wins over the configuration file.
    /// </summary>
    public static IServiceCollection AddBowireScim(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<BowireScimOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new BowireScimOptions();
        configuration.GetSection("Bowire:Scim").Bind(options);
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(sp =>
        {
            var paths = sp.GetService<IBowirePathResolver>() ?? BowirePaths.Current;
            return new BowireScimStore(paths.Root(BowireStorageScope.Data));
        });

        if (options.Enabled && options.PurgeAfter > TimeSpan.Zero)
        {
            services.AddHostedService<BowireScimPurgeService>();
        }

        return services;
    }

    /// <summary>
    /// Mount the SCIM endpoints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Outside the workbench's route group deliberately. Those routes are
    /// gated by whatever auth provider the operator configured, and a
    /// provisioning connector holds a shared secret rather than a user
    /// session — it could never pass that gate. SCIM authenticates itself,
    /// with its own token, on its own path.
    /// </para>
    /// <para>
    /// No-op unless <see cref="BowireScimOptions.Enabled"/> is set.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Provisioning is enabled with no token. Serving it open would let anyone
    /// who can reach the host create identities.
    /// </exception>
    public static IEndpointRouteBuilder MapBowireScim(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var options = services.GetService<BowireScimOptions>();
        if (options is null || !options.Enabled) return endpoints;

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new InvalidOperationException(
                "Bowire:Scim:Enabled is set but Bowire:Scim:Token is empty. A provisioning "
                + "endpoint without a token lets anyone who can reach this host create and "
                + "remove identities, so it is refused at startup rather than served open.");
        }

        var store = services.GetRequiredService<BowireScimStore>();
        return BowireScimEndpoints.Map(endpoints, options, store);
    }

    /// <summary>
    /// Refuse a request from an identity the directory has deprovisioned, and
    /// remember which token subject each provisioned identity turns out to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what separates provisioning from bookkeeping. An install where
    /// deactivating in Okta sets a flag that nothing reads has not
    /// deprovisioned anybody — the person keeps working until someone notices.
    /// </para>
    /// <para>
    /// It also does the binding no other code can. Provisioning knows a
    /// <c>userName</c> and an <c>externalId</c>; a token carries a subject.
    /// The first request from a provisioned identity is the only moment both
    /// are in the same place, so that is where the two get tied together —
    /// and after that, deprovisioning knows exactly whose slot to archive.
    /// </para>
    /// </remarks>
    public static IApplicationBuilder UseBowireScim(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ApplicationServices.GetService<BowireScimOptions>();
        if (options is null || !options.Enabled) return app;

        var store = app.ApplicationServices.GetRequiredService<BowireScimStore>();
        var tenancy = app.ApplicationServices.GetService<BowireTenancyOptions>();

        return app.Use(async (context, next) =>
        {
            var subject = tenancy?.SubjectOf(context.User);
            if (subject is null)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var record = store.FindBySubject(subject);

            if (record is null)
            {
                // Unknown to the directory. Allowed by default: an IdP that has
                // not finished its first sync would otherwise lock out the
                // operator who just turned provisioning on. Installs that want
                // SCIM to be the allow-list set RequireProvisioned.
                if (options.RequireProvisioned && options.EnforceActive)
                {
                    await Refuse(context,
                        "This identity has not been provisioned for this Bowire instance.")
                        .ConfigureAwait(false);
                    return;
                }

                await next(context).ConfigureAwait(false);
                return;
            }

            if (options.EnforceActive && !record.Resource.Active)
            {
                await Refuse(context,
                    "This identity has been deactivated in the directory that provisions this Bowire instance.")
                    .ConfigureAwait(false);
                return;
            }

            store.BindSubject(record.Resource.Id, subject);
            await next(context).ConfigureAwait(false);
        });
    }

    private static async Task Refuse(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = "urn:bowire:scim:deprovisioned",
            title = "Not provisioned",
            status = StatusCodes.Status403Forbidden,
            detail,
        }, context.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>
/// Deletes deprovisioned identities once their purge window has closed (#96).
/// </summary>
/// <remarks>
/// A daily sweep rather than a check on each request: the window is measured
/// in days, and doing this on the request path would make one unlucky caller
/// pay for a directory-wide delete.
/// </remarks>
internal sealed class BowireScimPurgeService(
    BowireScimStore store,
    BowireScimOptions options,
    ILogger<BowireScimPurgeService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        // Once at start-up as well: an install that is restarted daily would
        // otherwise never reach the first tick.
        do
        {
            Sweep();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private void Sweep()
    {
        try
        {
            var purged = store.Purge(options.PurgeAfter);
            if (purged > 0 && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Purged {Count} deprovisioned identity/identities past the {Window} window.",
                    purged, options.PurgeAfter);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not purge deprovisioned identities; will retry tomorrow.");
        }
    }
}
