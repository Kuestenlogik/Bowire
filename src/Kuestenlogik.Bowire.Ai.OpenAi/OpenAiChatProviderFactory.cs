// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAI;

namespace Kuestenlogik.Bowire.Ai.OpenAi;

/// <summary>
/// BYOK cloud provider for OpenAI proper and any OpenAI-compatible
/// endpoint (OpenRouter is the explicit second target). Matches the
/// provider ids <c>openai</c> and <c>openrouter</c> case-insensitively.
/// Requires <see cref="BowireAiOptions.ApiKey"/>; missing key returns a
/// <c>(null, null)</c> tuple so the runtime surfaces a configurable 503
/// instead of a confusing SDK-construction failure.
/// </summary>
internal sealed class OpenAiChatProviderFactory : IBowireAiProviderFactory
{
    public bool Matches(string providerId) =>
        string.Equals(providerId, "openai", StringComparison.OrdinalIgnoreCase)
        || string.Equals(providerId, "openrouter", StringComparison.OrdinalIgnoreCase);

    public (IChatClient? Client, IDisposable? Inner) Build(BowireAiOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.ApiKey)) return (null, null);

        var providerId = opts.ProviderId ?? string.Empty;
        var defaultEndpoint = string.Equals(providerId, "openrouter", StringComparison.OrdinalIgnoreCase)
            ? "https://openrouter.ai/api/v1"
            : "https://api.openai.com/v1";
        // Endpoint resolution: if the user left the Ollama default in
        // place (or empty), substitute this provider's cloud default.
        // Otherwise honour the typed value — gives Azure / proxy
        // deployments a clean override.
        var endpoint = (string.IsNullOrWhiteSpace(opts.Endpoint)
                        || string.Equals(opts.Endpoint, "http://localhost:11434", StringComparison.OrdinalIgnoreCase))
            ? defaultEndpoint
            : opts.Endpoint;

        var oaiOpts = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
        var oaiClient = new OpenAIClient(new ApiKeyCredential(opts.ApiKey!), oaiOpts);
        var rawChat = oaiClient.GetChatClient(opts.Model).AsIChatClient();
        var wrapped = new ChatClientBuilder(rawChat)
            .UseFunctionInvocation()
            .Build();
        return (wrapped, wrapped);
    }
}

/// <summary>
/// DI helper for <see cref="OpenAiChatProviderFactory"/>. Call once
/// after <c>AddBowireAi(...)</c> so the runtime sees both the default
/// Ollama factory and this OpenAI / OpenRouter factory.
/// </summary>
public static class BowireAiOpenAiServiceCollectionExtensions
{
    public static IServiceCollection AddBowireAiOpenAi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IBowireAiProviderFactory, OpenAiChatProviderFactory>());
        return services;
    }
}
