// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Kuestenlogik.Bowire.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// Wires per-identity storage into a host (#97).
/// </summary>
public static class BowireTenancyExtensions
{
    /// <summary>
    /// Register the tenancy seam. Reads <c>Bowire:MultiTenant</c>;
    /// <paramref name="configure"/> wins over the configuration file.
    /// </summary>
    /// <remarks>
    /// Registering is not enabling. The services land either way so that a
    /// migration endpoint can be mapped unconditionally, and
    /// <see cref="UseBowireTenancy"/> is where the store actually changes.
    /// </remarks>
    public static IServiceCollection AddBowireTenancy(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<BowireTenancyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new BowireTenancyOptions();
        configuration.GetSection("Bowire:MultiTenant").Bind(options);
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        // #98 — who the caller is, beyond the subject their storage is keyed
        // on. Claims only; a package that knows about roles replaces it.
        services.TryAddSingleton<IBowireUserDirectory, ClaimsUserDirectory>();
        // #98 — the record of who acted on whose behalf. Registered whether or
        // not impersonation is ever used: the endpoint that starts one has to
        // be able to write the first line.
        services.TryAddSingleton(sp =>
        {
            var paths = sp.GetService<IBowirePathResolver>() ?? BowirePaths.Current;
            return new BowireAuditLog(paths.Root(BowireStorageScope.Data));
        });
        services.TryAddSingleton(sp =>
        {
            var paths = sp.GetService<IBowirePathResolver>() ?? BowirePaths.Current;
            var root = paths.Root(BowireStorageScope.Data);

            // Wrapping a tenancy in a tenancy would make a call with no
            // subject recurse instead of reaching the shared store. This can
            // only happen if a host wires the seam twice, which is exactly the
            // sort of thing that happens when embedding.
            return BowireUserContext.Current is BowireTenancy already
                ? already
                : new BowireTenancy(root, BowireUserContext.Current);
        });

        return services;
    }

    /// <summary>
    /// Serve each authenticated identity out of its own slot, and put the
    /// scope in place for the duration of the request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No-op unless <see cref="BowireTenancyOptions.Enabled"/> is set, so
    /// calling it unconditionally is safe and is the intended pattern —
    /// alongside <c>UseBowireAuth</c>, after <c>UseAuthentication</c>, since
    /// there is no identity to scope by before that has run.
    /// </para>
    /// <para>
    /// Swapping <see cref="BowireUserContext.Current"/> here rather than in
    /// <see cref="AddBowireTenancy"/> is deliberate: the storage root can
    /// still move between the two — a project manifest opting into
    /// <c>.bowire/</c> is applied when the host is built — and a tenancy that
    /// captured the root before that would file everyone under the wrong tree.
    /// </para>
    /// </remarks>
    public static IApplicationBuilder UseBowireTenancy(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ApplicationServices.GetService<BowireTenancyOptions>();
        if (options is null || !options.Enabled) return app;

        var tenancy = app.ApplicationServices.GetRequiredService<BowireTenancy>();
        BowireUserContext.Current = tenancy;

        var logger = app.ApplicationServices
            .GetService<ILoggerFactory>()
            ?.CreateLogger("Kuestenlogik.Bowire.Auth.Tenancy");

        // #98 — resolved once: both are singletons, and the impersonation
        // check runs on every request.
        var directory = app.ApplicationServices.GetService<IBowireUserDirectory>()
            ?? new ClaimsUserDirectory();
        var audit = app.ApplicationServices.GetService<BowireAuditLog>();

        // One disk check per identity per process, not one per request: the
        // answer only changes when this middleware itself changes it.
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        return app.Use(async (context, next) =>
        {
            var subject = options.SubjectOf(context.User);
            if (subject is null)
            {
                // Not an error to handle here. In multi-tenant mode the
                // endpoints require an authenticated caller, so this request
                // is on its way to a 401 — and anything else reaching this
                // point (a health probe, a static file) has no business
                // writing to somebody's slot.
                await next(context).ConfigureAwait(false);
                return;
            }

            if (options.Migration == BowireUserMigrationMode.Auto && seen.TryAdd(subject, 0))
            {
                AutoMigrate(tenancy.StorageRoot, subject, logger);
            }

            // #98 — the cookie says whose slot to open; it never says who is
            // allowed to. That is re-derived from this request's own token and
            // re-checked against the directory, every time, so a forged cookie
            // buys nothing and a demoted administrator stops impersonating on
            // their very next request without anything having to expire.
            var served = subject;
            IDisposable? acting = null;
            var target = context.Request.Cookies[BowireImpersonation.CookieName];

            if (!string.IsNullOrWhiteSpace(target)
                && !string.Equals(target, subject, StringComparison.Ordinal))
            {
                if (directory.Describe(context.User, subject).IsAdmin)
                {
                    served = target.Trim();
                    acting = BowireImpersonation.Enter(subject, served);

                    // Reads are not recorded: burying the handful of lines that
                    // matter under a day of navigation is how an audit trail
                    // stops being readable. What changed something is.
                    if (audit is not null && !HttpMethods.IsGet(context.Request.Method)
                        && !HttpMethods.IsHead(context.Request.Method)
                        && !HttpMethods.IsOptions(context.Request.Method)
                        && !context.Request.Path.Value!.EndsWith(
                            BowireImpersonation.EndpointPath, StringComparison.Ordinal))
                    {
                        audit.Record(context.Request.Method, subject, served, context.Request.Path);
                    }
                }
                else
                {
                    // Never an administrator, or no longer one. Drop the cookie
                    // rather than ignore it, so the workbench stops showing a
                    // banner for something that is not happening.
                    context.Response.Cookies.Delete(BowireImpersonation.CookieName);
                }
            }

            using (acting)
            using (BowireTenancy.Enter(served))
            {
                await next(context).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Bring the legacy state across without asking, for an install that said
    /// there is only one person to ask.
    /// </summary>
    /// <remarks>
    /// A failure here is logged and dropped rather than raised. The request
    /// that happened to be first is not the place to surface it: the person
    /// can still work, the original files are still on disk, and the
    /// workbench's own offer is still reachable — whereas a 500 on sign-in
    /// looks like Bowire is down.
    /// </remarks>
    private static void AutoMigrate(string storageRoot, string subject, ILogger? logger)
    {
        try
        {
            var plan = BowireUserMigrator.Plan(storageRoot, subject, BowireUserMigrationMode.Auto);
            if (plan.State != BowireUserMigrationState.Available) return;

            var receipt = BowireUserMigrator.Apply(plan);
            if (logger?.IsEnabled(LogLevel.Information) == true)
            {
                logger.LogInformation(
                    "Migrated {Files} file(s), {Bytes} byte(s) from {Source} into slot {Slot}.",
                    receipt.Files, receipt.Bytes, receipt.Source, plan.Slug);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger?.LogWarning(ex,
                "Could not migrate the existing single-user state into this identity's slot. "
                + "The original files are untouched; the workbench can still offer the migration.");
        }
    }
}
