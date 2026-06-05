// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Ai;

/// <summary>
/// Configuration knobs for the optional <c>Kuestenlogik.Bowire.Ai</c>
/// package (#25). Bound from <c>Bowire:Ai</c>; the CLI's
/// <c>--ai-provider</c> / <c>--ai-endpoint</c> / <c>--ai-model</c> flags
/// feed the same keys via the in-memory configuration overlay.
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
/// <c>ollama</c> against <c>http://localhost:11434</c> -- nothing
/// leaves the machine. Cloud providers (BYOK Anthropic / OpenAI /
/// OpenRouter) land in Phase 3 against the same
/// <see cref="ProviderId"/> seam.
/// </para>
/// </remarks>
public sealed class BowireAiOptions
{
    /// <summary>
    /// Active provider id. <c>"ollama"</c> (default), <c>"lmstudio"</c>,
    /// or the future cloud-provider ids <c>"openai"</c> / <c>"anthropic"</c>
    /// / <c>"openrouter"</c>. Compared case-insensitively.
    /// </summary>
    public string ProviderId { get; set; } = "ollama";

    /// <summary>
    /// Base URL of the provider endpoint. For Ollama:
    /// <c>http://localhost:11434</c>; LM Studio:
    /// <c>http://localhost:1234</c>; cloud providers use their public
    /// API base. Empty -> the provider's own default.
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Model id the provider serves under (e.g. <c>"llama3.2:3b"</c>,
    /// <c>"qwen2.5:7b"</c>, <c>"gpt-4o-mini"</c>). Bowire doesn't
    /// validate the name -- the provider returns its own error if the
    /// model isn't loaded.
    /// </summary>
    public string Model { get; set; } = "llama3.2:3b";

    /// <summary>
    /// Auto-detect Ollama + LM Studio on the standard ports during the
    /// <c>GET /api/ai/probe-local</c> call. Defaults <c>true</c>;
    /// privacy-paranoid users can disable.
    /// </summary>
    public bool AutoDetectLocal { get; set; } = true;
}
