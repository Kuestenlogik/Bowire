// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// Assembly-scan registry for <see cref="IBowireAuthProvider"/>
/// implementations — mirrors <see cref="BowireProtocolRegistry"/>.
/// At most one provider is active per process; selection happens via
/// <see cref="BowireAuthOptions.ProviderId"/>.
/// </summary>
/// <remarks>
/// <para>
/// Auth providers are loaded the same way protocol plugins are:
/// assembly-scan over <c>Kuestenlogik.Bowire*</c> in the current
/// <see cref="AppDomain"/>. Sibling plugins under
/// <c>~/.bowire/plugins/</c> land there too (via
/// <c>BowirePluginHost</c>), so a standalone install only needs
/// <c>bowire plugin install Kuestenlogik.Bowire.Auth.Oidc</c> to make
/// OIDC selectable.
/// </para>
/// <para>
/// At-most-one is by design: mixing two auth schemes on the same set
/// of endpoints invites confusion and conflicting <c>HttpContext.User</c>
/// resolution. Operators that need fallback schemes can wire them
/// inside a single provider (e.g. an "oidc+apikey" provider that
/// accepts either).
/// </para>
/// </remarks>
public static class BowireAuthProviderRegistry
{
    /// <summary>
    /// Walk <see cref="AppDomain.CurrentDomain"/> for
    /// <see cref="IBowireAuthProvider"/> implementations and return
    /// them keyed by <see cref="IBowireAuthProvider.Id"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, IBowireAuthProvider> Discover(ILogger? logger = null)
    {
        var result = new Dictionary<string, IBowireAuthProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name) || !name.StartsWith("Kuestenlogik.Bowire", StringComparison.OrdinalIgnoreCase))
                continue;

            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (Exception ex) when (ex is System.Reflection.ReflectionTypeLoadException or TypeLoadException or FileLoadException or FileNotFoundException or BadImageFormatException)
            {
                // Reflection-only assembly walk: a missing transitive
                // ref or a corrupt embedded type lights up here.
                if (logger is not null) AuthProviderLog.EnumerateTypesFailed(logger, name, ex);
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(IBowireAuthProvider).IsAssignableFrom(type)) continue;
                // 3rd-party provider's parameterless ctor can throw any
                // type from its static field initialiser; one bad
                // provider must not prevent the others from registering.
#pragma warning disable CA1031 // Do not catch general exception types
                try
                {
                    if (Activator.CreateInstance(type) is IBowireAuthProvider provider
                        && !string.IsNullOrEmpty(provider.Id))
                    {
                        result[provider.Id] = provider;
                    }
                }
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    if (logger is not null)
                        AuthProviderLog.InstantiationFailed(logger, type.FullName ?? "(unknown)", name, ex);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Apply the selected provider to the host. Called from
    /// <c>AddBowireAuth</c> after the provider id has been resolved
    /// from <see cref="BowireAuthOptions"/> + configuration.
    /// </summary>
    /// <returns>
    /// The active provider, or <c>null</c> when no provider is
    /// selected (the laptop-friendly default).
    /// </returns>
    public static IBowireAuthProvider? ApplyAuthentication(
        IServiceCollection services,
        IConfiguration configuration,
        BowireAuthOptions options,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(options.ProviderId))
            return null;

        var providers = Discover(logger);
        if (!providers.TryGetValue(options.ProviderId, out var provider))
        {
            var known = providers.Count == 0 ? "(none loaded)" : string.Join(", ", providers.Keys);
            throw new InvalidOperationException(
                $"Bowire auth provider '{options.ProviderId}' not found. " +
                $"Loaded providers: {known}. " +
                "Install the matching plugin (e.g. `bowire plugin install Kuestenlogik.Bowire.Auth.Oidc`) or drop the --auth-provider flag.");
        }

        provider.AddAuthentication(services, configuration);

        // Replace the no-op default policy registered by
        // AddBowireAuth() with the provider's real one. Re-adding
        // AddAuthorization is the documented way to override a
        // named policy in ASP.NET — the last-registered factory wins.
        services.AddAuthorization(o =>
        {
            o.AddPolicy(BowireAuthPolicies.Default, p => provider.BuildDefaultPolicy(p));
        });

        services.AddSingleton<IBowireAuthProvider>(provider);
        return provider;
    }
}

/// <summary>
/// Configuration for the Bowire auth seam. Bound from
/// <c>Bowire:Auth</c> by <c>AddBowireAuth</c>.
/// </summary>
public sealed class BowireAuthOptions
{
    /// <summary>
    /// Id of the active <see cref="IBowireAuthProvider"/>. Empty /
    /// null means "no auth" — Bowire's default, equivalent to today's
    /// laptop behaviour.
    /// </summary>
    public string? ProviderId { get; set; }
}

/// <summary>
/// Policy names used by Bowire's endpoint mapping. The single
/// <see cref="Default"/> policy is wired by
/// <see cref="BowireAuthProviderRegistry.ApplyAuthentication"/> and
/// consumed by <c>BowireApiEndpoints.Map</c> via
/// <c>.RequireAuthorization(BowireAuthPolicies.Default)</c>.
/// </summary>
public static class BowireAuthPolicies
{
    public const string Default = "Bowire.Default";
}

/// <summary>
/// Source-generated logger wrappers for the auth-provider scan paths.
/// Spinning these out of the inline calls keeps CA1873 happy (boxing
/// of typeof().FullName + Exception args only happens at the
/// LogLevel the host actually wants).
/// </summary>
internal static partial class AuthProviderLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Couldn't enumerate types in {Assembly} for auth-provider scan.")]
    public static partial void EnumerateTypesFailed(ILogger logger, string assembly, Exception ex);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Auth provider {Type} from {Assembly} failed to instantiate — skipping.")]
    public static partial void InstantiationFailed(ILogger logger, string type, string assembly, Exception ex);
}
