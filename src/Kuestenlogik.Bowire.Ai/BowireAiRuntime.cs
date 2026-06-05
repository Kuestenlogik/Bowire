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
/// </remarks>
public sealed class BowireAiRuntime
{
    private readonly object _lock = new();
    private BowireAiOptions _options;
    private IChatClient? _client;

    public BowireAiRuntime(BowireAiOptions initialOptions)
    {
        ArgumentNullException.ThrowIfNull(initialOptions);
        _options = Clone(initialOptions);
        _client = Build(_options);
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
        IChatClient? built = Build(snapshot);
        IChatClient? toDispose;
        lock (_lock)
        {
            toDispose = _client;
            _options = snapshot;
            _client = built;
        }
        toDispose?.Dispose();
        return Clone(snapshot);
    }

    private static OllamaApiClient? Build(BowireAiOptions opts)
    {
        // Phase 2 covers Ollama + LM Studio — both speak Ollama's wire
        // shape on the same OllamaSharp client. Phase 3 widens the
        // switch to cloud connectors; until then unknown provider ids
        // produce a null client and /api/ai/chat returns 503 with a
        // "no client" message rather than a confusing dispatch error.
        if (!IsOllamaShape(opts.ProviderId)) return null;
        var endpoint = string.IsNullOrEmpty(opts.Endpoint)
            ? "http://localhost:11434"
            : opts.Endpoint;
        return new OllamaApiClient(new Uri(endpoint), opts.Model);
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
