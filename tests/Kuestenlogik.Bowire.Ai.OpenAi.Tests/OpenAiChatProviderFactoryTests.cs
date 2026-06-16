// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Ai.OpenAi.Tests;

/// <summary>
/// Pins the BYOK OpenAI provider's wiring: the factory matches the
/// two ids it owns case-insensitively, refuses to build a client
/// without an API key, builds one when the key is present, and the
/// DI extension stacks correctly on top of the core AddBowireAi.
/// </summary>
public sealed class OpenAiChatProviderFactoryTests
{
    [Theory]
    [InlineData("openai", true)]
    [InlineData("OpenAI", true)]
    [InlineData("OPENAI", true)]
    [InlineData("openrouter", true)]
    [InlineData("OpenRouter", true)]
    [InlineData("OPENROUTER", true)]
    [InlineData("anthropic", false)]
    [InlineData("ollama", false)]
    [InlineData("mcp", false)]
    [InlineData("", false)]
    public void Matches_OnlyOpenAiShape_CaseInsensitive(string providerId, bool expected)
    {
        var factory = new OpenAiChatProviderFactory();
        Assert.Equal(expected, factory.Matches(providerId));
    }

    [Fact]
    public void Build_WithoutApiKey_ReturnsNullClient()
    {
        // The 503-shaped failure mode for "no key configured" — the
        // chat endpoint translates a null client into a configurable
        // "set your API key" error rather than throwing inside the
        // OpenAI SDK constructor on an empty credential.
        var factory = new OpenAiChatProviderFactory();
        var (client, inner) = factory.Build(new BowireAiOptions
        {
            ProviderId = "openai",
            ApiKey = null,
            Model = "gpt-4o-mini",
        });

        Assert.Null(client);
        Assert.Null(inner);
    }

    [Fact]
    public void Build_WithEmptyApiKey_ReturnsNullClient()
    {
        var factory = new OpenAiChatProviderFactory();
        var (client, _) = factory.Build(new BowireAiOptions
        {
            ProviderId = "openai",
            ApiKey = "   ",
            Model = "gpt-4o-mini",
        });

        Assert.Null(client);
    }

    [Fact]
    public void Build_OpenAi_WithApiKey_BuildsClient()
    {
        // We don't make a network call — just verifying the factory
        // hands back a non-null IChatClient when the credentials are
        // there. The OpenAI SDK accepts an arbitrary string as the
        // API key at construction time and defers validation to first
        // call, which is what we want for fast Settings-UI hot-swap.
        var factory = new OpenAiChatProviderFactory();
        var (client, inner) = factory.Build(new BowireAiOptions
        {
            ProviderId = "openai",
            ApiKey = "sk-test-key-not-real",
            Model = "gpt-4o-mini",
        });

        Assert.NotNull(client);
        Assert.NotNull(inner);
        // Inner === Client for OpenAI because the SDK's OpenAIClient
        // isn't IDisposable on its own; we return the MEAI wrapper as
        // both client and inner so Dispose has a single target.
        Assert.Same(client, inner);
        client.Dispose();
    }

    [Fact]
    public void Build_OpenRouter_HonoursDefaultEndpoint_WhenOllamaDefaultLeaksThrough()
    {
        // The Ollama default leaks through when the user picks
        // 'openrouter' for the first time without re-typing the
        // endpoint — the factory swaps it for the OpenRouter base
        // URL rather than failing or hitting localhost.
        var factory = new OpenAiChatProviderFactory();
        var (client, _) = factory.Build(new BowireAiOptions
        {
            ProviderId = "openrouter",
            Endpoint = "http://localhost:11434",
            ApiKey = "or-test-key-not-real",
            Model = "anthropic/claude-3.5-sonnet",
        });

        Assert.NotNull(client);
        client!.Dispose();
    }

    [Fact]
    public void AddBowireAiOpenAi_RegistersFactory_OnTopOfOllamaDefault()
    {
        var services = new ServiceCollection();
        using var cfg = new Microsoft.Extensions.Configuration.ConfigurationManager();
        services.AddBowireAi(cfg);
        services.AddBowireAiOpenAi();

        using var provider = services.BuildServiceProvider();
        var factories = provider.GetServices<IBowireAiProviderFactory>().ToArray();

        // Both Ollama (from core) and OpenAI (from this package)
        // appear; the runtime picks whichever Matches the configured
        // ProviderId.
        Assert.Contains(factories, f => f is OpenAiChatProviderFactory);
        Assert.True(factories.Length >= 2);
    }

    [Fact]
    public void AddBowireAiOpenAi_IsIdempotent()
    {
        var services = new ServiceCollection();
        using var cfg = new Microsoft.Extensions.Configuration.ConfigurationManager();
        services.AddBowireAi(cfg);
        services.AddBowireAiOpenAi();
        services.AddBowireAiOpenAi();
        services.AddBowireAiOpenAi();

        using var provider = services.BuildServiceProvider();
        var openAiCount = provider.GetServices<IBowireAiProviderFactory>()
            .Count(f => f is OpenAiChatProviderFactory);

        Assert.Equal(1, openAiCount);
    }
}
