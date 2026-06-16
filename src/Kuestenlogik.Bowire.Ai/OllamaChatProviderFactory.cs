// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Kuestenlogik.Bowire.Ai;

/// <summary>
/// Default <see cref="IBowireAiProviderFactory"/> that builds an
/// <see cref="IChatClient"/> over OllamaSharp. Handles both the
/// <c>ollama</c> id (default endpoint <c>http://localhost:11434</c>)
/// and the <c>lmstudio</c> id (<c>http://localhost:1234</c>) because
/// LM Studio speaks Ollama's wire shape. Ships with the core
/// <c>Kuestenlogik.Bowire.Ai</c> package — every embedder gets the
/// local-first path for free.
/// </summary>
internal sealed class OllamaChatProviderFactory : IBowireAiProviderFactory
{
    public bool Matches(string providerId) =>
        string.Equals(providerId, "ollama", StringComparison.OrdinalIgnoreCase)
        || string.Equals(providerId, "lmstudio", StringComparison.OrdinalIgnoreCase);

    public (IChatClient? Client, IDisposable? Inner) Build(BowireAiOptions opts)
    {
        var endpoint = string.IsNullOrEmpty(opts.Endpoint)
            ? "http://localhost:11434"
            : opts.Endpoint;

        // Wrap the raw OllamaApiClient with FunctionInvokingChatClient
        // so tool calls (#108 Phase 2 + #109 Phase 3) actually round-
        // trip: the base IChatClient stops after the model emits a
        // FunctionCallContent and never invokes the tool body. The
        // MEAI extension reads the tool list from ChatOptions, invokes
        // matching AIFunctions, feeds the result back to the model, and
        // repeats until the model produces final text content.
        //
        // The runtime holds both the wrapper (returned as the client)
        // and the raw OllamaApiClient (returned as the inner) so
        // Dispose + Update have an explicit handle on the socket-owning
        // resource rather than relying on ChatClientBuilder to forward
        // Dispose correctly — that's our IDisposable contract pinning
        // #25's "no socket pool leak across Settings-UI saves" rule.
        var inner = new OllamaApiClient(new Uri(endpoint), opts.Model);
        var client = new ChatClientBuilder(inner)
            .UseFunctionInvocation()
            .Build();
        return (client, inner);
    }
}
