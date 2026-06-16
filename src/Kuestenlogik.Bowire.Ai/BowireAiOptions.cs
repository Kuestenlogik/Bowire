// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Ai;

/// <summary>
/// Configuration knobs for the optional <c>Kuestenlogik.Bowire.Ai</c>
/// package (#25). Bound from <c>Bowire:Ai</c>; the CLI's
/// <c>--ai-provider</c> / <c>--ai-endpoint</c> / <c>--ai-model</c> /
/// <c>--ai-api-key</c> flags feed the same keys via the in-memory
/// configuration overlay.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in by construction.</b> The package is only present if the
/// host added <c>Kuestenlogik.Bowire.Ai</c> as a PackageReference. The
/// standalone CLI bundles it; embedded hosts pick it up explicitly.
/// Without the package, none of these options exist and the AI tab's
/// hint engine stays the only AI surface.
/// </para>
/// <para>
/// <b>Privacy stance per the ADR.</b> The default provider is
/// <c>ollama</c> against <c>http://localhost:11434</c> — nothing
/// leaves the machine. BYOK cloud providers (Phase 3:
/// <c>openai</c> / <c>anthropic</c> / <c>openrouter</c>) require
/// <see cref="ApiKey"/>; the key stays on the user's machine
/// (persisted in <c>ai-config.json</c>) and is never proxied through
/// Küstenlogik infrastructure. The MCP-client-reversal provider
/// (Phase 4: <c>mcp</c>) routes prompts through a user-controlled
/// MCP host — same local-first stance, no Bowire egress.
/// </para>
/// </remarks>
public sealed class BowireAiOptions
{
    /// <summary>
    /// Active provider id. <c>"ollama"</c> (default), <c>"lmstudio"</c>,
    /// the BYOK cloud ids <c>"openai"</c> / <c>"anthropic"</c> /
    /// <c>"openrouter"</c>, or <c>"mcp"</c> for the MCP-client-reversal
    /// path. Compared case-insensitively.
    /// </summary>
    public string ProviderId { get; set; } = "ollama";

    /// <summary>
    /// Base URL of the provider endpoint. For Ollama:
    /// <c>http://localhost:11434</c>; LM Studio:
    /// <c>http://localhost:1234</c>; OpenAI: <c>https://api.openai.com/v1</c>;
    /// OpenRouter: <c>https://openrouter.ai/api/v1</c>; Anthropic uses
    /// the SDK's built-in endpoint and ignores this field. For
    /// <c>"mcp"</c> the value is either an absolute MCP-over-HTTP URL
    /// (<c>http://localhost:3845/mcp</c>) or a <c>stdio:</c>-prefixed
    /// command line (<c>stdio:claude mcp serve</c>). Empty falls back
    /// to the provider's own default where one exists.
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Model id the provider serves under (e.g. <c>"llama3.2:3b"</c>,
    /// <c>"qwen2.5:7b"</c>, <c>"gpt-4o-mini"</c>,
    /// <c>"claude-opus-4-7"</c>). Bowire doesn't validate the name —
    /// the provider returns its own error if the model isn't loaded.
    /// For <c>"mcp"</c> the value is the model id the upstream MCP
    /// host should route the sampling request to.
    /// </summary>
    public string Model { get; set; } = "llama3.2:3b";

    /// <summary>
    /// BYOK API key for the cloud providers. Required for
    /// <c>"openai"</c> / <c>"anthropic"</c> / <c>"openrouter"</c> —
    /// the provider's own SDK reads it. Ignored for local providers
    /// (<c>"ollama"</c> / <c>"lmstudio"</c>) and the MCP path. Stored
    /// in <c>ai-config.json</c> next to the rest of the options;
    /// never proxied through Küstenlogik infrastructure.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Auto-detect Ollama + LM Studio on the standard ports during the
    /// <c>GET /api/ai/probe-local</c> call. Defaults <c>true</c>;
    /// privacy-paranoid users can disable.
    /// </summary>
    public bool AutoDetectLocal { get; set; } = true;
}
