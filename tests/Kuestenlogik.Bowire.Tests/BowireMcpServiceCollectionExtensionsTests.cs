// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Tests;

public class BowireMcpServiceCollectionExtensionsTests
{
    [Fact]
    public void AddBowireMcp_Returns_McpServerBuilder()
    {
        var services = new ServiceCollection();

        var builder = services.AddBowireMcp();

        Assert.NotNull(builder);
        Assert.IsAssignableFrom<IMcpServerBuilder>(builder);
    }

    [Fact]
    public void AddBowireMcp_Registers_BowireMockHandleRegistry_Singleton()
    {
        var services = new ServiceCollection();

        services.AddBowireMcp();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(BowireMockHandleRegistry));
        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Singleton, descriptor!.Lifetime);
    }

    [Fact]
    public void AddBowireMcp_Registers_BowireProtocolRegistry_Singleton()
    {
        var services = new ServiceCollection();

        services.AddBowireMcp();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(BowireProtocolRegistry));
        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Singleton, descriptor!.Lifetime);
    }

    [Fact]
    public async Task AddBowireMcp_Registers_BowireMcpOptions()
    {
        var services = new ServiceCollection();

        services.AddBowireMcp();
        await using var sp = services.BuildServiceProvider();

        var options = sp.GetService<IOptions<BowireMcpOptions>>();
        Assert.NotNull(options);
        Assert.NotNull(options!.Value);
        Assert.Equal("bowire-mcp", options.Value.ServerName);
    }

    [Fact]
    public async Task AddBowireMcp_Configure_Action_Is_Applied()
    {
        var services = new ServiceCollection();

        services.AddBowireMcp(o =>
        {
            o.AllowArbitraryUrls = true;
            o.LoadAllowlistFromEnvironments = false;
            o.MaxSubscribeMs = 1234;
        });

        await using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IOptions<BowireMcpOptions>>().Value;

        Assert.True(resolved.AllowArbitraryUrls);
        Assert.False(resolved.LoadAllowlistFromEnvironments);
        Assert.Equal(1234, resolved.MaxSubscribeMs);
    }

    [Fact]
    public async Task AddBowireMcp_Without_Configure_Action_Uses_Defaults()
    {
        var services = new ServiceCollection();

        services.AddBowireMcp();

        await using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IOptions<BowireMcpOptions>>().Value;

        Assert.False(resolved.AllowArbitraryUrls);
        Assert.True(resolved.LoadAllowlistFromEnvironments);
    }

    [Fact]
    public async Task AddBowireMcp_Resolves_BowireMockHandleRegistry_Instance()
    {
        var services = new ServiceCollection();

        services.AddBowireMcp();
        await using var sp = services.BuildServiceProvider();

        var first = sp.GetRequiredService<BowireMockHandleRegistry>();
        var second = sp.GetRequiredService<BowireMockHandleRegistry>();

        // Singleton — same instance both times.
        Assert.Same(first, second);
    }

    [Fact]
    public async Task AddBowireMcp_Resolves_BowireProtocolRegistry_Singleton_Instance()
    {
        var services = new ServiceCollection();

        services.AddBowireMcp();
        await using var sp = services.BuildServiceProvider();

        // Triggers the AddSingleton factory body (logger + Discover scan).
        var first = sp.GetRequiredService<BowireProtocolRegistry>();
        var second = sp.GetRequiredService<BowireProtocolRegistry>();

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.NotNull(first.Protocols);
    }

    [Fact]
    public async Task AddBowireMcp_ServerInfo_ConfigureCallback_Sets_Bowire_Identity()
    {
        var services = new ServiceCollection();

        services.AddBowireMcp();
        await using var sp = services.BuildServiceProvider();

        // Resolving the SDK's IOptions<McpServerOptions> triggers the
        // configure callback registered via AddMcpServer(o => …), which
        // writes ServerInfo. The lookup is reflective so the test doesn't
        // take a hard compile-time dependency on the SDK's surface — the
        // type may live in either ModelContextProtocol or
        // ModelContextProtocol.Core depending on the package version.
        var optionsType = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.StartsWith("ModelContextProtocol", StringComparison.Ordinal) == true)
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
            })
            .FirstOrDefault(t => t!.FullName == "ModelContextProtocol.Server.McpServerOptions");
        Assert.NotNull(optionsType);

        var iopt = typeof(IOptions<>).MakeGenericType(optionsType!);
        var resolved = sp.GetRequiredService(iopt);
        var value = iopt.GetProperty("Value")!.GetValue(resolved);
        Assert.NotNull(value);

        var serverInfo = value!.GetType().GetProperty("ServerInfo")!.GetValue(value);
        Assert.NotNull(serverInfo);

        var name = (string?)serverInfo!.GetType().GetProperty("Name")!.GetValue(serverInfo);
        var title = (string?)serverInfo.GetType().GetProperty("Title")!.GetValue(serverInfo);
        Assert.Equal("bowire-mcp", name);
        Assert.Contains("Bowire", title, StringComparison.Ordinal);
    }
}
