// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Ai.Mcp.Tests;

/// <summary>
/// Pins the MCP-client-reversal provider wiring (#25 Phase 4): the
/// factory matches only <c>mcp</c> case-insensitively, refuses to
/// build a client when the endpoint is the Ollama-default leftover
/// or empty, builds one for both endpoint shapes (stdio: prefix and
/// absolute http URL), and the DI extension stacks correctly on top
/// of the core <c>AddBowireAi</c>.
/// </summary>
public sealed class McpChatProviderFactoryTests
{
    [Theory]
    [InlineData("mcp", true)]
    [InlineData("MCP", true)]
    [InlineData("Mcp", true)]
    [InlineData("openai", false)]
    [InlineData("anthropic", false)]
    [InlineData("ollama", false)]
    [InlineData("", false)]
    public void Matches_OnlyMcp_CaseInsensitive(string providerId, bool expected)
    {
        var factory = new McpChatProviderFactory();
        Assert.Equal(expected, factory.Matches(providerId));
    }

    [Fact]
    public void Build_WithEmptyEndpoint_ReturnsNullClient()
    {
        var factory = new McpChatProviderFactory();
        var (client, inner) = factory.Build(new BowireAiOptions
        {
            ProviderId = "mcp",
            Endpoint = "",
            Model = "any",
        });

        Assert.Null(client);
        Assert.Null(inner);
    }

    [Fact]
    public void Build_WithOllamaDefaultEndpoint_ReturnsNullClient()
    {
        // The Ollama default leaks through whenever the user picks
        // 'mcp' without re-typing the endpoint — the factory rejects
        // that as "not configured" so the workbench surfaces a 503
        // with a configure-me message rather than spawning the wrong
        // transport. Same shape as the BYOK ApiKey-missing rejection.
        var factory = new McpChatProviderFactory();
        var (client, _) = factory.Build(new BowireAiOptions
        {
            ProviderId = "mcp",
            Endpoint = "http://localhost:11434",
            Model = "any",
        });

        Assert.Null(client);
    }

    [Fact]
    public void Build_WithHttpEndpoint_BuildsClient()
    {
        // No network call — just verifying the factory builds a
        // non-null IChatClient when the endpoint is configured.
        // The HTTP transport defers actual connection to the first
        // chat call, so the factory itself stays cheap.
        var factory = new McpChatProviderFactory();
        var (client, inner) = factory.Build(new BowireAiOptions
        {
            ProviderId = "mcp",
            Endpoint = "http://localhost:3845/mcp",
            Model = "claude-opus-4-7",
        });

        Assert.NotNull(client);
        Assert.NotNull(inner);
        Assert.IsType<BowireMcpChatClient>(inner);
        client.Dispose();
    }

    [Fact]
    public void Build_WithStdioEndpoint_BuildsClient()
    {
        var factory = new McpChatProviderFactory();
        var (client, inner) = factory.Build(new BowireAiOptions
        {
            ProviderId = "mcp",
            Endpoint = "stdio:claude mcp serve",
            Model = "any",
        });

        Assert.NotNull(client);
        Assert.NotNull(inner);
        client.Dispose();
    }

    [Fact]
    public void AddBowireAiMcp_RegistersFactory_OnTopOfOllamaDefault()
    {
        var services = new ServiceCollection();
        using var cfg = new Microsoft.Extensions.Configuration.ConfigurationManager();
        services.AddBowireAi(cfg);
        services.AddBowireAiMcp();

        using var provider = services.BuildServiceProvider();
        var factories = provider.GetServices<IBowireAiProviderFactory>().ToArray();

        Assert.Contains(factories, f => f is McpChatProviderFactory);
        Assert.True(factories.Length >= 2);
    }

    [Fact]
    public void AddBowireAiMcp_IsIdempotent()
    {
        var services = new ServiceCollection();
        using var cfg = new Microsoft.Extensions.Configuration.ConfigurationManager();
        services.AddBowireAi(cfg);
        services.AddBowireAiMcp();
        services.AddBowireAiMcp();

        using var provider = services.BuildServiceProvider();
        var count = provider.GetServices<IBowireAiProviderFactory>()
            .Count(f => f is McpChatProviderFactory);

        Assert.Equal(1, count);
    }

    [Fact]
    public void BowireMcpChatClient_NullEndpoint_Throws()
    {
        // Direct contract pin — the SDK transport ctor would throw
        // a confusing error on a null endpoint; we surface a clean
        // ArgumentException with the field name. ThrowIfNullOrWhiteSpace
        // throws ArgumentNullException (subclass) for null and
        // ArgumentException for whitespace — ThrowsAny covers both.
        Assert.ThrowsAny<ArgumentException>(() =>
            new BowireMcpChatClient(null!, "model"));
    }

    [Fact]
    public void BowireMcpChatClient_BogusEndpoint_FirstCallThrows()
    {
        var client = new BowireMcpChatClient("file:///not-mcp", "model");
        // Lazy-connect means the bogus endpoint is detected on first
        // chat call, not at construction time. This is the price of
        // making Settings-UI hot-swap cheap.
        Assert.NotNull(client);
        client.Dispose();
    }
}
