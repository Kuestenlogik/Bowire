// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kuestenlogik.Bowire.Lighthouse;

/// <summary>
/// DI wiring for Lighthouse (#102). Registers the Core engine — the outcome
/// ledger, the probe runner, and the <see cref="TimeProvider"/>-backed
/// scheduler. Registers <b>no</b> <see cref="ISignaler"/>: outbound channels are
/// opt-in and contributed by their own packages, so a host that calls only this
/// runs probes and writes the ledger without any outbound call. The host must
/// supply an <see cref="IProbeExecutor"/> (the recording-replay implementation).
/// </summary>
public static class LighthouseServiceCollectionExtensions
{
    /// <summary>
    /// Register the Lighthouse Core engine with the ledger rooted at
    /// <paramref name="ledgerRoot"/> (default <c>~/.bowire/lighthouse</c>).
    /// Idempotent for the ledger + scheduler singletons.
    /// </summary>
    public static IServiceCollection AddBowireLighthouse(this IServiceCollection services, string? ledgerRoot = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var root = string.IsNullOrWhiteSpace(ledgerRoot) ? DefaultLedgerRoot() : ledgerRoot;

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new OutcomeLedger(root));
        services.TryAddSingleton<ProbeRunner>();
        services.TryAddSingleton<IProbeScheduler, TimeProviderProbeScheduler>();
        // TimeProviderProbeScheduler is also resolvable concretely for hosts
        // that want RunProbeLoopAsync directly.
        services.TryAddSingleton(sp => (TimeProviderProbeScheduler)sp.GetRequiredService<IProbeScheduler>());
        return services;
    }

    private static string DefaultLedgerRoot()
    {
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.None);
        return string.IsNullOrEmpty(home)
            ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bowire", "lighthouse")
            : System.IO.Path.Combine(home, ".bowire", "lighthouse");
    }
}
