// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.WebSocket;

/// <summary>
/// DI registration helpers for the Bowire WebSocket plugin.
/// </summary>
public static class BowireWebSocketServiceCollectionExtensions
{
    /// <summary>
    /// Register the <see cref="IWebSocketEndpointRegistry"/> singleton
    /// the Bowire WebSocket plugin reads on <c>DiscoverAsync</c>, and
    /// optionally seed it with a known endpoint list.
    ///
    /// <para>
    /// Typical embedded-mode use:
    /// </para>
    /// <code>
    /// services.AddBowireWebSocketEndpoints(registry =>
    /// {
    ///     registry.Add(new WebSocketEndpointInfo("/ws/chat", "Chat", "Group chat"));
    ///     registry.Add(new WebSocketEndpointInfo("/ws/notify", "Notifications", null));
    /// });
    /// </code>
    ///
    /// <para>
    /// Safe to call multiple times — repeated calls reuse the registry
    /// the first call registered and invoke their <paramref name="configure"/>
    /// action against that same instance. Endpoints discovered from MVC routes
    /// via <see cref="WebSocketEndpointAttribute"/> are merged on top
    /// of whatever this registry holds at <c>DiscoverAsync</c> time.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configure">
    /// Optional seeding action, invoked right away against the shared
    /// registry. Use it to register WebSocket endpoints declaratively
    /// at host startup instead of imperatively after the container is
    /// built.
    /// </param>
    public static IServiceCollection AddBowireWebSocketEndpoints(
        this IServiceCollection services,
        Action<IWebSocketEndpointRegistry>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // One registry per collection, seeded by every call (#850). A
        // singleton factory per call left DI resolving only the last one,
        // so the endpoints of earlier calls vanished.
        var registry = services
            .FirstOrDefault(d => d.ServiceType == typeof(IWebSocketEndpointRegistry))?
            .ImplementationInstance as WebSocketEndpointRegistry;
        if (registry is null)
        {
            registry = new WebSocketEndpointRegistry();
            services.AddSingleton<IWebSocketEndpointRegistry>(registry);
        }

        configure?.Invoke(registry);
        return services;
    }
}
