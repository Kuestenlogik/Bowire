// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OllamaSharp;

namespace Kuestenlogik.Bowire.Ai;

/// <summary>
/// DI helpers for the optional <c>Kuestenlogik.Bowire.Ai</c> package
/// (#25 Phase 2). Idempotent — re-calling <see cref="AddBowireAi"/>
/// is a no-op rather than registering a duplicate.
/// </summary>
public static class BowireAiServiceCollectionExtensions
{
    /// <summary>
    /// Register an <see cref="IChatClient"/> against the provider id
    /// in <c>Bowire:Ai:ProviderId</c>. Default is Ollama at
    /// <c>http://localhost:11434</c> (auto-detected via
    /// <c>GET /api/ai/probe-local</c>); LM Studio works through the
    /// same OllamaSharp client because the wire shape matches.
    /// Cloud providers (Phase 3) register their own
    /// <see cref="IChatClient"/> implementation behind the same
    /// abstraction.
    /// </summary>
    /// <remarks>
    /// <b>Embedded-host override.</b> If the host already registered
    /// an <c>IChatClient</c> before calling
    /// <see cref="AddBowireAi"/>, Bowire uses that one and skips its
    /// own factory. That's the natural integration point: a host with
    /// existing AI infrastructure (Microsoft.Extensions.AI.OpenAI,
    /// Azure OpenAI, a custom in-house gateway) routes Bowire's
    /// chat calls through its already-configured client.
    /// </remarks>
    public static IServiceCollection AddBowireAi(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<BowireAiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var opts = new BowireAiOptions();
        configuration.GetSection("Bowire:Ai").Bind(opts);
        configure?.Invoke(opts);

        services.TryAddSingleton(opts);

        // Host-supplied IChatClient wins. We only register our own
        // factory when no one's claimed the slot yet.
        services.TryAddSingleton<IChatClient>(sp => BuildClient(opts));
        return services;
    }

    private static OllamaApiClient BuildClient(BowireAiOptions opts)
    {
        // Phase 2 covers Ollama + LM Studio -- both speak Ollama's
        // wire shape on the same OllamaSharp client. The provider id
        // dimension only matters for the dashboard / settings labels
        // and for the Phase-3 switch into cloud connectors.
        var endpoint = string.IsNullOrEmpty(opts.Endpoint)
            ? "http://localhost:11434"
            : opts.Endpoint;
        return new OllamaApiClient(new Uri(endpoint), opts.Model);
    }
}
