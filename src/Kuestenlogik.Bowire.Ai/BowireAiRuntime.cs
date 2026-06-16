// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.AI;

namespace Kuestenlogik.Bowire.Ai;

/// <summary>
/// Holds the live AI configuration + the active provider client. The
/// Settings UI (#63) calls <see cref="Update"/> to swap provider /
/// endpoint / model at runtime; <see cref="MutableChatClient"/> wraps
/// this so the singleton <see cref="IChatClient"/> registration always
/// dispatches to the current provider without restarting the host.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provider dispatch is plugin-based.</b> The runtime never imports
/// a provider SDK directly. Instead it receives the registered
/// <see cref="IBowireAiProviderFactory"/> list from DI and asks them
/// in order for a matching client. Embedded hosts that only install
/// <c>Kuestenlogik.Bowire.Ai</c> get the Ollama factory; adding
/// <c>.OpenAi</c> / <c>.Anthropic</c> / <c>.Mcp</c> registers each
/// additional factory. Without a matching factory the active client
/// stays <c>null</c> and <c>POST /api/ai/chat</c> returns 503 with
/// a configurable error.
/// </para>
/// <para>
/// Held as a singleton. Concurrent reads of <see cref="Current"/> race
/// against <see cref="Update"/> intentionally — the worst-case is a
/// single in-flight chat call landing on the previous client, which is
/// safer than locking the chat path for the duration of a request.
/// </para>
/// <para>
/// Implements <see cref="IDisposable"/> so the DI container disposes
/// the active provider client (and the inner socket-owning instance
/// each factory hands back) on host shutdown. <see cref="Update"/>
/// already disposes the prior pair on hot-swap; <see cref="Dispose"/>
/// covers the final shutdown leg.
/// </para>
/// </remarks>
public sealed class BowireAiRuntime : IDisposable
{
    private readonly object _lock = new();
    private readonly IBowireAiProviderFactory[] _factories;
    private BowireAiOptions _options;
    private IChatClient? _client;
    private IDisposable? _innerClient;
    private bool _disposed;

    public BowireAiRuntime(BowireAiOptions initialOptions, IEnumerable<IBowireAiProviderFactory>? factories = null)
    {
        ArgumentNullException.ThrowIfNull(initialOptions);
        // No factory list = bare-bones constructor path used by
        // tests and small embedded hosts that just want the Ollama
        // default. The DI extension (AddBowireAi) always feeds the
        // resolved enumerable through, which already contains the
        // Ollama factory + any additional provider packages — so
        // production paths never fall through here.
        var arr = factories?.ToArray() ?? [];
        _factories = arr.Length == 0
            ? [new OllamaChatProviderFactory()]
            : arr;
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
        // Dispose the wrapper first; it cooperatively forwards Dispose
        // to the inner client. Then dispose the inner reference
        // explicitly in case a future wrapper variant stops forwarding —
        // every factory documents Dispose as idempotent, so the double
        // call is a no-op on the second hit.
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

    /// <summary>
    /// Iterate the registered provider factories and ask the first
    /// match to build a client. Returns <c>(null, null)</c> when no
    /// factory matches the configured provider id or the matching
    /// factory rejects the options (missing API key, missing endpoint,
    /// &amp;c.). The chat endpoint translates that into a 503 the
    /// workbench renders as "configure your provider first".
    /// </summary>
    private (IChatClient? Client, IDisposable? Inner) Build(BowireAiOptions opts)
    {
        var providerId = opts.ProviderId ?? string.Empty;
        foreach (var factory in _factories)
        {
            if (factory.Matches(providerId))
                return factory.Build(opts);
        }
        return (null, null);
    }

    private static BowireAiOptions Clone(BowireAiOptions src) => new()
    {
        ProviderId = src.ProviderId,
        Endpoint = src.Endpoint,
        Model = src.Model,
        ApiKey = src.ApiKey,
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
                + "Check Settings → AI or set --ai-provider / --ai-endpoint / --ai-api-key.");
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
