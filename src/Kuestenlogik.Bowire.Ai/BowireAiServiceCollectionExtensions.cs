// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kuestenlogik.Bowire.Ai;

/// <summary>
/// DI helpers for the optional <c>Kuestenlogik.Bowire.Ai</c> package
/// (#25). Idempotent — re-calling <see cref="AddBowireAi"/> is a no-op
/// rather than registering a duplicate.
/// </summary>
public static class BowireAiServiceCollectionExtensions
{
    /// <summary>
    /// Register an <see cref="IChatClient"/> against the provider id in
    /// <c>Bowire:Ai:ProviderId</c>. Default is Ollama at
    /// <c>http://localhost:11434</c> (auto-detected via
    /// <c>GET /api/ai/probe-local</c>); LM Studio works through the
    /// same OllamaSharp client because the wire shape matches.
    /// Cloud providers (#25 Phase 3) register their own
    /// <see cref="IChatClient"/> implementation behind the same
    /// abstraction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Embedded-host override.</b> If the host already registered an
    /// <c>IChatClient</c> before calling <see cref="AddBowireAi"/>,
    /// Bowire uses that one and skips its own factory. That's the
    /// natural integration point: a host with existing AI infrastructure
    /// (Microsoft.Extensions.AI.OpenAI, Azure OpenAI, a custom in-house
    /// gateway) routes Bowire's chat calls through its already-configured
    /// client. The settings dialog's "AI" tab still renders, but the save
    /// path becomes a no-op tagged "host-managed" so the user understands
    /// the workbench can't swap a provider the host owns.
    /// </para>
    /// <para>
    /// <b>Config overlay precedence (#63).</b> Effective options layer
    /// in this order, low-to-high: <see cref="BowireAiOptions"/> defaults
    /// → <c>Bowire:Ai</c> section (env vars / CLI flags via
    /// <see cref="IConfiguration"/>) → <paramref name="configure"/>
    /// callback → user-config file (<c>ai-config.json</c>). Disk wins
    /// because it represents an explicit user choice from the Settings
    /// UI; a temporary CLI flag never silently overwrites that choice.
    /// </para>
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

        // User-config (persisted via IBowireUserStore) is the highest
        // layer — it captures explicit Settings-UI picks. We overlay
        // field-by-field so a partially-saved file still inherits the
        // unfilled fields from the lower layers instead of resetting
        // them to BowireAiOptions defaults.
        var persisted = BowireAiUserConfigStore.TryLoad();
        if (persisted is not null)
        {
            if (!string.IsNullOrEmpty(persisted.ProviderId)) opts.ProviderId = persisted.ProviderId;
            if (!string.IsNullOrEmpty(persisted.Endpoint)) opts.Endpoint = persisted.Endpoint;
            if (!string.IsNullOrEmpty(persisted.Model)) opts.Model = persisted.Model;
            opts.AutoDetectLocal = persisted.AutoDetectLocal;
        }

        services.TryAddSingleton(opts);

        // BowireAiRuntime owns the live IChatClient. POST /api/ai/config
        // calls Update on it to hot-swap; MutableChatClient proxies to
        // whichever client the runtime currently holds so the singleton
        // IChatClient registration stays valid across swaps.
        services.TryAddSingleton(sp => new BowireAiRuntime(sp.GetRequiredService<BowireAiOptions>()));

        // Host-supplied IChatClient wins by virtue of TryAdd — if the
        // host registered one before this call, our MutableChatClient
        // factory is skipped and the host's client carries all chat
        // traffic. Settings-UI saves still persist (the file is just a
        // hint for next startup), but the runtime swap is a no-op
        // because the host's IChatClient is the one DI hands out.
        services.TryAddSingleton<IChatClient>(sp =>
            new MutableChatClient(sp.GetRequiredService<BowireAiRuntime>()));

        return services;
    }
}
