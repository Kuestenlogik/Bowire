// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Ai.Anthropic.Tests;

/// <summary>
/// Pins the BYOK Anthropic provider wiring: the factory matches
/// only <c>anthropic</c> case-insensitively, refuses to build a
/// client without an API key, builds one when the key is present,
/// and the DI extension stacks correctly on top of the core
/// <c>AddBowireAi</c>.
/// </summary>
public sealed class AnthropicChatProviderFactoryTests
{
    [Theory]
    [InlineData("anthropic", true)]
    [InlineData("Anthropic", true)]
    [InlineData("ANTHROPIC", true)]
    [InlineData("openai", false)]
    [InlineData("openrouter", false)]
    [InlineData("ollama", false)]
    [InlineData("mcp", false)]
    [InlineData("", false)]
    public void Matches_OnlyAnthropic_CaseInsensitive(string providerId, bool expected)
    {
        var factory = new AnthropicChatProviderFactory();
        Assert.Equal(expected, factory.Matches(providerId));
    }

    [Fact]
    public void Build_WithoutApiKey_ReturnsNullClient()
    {
        var factory = new AnthropicChatProviderFactory();
        var (client, inner) = factory.Build(new BowireAiOptions
        {
            ProviderId = "anthropic",
            ApiKey = null,
            Model = "claude-opus-4-7",
        });

        Assert.Null(client);
        Assert.Null(inner);
    }

    [Fact]
    public void Build_WithEmptyApiKey_ReturnsNullClient()
    {
        var factory = new AnthropicChatProviderFactory();
        var (client, _) = factory.Build(new BowireAiOptions
        {
            ProviderId = "anthropic",
            ApiKey = "   ",
            Model = "claude-opus-4-7",
        });

        Assert.Null(client);
    }

    [Fact]
    public void Build_WithApiKey_BuildsClient()
    {
        // No network call — just verifying the factory constructs
        // a non-null IChatClient when credentials are there. The
        // Anthropic.SDK accepts an arbitrary string at construction
        // time, defers validation to first call.
        var factory = new AnthropicChatProviderFactory();
        var (client, inner) = factory.Build(new BowireAiOptions
        {
            ProviderId = "anthropic",
            ApiKey = "sk-ant-test-not-real",
            Model = "claude-opus-4-7",
        });

        Assert.NotNull(client);
        Assert.NotNull(inner);
        client.Dispose();
    }

    [Fact]
    public void AddBowireAiAnthropic_RegistersFactory_OnTopOfOllamaDefault()
    {
        var services = new ServiceCollection();
        using var cfg = new Microsoft.Extensions.Configuration.ConfigurationManager();
        services.AddBowireAi(cfg);
        services.AddBowireAiAnthropic();

        using var provider = services.BuildServiceProvider();
        var factories = provider.GetServices<IBowireAiProviderFactory>().ToArray();

        Assert.Contains(factories, f => f is AnthropicChatProviderFactory);
        Assert.True(factories.Length >= 2);
    }

    [Fact]
    public void AddBowireAiAnthropic_IsIdempotent()
    {
        var services = new ServiceCollection();
        using var cfg = new Microsoft.Extensions.Configuration.ConfigurationManager();
        services.AddBowireAi(cfg);
        services.AddBowireAiAnthropic();
        services.AddBowireAiAnthropic();

        using var provider = services.BuildServiceProvider();
        var count = provider.GetServices<IBowireAiProviderFactory>()
            .Count(f => f is AnthropicChatProviderFactory);

        Assert.Equal(1, count);
    }
}
