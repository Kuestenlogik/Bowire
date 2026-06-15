// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Kuestenlogik.Bowire.Ai;

/// <summary>
/// Holds the live AI configuration + the active provider client. The
/// Settings UI (#63) calls <see cref="Update"/> to swap provider /
/// endpoint / model at runtime; <see cref="MutableChatClient"/> wraps
/// this so the singleton <see cref="IChatClient"/> registration always
/// dispatches to the current provider without restarting the host.
/// </summary>
/// <remarks>
/// Held as a singleton. Concurrent reads of <see cref="Current"/> race
/// against <see cref="Update"/> intentionally — the worst-case is a
/// single in-flight chat call landing on the previous client, which is
/// safer than locking the chat path for the duration of a request.
/// <para>
/// Implements <see cref="IDisposable"/> so the DI container disposes
/// the active provider client (and the underlying OllamaSharp
/// <see cref="OllamaApiClient"/> that owns the HTTP socket pool) on
/// host shutdown. <see cref="Update"/> already disposes the prior pair
/// on hot-swap; <see cref="Dispose"/> covers the final shutdown leg.
/// </para>
/// </remarks>
public sealed class BowireAiRuntime : IDisposable
{
    private readonly object _lock = new();
    private BowireAiOptions _options;
    private IChatClient? _client;
    // The raw provider client that backs _client. ChatClientBuilder's
    // wrapper forwards Dispose, but we keep the inner reference so the
    // runtime owns the OllamaSharp socket pool's lifetime directly —
    // documented ownership > implicit forwarding through a third-party
    // builder. Both get disposed on hot-swap (Update) + shutdown (Dispose).
    private IDisposable? _innerClient;
    private bool _disposed;

    public BowireAiRuntime(BowireAiOptions initialOptions)
    {
        ArgumentNullException.ThrowIfNull(initialOptions);
        _options = Clone(initialOptions);
        (_client, _innerClient) = Build(_options);
    }

    /// <summary>Snapshot of the current options. Returned copy — callers can't mutate the live record.</summary>
    public BowireAiOptions Options
    {
        get
        {
            lock (_lock) return Clone(_options);
        }
    }

    /// <summary>The current provider client, or <c>null</c> when the configured provider isn't supported in this build.</summary>
    public IChatClient? Current
    {
        get
        {
            lock (_lock) return _client;
        }
    }

    /// <summary>
    /// Replace the live options + rebuild the provider client. Returns
    /// the post-update snapshot so callers (config endpoint, settings
    /// dialog) can echo back the canonical state.
    /// </summary>
    public BowireAiOptions Update(BowireAiOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var snapshot = Clone(next);
        var (builtClient, builtInner) = Build(snapshot);
        IChatClient? clientToDispose;
        IDisposable? innerToDispose;
        lock (_lock)
        {
            clientToDispose = _client;
            innerToDispose = _innerClient;
            _options = snapshot;
            _client = builtClient;
            _innerClient = builtInner;
        }
        // Dispose the wrapper first; it cooperatively forwards Dispose to
        // the inner client. Then dispose the inner reference explicitly
        // in case a future wrapper variant stops forwarding — Dispose is
        // documented idempotent on OllamaApiClient so the double-call is
        // a no-op on the second hit.
        clientToDispose?.Dispose();
        innerToDispose?.Dispose();
        return Clone(snapshot);
    }

    public void Dispose()
    {
        IChatClient? clientToDispose;
        IDisposable? innerToDispose;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            clientToDispose = _client;
            innerToDispose = _innerClient;
            _client = null;
            _innerClient = null;
        }
        clientToDispose?.Dispose();
        innerToDispose?.Dispose();
    }

    private static (IChatClient? Client, IDisposable? Inner) Build(BowireAiOptions opts)
    {
        // Phase 2 covers Ollama + LM Studio — both speak Ollama's wire
        // shape on the same OllamaSharp client. Phase 3 widens the
        // switch to cloud connectors; until then unknown provider ids
        // produce a null client and /api/ai/chat returns 503 with a
        // "no client" message rather than a confusing dispatch error.
        if (!IsOllamaShape(opts.ProviderId)) return (null, null);
        var endpoint = string.IsNullOrEmpty(opts.Endpoint)
            ? "http://localhost:11434"
            : opts.Endpoint;
        // Wrap the raw OllamaApiClient with FunctionInvokingChatClient so
        // tool calls (#108 Phase 2 + #109 Phase 3) actually round-trip:
        // base IChatClient stops after the model emits a FunctionCallContent
        // and never invokes the tool body. The MEAI extension reads the
        // tool list from ChatOptions, invokes matching AIFunctions, feeds
        // the result back to the model, and repeats until the model
        // produces final text content.
        //
        // The runtime holds both the wrapper (returned as _client) and
        // the raw OllamaApiClient (held as _innerClient) so Dispose +
        // Update have an explicit handle on the socket-owning resource
        // rather than relying on ChatClientBuilder to forward Dispose
        // correctly — that's our IDisposable contract pinning #25's
        // "no socket pool leak across Settings-UI saves" requirement.
        var inner = new OllamaApiClient(new Uri(endpoint), opts.Model);
        var client = new ChatClientBuilder(inner)
            .UseFunctionInvocation()
            .Build();
        return (client, inner);
    }

    private static bool IsOllamaShape(string providerId) =>
        string.Equals(providerId, "ollama", StringComparison.OrdinalIgnoreCase)
        || string.Equals(providerId, "lmstudio", StringComparison.OrdinalIgnoreCase);

    private static BowireAiOptions Clone(BowireAiOptions src) => new()
    {
        ProviderId = src.ProviderId,
        Endpoint = src.Endpoint,
        Model = src.Model,
        AutoDetectLocal = src.AutoDetectLocal,
    };
}

/// <summary>
/// <see cref="IChatClient"/> proxy that delegates every call to whichever
/// client <see cref="BowireAiRuntime"/> currently holds. Registered as
/// the default singleton so the workbench picks up Settings-UI changes
/// (#63) without a host restart, while still letting embedded hosts win
/// by registering their own <see cref="IChatClient"/> before
/// <see cref="BowireAiServiceCollectionExtensions.AddBowireAi"/>.
/// </summary>
internal sealed class MutableChatClient : IChatClient
{
    private readonly BowireAiRuntime _runtime;

    public MutableChatClient(BowireAiRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inner = _runtime.Current
            ?? throw new InvalidOperationException(
                "Bowire AI: no provider client is registered for the current configuration. "
                + "Check Settings → AI or set --ai-provider / --ai-endpoint.");
        return inner.GetResponseAsync(messages, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inner = _runtime.Current
            ?? throw new InvalidOperationException(
                "Bowire AI: no provider client is registered for the current configuration.");
        return inner.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        _runtime.Current?.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // The runtime owns the inner client's lifetime — disposing the
        // proxy must not pull the rug out from concurrent callers.
    }
}
