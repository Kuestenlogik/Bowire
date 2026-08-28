// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Kuestenlogik.Bowire.Projects;
using Microsoft.AspNetCore.Builder;
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

            using (BowireTenancy.Enter(subject))
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
