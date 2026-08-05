// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
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

    /// <summary>
    /// #560: register the manager wired with plugin-contributed schema
    /// sources, so the workbench's Mocks rail can start an OpenAPI /
    /// protobuf / GraphQL schema mock (not just recording-driven mocks).
    /// The caller enumerates the sources from its plugin loader (the
    /// standalone host does this at startup). Idempotent-friendly: the last
    /// registration wins, so call this instead of the no-arg overload.
    /// </summary>
    public static IServiceCollection AddBowireMockManagement(
        this IServiceCollection services,
        IReadOnlyList<IBowireMockSchemaSource> schemaSources,
        IReadOnlyList<IBowireMockLiveSchemaHandler> liveSchemaHandlers,
        IReadOnlyList<IBowireMockHostingExtension> hostingExtensions)
    {
        ArgumentNullException.ThrowIfNull(schemaSources);
        ArgumentNullException.ThrowIfNull(liveSchemaHandlers);
        ArgumentNullException.ThrowIfNull(hostingExtensions);
        // Factory registration so the DI container owns + disposes the manager
        // (it is IAsyncDisposable); a pre-constructed instance would trip CA2000.
        services.AddSingleton(_ =>
            new BowireMockHostManager(schemaSources, liveSchemaHandlers, hostingExtensions));
        return services;
    }
}
