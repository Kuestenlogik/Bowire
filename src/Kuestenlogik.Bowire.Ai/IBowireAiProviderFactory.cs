// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.AI;

namespace Kuestenlogik.Bowire.Ai;

/// <summary>
/// Plugin seam for AI provider connectors. Each provider package
/// (<c>Kuestenlogik.Bowire.Ai.OpenAi</c> / <c>.Anthropic</c> /
/// <c>.Mcp</c>) registers exactly one implementation against the
/// DI container. The core <c>Kuestenlogik.Bowire.Ai</c> package
/// ships only the Ollama / LM Studio factory and the seam itself
/// — embedded hosts pay only for the providers they install.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a factory, not a direct service registration.</b> The active
/// provider is a runtime pick from the Settings UI (#63), not a
/// compile-time choice. The runtime needs to (a) hold every available
/// factory and (b) build a fresh <see cref="IChatClient"/> when the
/// user swaps providers without restarting the host. The factory
/// shape lets each provider package own its own credential reading,
/// endpoint defaults, and disposable lifetime contract.
/// </para>
/// <para>
/// <b>Factory selection contract.</b> <see cref="BowireAiRuntime"/>
/// iterates registered factories in DI order and picks the first whose
/// <see cref="Matches"/> accepts the configured provider id. Each
/// provider package's <c>AddBowireAi*</c> extension registers the
/// factory as a transient (default) or singleton (if it caches HTTP
/// clients itself); either lifetime works because the runtime only
/// asks for <see cref="Build"/> on hot-swap.
/// </para>
/// </remarks>
public interface IBowireAiProviderFactory
{
    /// <summary>
    /// True when this factory handles the configured
    /// <see cref="BowireAiOptions.ProviderId"/>. Compared
    /// case-insensitively in implementations. A single factory MAY
    /// match multiple ids when they share an SDK shape — the OpenAI
    /// factory matches both <c>openai</c> and <c>openrouter</c>
    /// because they speak the same wire format.
    /// </summary>
    bool Matches(string providerId);

    /// <summary>
    /// Build the active <see cref="IChatClient"/> for these options.
    /// Returns <c>(null, null)</c> when the provider id matches but
    /// the options are incomplete (missing API key, missing endpoint)
    /// — the runtime surfaces this as a 503 with a "configure me"
    /// message rather than throwing. The second tuple item is the
    /// inner socket-owning disposable; the runtime calls
    /// <see cref="IDisposable.Dispose"/> on it during hot-swap and
    /// shutdown so a Settings-UI save doesn't leak the HTTP pool.
    /// May be the same instance as the client when there's no
    /// separate inner — that's fine, the runtime treats double-dispose
    /// as a no-op.
    /// </summary>
    (IChatClient? Client, IDisposable? Inner) Build(BowireAiOptions opts);
}
