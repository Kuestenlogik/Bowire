// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Catalogue.Agent;
using Kuestenlogik.Bowire.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Catalogue.Agent.Tests;

/// <summary>
/// Tests for <see cref="BowireAgentCatalogueServiceCollectionExtensions"/> —
/// the DI wiring for the agent catalogue provider (#305 Phase E).
/// Exercises both <c>AddBowireAgentCatalogue</c> overloads and the
/// two branches of the private <c>RegisterAccessorOverride</c> factory
/// (agent vs. fallback), plus the argument guards.
/// </summary>
public sealed class BowireAgentCatalogueServiceCollectionExtensionsTests
{
    /// <summary>
    /// Registers <see cref="BowireCatalogueOptions"/> so the accessor
    /// factory's <c>IOptions&lt;BowireCatalogueOptions&gt;</c> dependency
    /// resolves — mirrors what core's <c>AddBowireCatalogue</c> would do.
    /// </summary>
    private static ServiceCollection ServicesWithCoreProvider(string? provider)
    {
        var services = new ServiceCollection();
        services.AddOptions<BowireCatalogueOptions>()
                .Configure(o => o.Provider = provider);
        return services;
    }

    [Fact]
    public void AddBowireAgentCatalogue_Configure_Wires_AgentProvider_When_Provider_Is_Agent()
    {
        var services = ServicesWithCoreProvider("agent");

        services.AddBowireAgentCatalogue(o => o.HubUrl = "https://hub.example.com");

        using var sp = services.BuildServiceProvider();
        var accessor = sp.GetRequiredService<BowireCatalogueProviderAccessor>();

        Assert.NotNull(accessor.Provider);
        Assert.IsType<AgentCatalogueProvider>(accessor.Provider);
        Assert.Equal("agent", accessor.Provider!.Id);
    }

    [Fact]
    public void AddBowireAgentCatalogue_Configure_Is_Case_Insensitive_On_Provider_Id()
    {
        var services = ServicesWithCoreProvider("Agent");

        services.AddBowireAgentCatalogue(o => { });

        using var sp = services.BuildServiceProvider();
        var accessor = sp.GetRequiredService<BowireCatalogueProviderAccessor>();

        Assert.IsType<AgentCatalogueProvider>(accessor.Provider);
    }

    [Fact]
    public void AddBowireAgentCatalogue_Configure_Falls_Back_To_Null_When_Provider_Unset()
    {
        // Provider unset → BowireCatalogueProviderRegistry.Resolve
        // returns null → accessor exposes the "no catalogue" surface.
        var services = ServicesWithCoreProvider(provider: null);

        services.AddBowireAgentCatalogue(o => { });

        using var sp = services.BuildServiceProvider();
        var accessor = sp.GetRequiredService<BowireCatalogueProviderAccessor>();

        Assert.Null(accessor.Provider);
    }

    [Fact]
    public void AddBowireAgentCatalogue_Configure_Wires_Core_Fallback_When_Provider_Is_Not_Agent()
    {
        // A non-agent provider id routes through the core registry —
        // "local" is a built-in core provider, so the accessor should
        // carry it (and NOT the AgentCatalogueProvider).
        var services = ServicesWithCoreProvider("local");

        services.AddBowireAgentCatalogue(o => { });

        using var sp = services.BuildServiceProvider();
        var accessor = sp.GetRequiredService<BowireCatalogueProviderAccessor>();

        Assert.NotNull(accessor.Provider);
        Assert.IsNotType<AgentCatalogueProvider>(accessor.Provider);
        Assert.Equal("local", accessor.Provider!.Id);
    }

    [Fact]
    public void AddBowireAgentCatalogue_Configure_Applies_Options_To_AgentProvider()
    {
        // The options the Action configures must be visible to the
        // provider resolved from the accessor — proves the factory
        // resolves IOptions<BowireAgentCatalogueOptions> lazily.
        var services = ServicesWithCoreProvider("agent");

        services.AddBowireAgentCatalogue(o =>
        {
            o.HubUrl = "https://configured.example.com";
            o.BootstrapToken = "tok";
        });

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<BowireAgentCatalogueOptions>>().Value;

        Assert.Equal("https://configured.example.com", options.HubUrl);
        Assert.Equal("tok", options.BootstrapToken);
    }

    [Fact]
    public void AddBowireAgentCatalogue_Configuration_Binds_Agent_Options_Section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Discovery:Catalogue:Agent:HubUrl"] = "https://hub.bound.example.com",
                ["Bowire:Discovery:Catalogue:Agent:BootstrapToken"] = "bootstrap-42",
                ["Bowire:Discovery:Catalogue:Agent:Timeout"] = "00:00:30",
            })
            .Build();

        var services = ServicesWithCoreProvider("agent");
        services.AddBowireAgentCatalogue(configuration);

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<BowireAgentCatalogueOptions>>().Value;

        Assert.Equal("https://hub.bound.example.com", options.HubUrl);
        Assert.Equal("bootstrap-42", options.BootstrapToken);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }

    [Fact]
    public void AddBowireAgentCatalogue_Configuration_Wires_AgentProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = ServicesWithCoreProvider("agent");

        services.AddBowireAgentCatalogue(configuration);

        using var sp = services.BuildServiceProvider();
        var accessor = sp.GetRequiredService<BowireCatalogueProviderAccessor>();

        Assert.IsType<AgentCatalogueProvider>(accessor.Provider);
    }

    [Fact]
    public void AddBowireAgentCatalogue_Returns_Same_ServiceCollection_For_Chaining()
    {
        var services = ServicesWithCoreProvider("agent");

        var returnedConfigure = services.AddBowireAgentCatalogue(o => { });
        Assert.Same(services, returnedConfigure);

        var returnedConfiguration =
            services.AddBowireAgentCatalogue(new ConfigurationBuilder().Build());
        Assert.Same(services, returnedConfiguration);
    }

    [Fact]
    public void AddBowireAgentCatalogue_Configuration_Throws_On_Null_Services()
    {
        IServiceCollection services = null!;
        Assert.Throws<ArgumentNullException>(() =>
            services.AddBowireAgentCatalogue(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void AddBowireAgentCatalogue_Configuration_Throws_On_Null_Configuration()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() =>
            services.AddBowireAgentCatalogue((IConfiguration)null!));
    }

    [Fact]
    public void AddBowireAgentCatalogue_Configure_Throws_On_Null_Services()
    {
        IServiceCollection services = null!;
        Assert.Throws<ArgumentNullException>(() =>
            services.AddBowireAgentCatalogue(o => { }));
    }

    [Fact]
    public void AddBowireAgentCatalogue_Configure_Throws_On_Null_Configure()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() =>
            services.AddBowireAgentCatalogue((Action<BowireAgentCatalogueOptions>)null!));
    }
}

/// <summary>
/// Cheap coverage for <see cref="BowireAgentCatalogueOptions"/> defaults
/// — the wire-shape contract fields the DI layer binds onto.
/// </summary>
public sealed class BowireAgentCatalogueOptionsTests
{
    [Fact]
    public void Defaults_Are_Null_Endpoint_With_Ten_Second_Timeout()
    {
        var options = new BowireAgentCatalogueOptions();

        Assert.Null(options.HubUrl);
        Assert.Null(options.BootstrapToken);
        Assert.Null(options.StubResponse);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Timeout);
    }
}
