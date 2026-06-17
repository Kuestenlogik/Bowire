// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Mock.Management;

/// <summary>
/// DI helpers for the mock-management surface introduced in #56. Pair
/// with <see cref="BowireMockManagementEndpoints.MapBowireMockManagement"/>
/// at host wire-in time.
/// </summary>
public static class BowireMockManagementServiceCollectionExtensions
{
    /// <summary>
    /// Register the <see cref="BowireMockHostManager"/> singleton so
    /// the endpoints + future MCP tools can resolve it. Single owner
    /// of mock-server lifecycle after the #223 consolidation —
    /// MockRegistry + the parallel <c>/api/mock/*</c> surface are
    /// gone. Idempotent — re-calling is a no-op.
    /// </summary>
    public static IServiceCollection AddBowireMockManagement(this IServiceCollection services)
    {
        services.AddSingleton<BowireMockHostManager>();
        return services;
    }
}
