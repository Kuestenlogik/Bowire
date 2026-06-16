// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kuestenlogik.Bowire.Ai.Anthropic;

/// <summary>
/// BYOK Anthropic provider. Anthropic.SDK's
/// <c>AnthropicClient.Messages</c> property is itself an
/// <see cref="IChatClient"/>, so this factory wraps it only with
/// <c>UseFunctionInvocation</c> for tool-call round-tripping and
/// returns it. Matches the provider id <c>anthropic</c>
/// case-insensitively. Missing <see cref="BowireAiOptions.ApiKey"/>
/// returns <c>(null, null)</c> so the runtime renders the standard
/// "configure your provider" 503.
/// </summary>
internal sealed class AnthropicChatProviderFactory : IBowireAiProviderFactory
{
    public bool Matches(string providerId) =>
        string.Equals(providerId, "anthropic", StringComparison.OrdinalIgnoreCase);

    public (IChatClient? Client, IDisposable? Inner) Build(BowireAiOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.ApiKey)) return (null, null);

        var anthropic = new AnthropicClient(new APIAuthentication(opts.ApiKey!));
        var inner = anthropic.Messages;
        var wrapped = new ChatClientBuilder(inner)
            .UseFunctionInvocation()
            .Build();
        // AnthropicClient implements IDisposable per the SDK's
        // documentation — hand it back as the disposable so the
        // runtime can flush the HTTP pool on Settings-UI saves.
        return (wrapped, anthropic);
    }
}

/// <summary>
/// DI helper for <see cref="AnthropicChatProviderFactory"/>. Call once
/// after <c>AddBowireAi(...)</c> to register the Claude factory.
/// </summary>
public static class BowireAiAnthropicServiceCollectionExtensions
{
    public static IServiceCollection AddBowireAiAnthropic(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IBowireAiProviderFactory, AnthropicChatProviderFactory>());
        return services;
    }
}
