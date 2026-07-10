// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>DI wiring for the security scanner's host-facing services.</summary>
public static class BowireSecurityScannerServiceCollectionExtensions
{
    /// <summary>
    /// Register the scanner-backed <see cref="ISecurityScanProbeRunner"/> so the
    /// AI scan-orchestration endpoint (<c>POST /api/ai/security-scan</c>, #104)
    /// can execute live probes. Opt-in — a host that doesn't call this runs the
    /// orchestration in plan-only mode (no probe execution).
    /// </summary>
    public static IServiceCollection AddBowireSecurityScanProbeRunner(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISecurityScanProbeRunner, ScannerProbeRunner>();
        return services;
    }
}
