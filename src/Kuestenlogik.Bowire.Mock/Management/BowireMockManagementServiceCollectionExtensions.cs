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
    /// Register the <see cref="MockRegistry"/> singleton so endpoints +
    /// future MCP tools can resolve it. Idempotent — re-calling is a
    /// no-op rather than registering a duplicate.
    /// </summary>
    public static IServiceCollection AddBowireMockManagement(this IServiceCollection services)
    {
        services.AddSingleton<MockRegistry>();
        return services;
    }
}
