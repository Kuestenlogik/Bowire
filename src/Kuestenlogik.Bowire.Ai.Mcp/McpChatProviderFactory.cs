// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kuestenlogik.Bowire.Ai.Mcp;

/// <summary>
/// MCP-client-reversal provider (#25 Phase 4). Wraps the user's MCP
/// host as an <see cref="IChatClient"/> via <see cref="BowireMcpChatClient"/>.
/// Matches the provider id <c>mcp</c> case-insensitively. Requires
/// a non-default <see cref="BowireAiOptions.Endpoint"/> — either an
/// absolute http(s) URL or a <c>stdio:</c>-prefixed command line.
/// </summary>
internal sealed class McpChatProviderFactory : IBowireAiProviderFactory
{
    public bool Matches(string providerId) =>
        string.Equals(providerId, "mcp", StringComparison.OrdinalIgnoreCase);

    public (IChatClient? Client, IDisposable? Inner) Build(BowireAiOptions opts)
    {
        // The Ollama default leaks through when the user picks 'mcp'
        // for the first time without typing an endpoint — treat that
        // as "not configured" and surface the standard 503.
        if (string.IsNullOrWhiteSpace(opts.Endpoint)
            || string.Equals(opts.Endpoint, "http://localhost:11434", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var inner = new BowireMcpChatClient(opts.Endpoint, opts.Model);
        var wrapped = new ChatClientBuilder(inner)
            .UseFunctionInvocation()
            .Build();
        return (wrapped, inner);
    }
}

/// <summary>
/// DI helper for <see cref="McpChatProviderFactory"/>. Call once
/// after <c>AddBowireAi(...)</c> to register the MCP factory.
/// </summary>
public static class BowireAiMcpServiceCollectionExtensions
{
    public static IServiceCollection AddBowireAiMcp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IBowireAiProviderFactory, McpChatProviderFactory>());
        return services;
    }
}
