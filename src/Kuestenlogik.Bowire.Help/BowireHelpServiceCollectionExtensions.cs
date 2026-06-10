// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Help;
using Kuestenlogik.Bowire.Help.Provider;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Opt-in registration for the in-app help provider. Embedded hosts
/// call <c>builder.Services.AddBowireHelp()</c> alongside
/// <c>app.MapBowire()</c>; standalone tool + Docker image take this
/// package as a transitive reference so the CLI surface gets help
/// without extra wiring.
/// </summary>
public static class BowireHelpServiceCollectionExtensions
{
    /// <summary>
    /// Register the markdown-backed <see cref="IBowireHelpProvider"/>
    /// as a singleton. The provider scans the embedded docs/ subset
    /// once at construction; cost is amortised over the host's
    /// lifetime.
    /// </summary>
    public static IServiceCollection AddBowireHelp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IBowireHelpProvider, MarkdownHelpProvider>();
        return services;
    }
}
