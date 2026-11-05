// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// <c>AddHttpClient</c> registers <see cref="HttpClient"/> itself as the
/// factory's default-named client. A singleton whose constructor takes one
/// keeps the factory's handler entry, and with it the host's root service
/// provider, so a disposed host is never collected (#989). No singleton
/// <c>AddBowire</c> registers may take an <see cref="HttpClient"/> from DI.
/// </summary>
public sealed class NoSingletonKeepsAFactoryClientTests
{
    [Fact]
    public void No_Singleton_From_AddBowire_Takes_An_HttpClient()
    {
        var services = new ServiceCollection();
        services.AddBowire();

        var offenders = services
            .Where(d => d.Lifetime == ServiceLifetime.Singleton && d.ImplementationType is not null)
            .Select(d => d.ImplementationType!)
            .Distinct()
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(HttpClient))))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_Update_Check_Service_Resolves_Without_A_Factory_Client()
    {
        var services = new ServiceCollection();
        services.AddBowire();

        Assert.Contains(services, d => d.ServiceType == typeof(HttpClient));
        Assert.DoesNotContain(
            typeof(PluginUpdateCheckService).GetConstructors(),
            c => c.GetParameters().Length > 0);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<PluginUpdateCheckService>());
    }
}
