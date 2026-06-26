// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Sources;

/// <summary>
/// Assembly-scan registry for <see cref="IBowireCatalogueProvider"/>
/// implementations — mirrors <see cref="BowireProtocolRegistry"/> and
/// <c>BowireAuthProviderRegistry</c>. At most one provider is active
/// per process; selection happens via
/// <see cref="BowireCatalogueOptions.Provider"/>.
/// </summary>
/// <remarks>
/// <para>
/// Providers are loaded the same way protocol plugins are: assembly-
/// scan over every loaded <c>Kuestenlogik.Bowire*</c> assembly. The
/// built-in <c>local</c>, <c>http</c>, and <c>consul</c> providers
/// live in the core package; <c>kubernetes</c> and <c>agent</c>
/// providers ship as separate
/// <c>Kuestenlogik.Bowire.Catalogue.*</c> packages so heavyweight
/// dependencies only land in installs that opt-in.
/// </para>
/// <para>
/// At-most-one is by design: mixing two catalogues on the same UI
/// invites confusion about which provider owns which row. Operators
/// that need to aggregate multiple registries can wire an aggregator
/// provider client-side or in front of Bowire (e.g. a small HTTP
/// service that merges Consul + k8s and exposes the union to
/// Bowire's <c>http</c> provider).
/// </para>
/// </remarks>
public static class BowireCatalogueProviderRegistry
{
    /// <summary>
    /// Walk <see cref="AppDomain.CurrentDomain"/> for
    /// <see cref="IBowireCatalogueProvider"/> implementations and
    /// return them keyed by <see cref="IBowireCatalogueProvider.Id"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, IBowireCatalogueProvider> Discover(ILogger? logger = null)
    {
        var result = new Dictionary<string, IBowireCatalogueProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name) || !name.StartsWith("Kuestenlogik.Bowire", StringComparison.OrdinalIgnoreCase))
                continue;

            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (Exception ex) when (ex is ReflectionTypeLoadException or TypeLoadException or FileLoadException or FileNotFoundException or BadImageFormatException)
            {
                // Same defensive pattern as BowireAuthProviderRegistry —
                // a corrupt sibling DLL shouldn't disable catalogue scanning
                // for the rest of the loaded plugins.
                if (logger is not null) CatalogueProviderLog.EnumerateTypesFailed(logger, name, ex);
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(IBowireCatalogueProvider).IsAssignableFrom(type)) continue;
                // 3rd-party provider's parameterless ctor can throw any
                // type from its static field initialiser; one bad
                // provider must not prevent the others from registering.
#pragma warning disable CA1031 // Do not catch general exception types
                try
                {
                    if (Activator.CreateInstance(type) is IBowireCatalogueProvider provider
                        && !string.IsNullOrEmpty(provider.Id))
                    {
                        result[provider.Id] = provider;
                    }
                }
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    if (logger is not null)
                        CatalogueProviderLog.InstantiationFailed(logger, type.FullName ?? "(unknown)", name, ex);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Resolve the currently-configured provider, or <c>null</c> when
    /// the host hasn't selected one. Throws <see cref="InvalidOperationException"/>
    /// when an unknown id is configured so a typo in
    /// <c>appsettings.json</c> surfaces at startup instead of silently
    /// disabling the catalogue.
    /// </summary>
    public static IBowireCatalogueProvider? Resolve(
        BowireCatalogueOptions options,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(options.Provider)) return null;

        var providers = Discover(logger);
        if (providers.TryGetValue(options.Provider, out var provider))
            return provider;

        var known = providers.Count == 0 ? "(none loaded)" : string.Join(", ", providers.Keys);
        throw new InvalidOperationException(
            $"Bowire catalogue provider '{options.Provider}' not found. " +
            $"Loaded providers: {known}. " +
            "Install the matching package (e.g. `Kuestenlogik.Bowire.Catalogue.Kubernetes`) " +
            "or drop the Bowire:Discovery:Catalogue:Provider key.");
    }
}

/// <summary>
/// Source-generated logger wrappers for the catalogue-provider scan
/// paths. Same pattern as
/// <c>BowireAuthProviderRegistry.AuthProviderLog</c>.
/// </summary>
internal static partial class CatalogueProviderLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Couldn't enumerate types in {Assembly} for catalogue-provider scan.")]
    public static partial void EnumerateTypesFailed(ILogger logger, string assembly, Exception ex);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Catalogue provider {Type} from {Assembly} failed to instantiate — skipping.")]
    public static partial void InstantiationFailed(ILogger logger, string type, string assembly, Exception ex);
}
