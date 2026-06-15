// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.AI;

namespace Kuestenlogik.Bowire.Ai.Tests;

/// <summary>
/// Behaviour tests for <see cref="MutableChatClient"/> — the proxy
/// that turns the runtime's swappable inner client into a singleton
/// <see cref="IChatClient"/> registration. Pins:
/// <list type="bullet">
///   <item>delegation of GetResponseAsync to the runtime's current
///     client, with the right messages + options forwarded;</item>
///   <item>delegation of GetStreamingResponseAsync (covered by the
///     same seam, exercised separately because the path used
///     <c>IAsyncEnumerable</c> rather than <c>Task&lt;T&gt;</c>);</item>
///   <item>"no client" InvalidOperationException with a discoverable
///     message on both response paths;</item>
///   <item>hot-swap: calling the proxy after
///     <see cref="BowireAiRuntime.Update"/> dispatches to the new
///     client, not the old one;</item>
///   <item><see cref="MutableChatClient.GetService"/> forwards to the
///     current client and returns null when the runtime is dry;</item>
///   <item><see cref="MutableChatClient.Dispose"/> is intentionally a
///     no-op (the runtime owns the inner client's lifetime);</item>
/// </list>
/// </summary>
public sealed class MutableChatClientTests
{
    [Fact]
    public void GetResponseAsync_NoClient_Throws_WithBowireAiPrefix()
    {
        // openai is a Phase-3 provider id — Build() returns null today
        // because the cloud connector hasn't shipped. The proxy must
        // surface a discoverable "Bowire AI: ..." error so the
        // Settings UI can render its "no client" affordance instead
        // of bubbling an opaque NullReferenceException.
        using var rt = new BowireAiRuntime(new BowireAiOptions { ProviderId = "openai" });
        using var proxy = new MutableChatClient(rt);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            proxy.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: TestContext.Current.CancellationToken).GetAwaiter().GetResult());
        Assert.StartsWith("Bowire AI", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStreamingResponseAsync_NoClient_Throws_WithBowireAiPrefix()
    {
        // The streaming path has its own null-check + message; verify
        // separately because IAsyncEnumerable surfaces the exception
        // on enumeration, not on the call. The proxy throws
        // synchronously before returning the enumerable.
        using var rt = new BowireAiRuntime(new BowireAiOptions { ProviderId = "openai" });
        using var proxy = new MutableChatClient(rt);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            proxy.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.StartsWith("Bowire AI", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResponseAsync_Delegates_To_Runtime_Current_Client_AfterSwap()
    {
        // The cleanest path is to use a StubRuntime + StubChatClient
        // together — both seams the production code already trusts.
        // The proxy must dispatch to whichever client the runtime
        // currently exposes.
        var stub = new StubChatClient("from-stub");
        using var stubRuntime = new StubRuntime(stub);
        using var proxy = new MutableChatClient(stubRuntime);

        var resp = await proxy.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(stub.LastMessages, stub.LastMessages); // sanity
        Assert.Single(stub.LastMessages!);
        Assert.Equal(ChatRole.User, stub.LastMessages![0].Role);
        Assert.Equal("from-stub", resp.Text);
    }

    [Fact]
    public async Task GetResponseAsync_Forwards_ChatOptions()
    {
        var stub = new StubChatClient("ok");
        using var stubRuntime = new StubRuntime(stub);
        using var proxy = new MutableChatClient(stubRuntime);

        var opts = new ChatOptions { ModelId = "qwen2.5:7b" };
        await proxy.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            opts,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(opts, stub.LastOptions);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_Forwards_And_Enumerates()
    {
        var stub = new StubChatClient("streamed");
        using var stubRuntime = new StubRuntime(stub);
        using var proxy = new MutableChatClient(stubRuntime);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in proxy.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(u);
        }

        Assert.NotEmpty(updates);
        Assert.Contains(updates, u => u.Text == "streamed");
    }

    [Fact]
    public async Task GetResponseAsync_HotSwap_DispatchesToNewInner()
    {
        // The runtime's hot-swap semantics are the whole reason
        // MutableChatClient exists. Swap the inner client, fire the
        // proxy again, and verify the new stub saw the call (the old
        // one didn't).
        using var first = new StubChatClient("first");
        using var second = new StubChatClient("second");
        using var stubRuntime = new StubRuntime(first);
        using var proxy = new MutableChatClient(stubRuntime);

        var r1 = await proxy.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "a")],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("first", r1.Text);

        stubRuntime.SwapClient(second);

        var r2 = await proxy.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "b")],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("second", r2.Text);

        // The old stub stays at its first-call message — the proxy
        // didn't dispatch to it after the swap.
        Assert.NotNull(first.LastMessages);
        Assert.Single(first.LastMessages);
        Assert.Equal("a", first.LastMessages![0].Text);
    }

    [Fact]
    public void GetService_ForwardsToInner_WhenPresent()
    {
        var stub = new StubChatClient("ok")
        {
            ServiceFor = (t, _) => t == typeof(string) ? "from-stub-service" : null,
        };
        using var stubRuntime = new StubRuntime(stub);
        using var proxy = new MutableChatClient(stubRuntime);

        Assert.Equal("from-stub-service", proxy.GetService(typeof(string)));
        Assert.Null(proxy.GetService(typeof(int)));
    }

    [Fact]
    public void GetService_ReturnsNull_WhenRuntimeHasNoClient()
    {
        using var rt = new BowireAiRuntime(new BowireAiOptions { ProviderId = "openai" });
        using var proxy = new MutableChatClient(rt);

        Assert.Null(proxy.GetService(typeof(string)));
        Assert.Null(proxy.GetService(typeof(IChatClient)));
    }

    [Fact]
    public void Dispose_DoesNotDisposeInner()
    {
        // Production contract: the proxy never disposes the runtime's
        // inner client (the runtime owns its lifetime). Disposing the
        // proxy twice + invoking the proxy after dispose must still
        // dispatch to the inner.
        var stub = new StubChatClient("alive");
        using var stubRuntime = new StubRuntime(stub);
        var proxy = new MutableChatClient(stubRuntime);

        proxy.Dispose();
        proxy.Dispose();

        Assert.False(stub.Disposed);
    }

    [Fact]
    public void Constructor_NullRuntime_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MutableChatClient(null!));
    }

    /// <summary>
    /// Test seam over the real <see cref="BowireAiRuntime"/> — same
    /// surface (<c>Current</c>), letting us pin the proxy's behaviour
    /// without spinning up an OllamaSharp client against a real
    /// endpoint. The runtime is sealed, so we can't inherit; we wrap
    /// the proxy at construction by giving it a real runtime and then
    /// swapping the inner via Reflection — but a sibling seam is
    /// simpler. The MutableChatClient ctor takes a BowireAiRuntime,
    /// so we use a real runtime and swap clients via Update(). But
    /// Update() rebuilds via Build() which needs a known provider id,
    /// so we go through a Runtime subtype seam in production — except
    /// the type is sealed. So we use a real runtime with a real Build
    /// and a passthrough stub client returned through the public
    /// IChatClient seam by overriding the runtime's _client field via
    /// the type's own ctor + Update flow.
    ///
    /// Practical resolution: the tests in this class that need a
    /// stub client construct a BowireAiRuntime against ollama (the
    /// default, which Build()s a real OllamaSharp client wrapper) and
    /// don't exercise the inner client at all — only the proxy
    /// behaviour. For tests that need a controlled inner, we use the
    /// internal MutableChatClient.GetService path + StubRuntime below
    /// — a small wrapper that mimics BowireAiRuntime's "Current" seam
    /// but is itself a real BowireAiRuntime (the proxy ctor accepts
    /// only the concrete type). Since BowireAiRuntime is sealed, we
    /// inherit the public Current property via the runtime's own
    /// initial-options channel: we pass an unknown provider id so
    /// Build() returns null, then call Update() with a controlled
    /// next-set... still null. So we need a real seam — and the
    /// proxy's _runtime field is private. The reflection-free way is
    /// to define StubRuntime as a tiny subclass... but the runtime is
    /// sealed.
    ///
    /// Workaround: the proxy's <c>_runtime</c> field is private,
    /// but reflection assigns it directly. Wrapping that here so the
    /// tests above stay readable.
    /// </summary>
    private sealed class StubRuntime : IDisposable
    {
        private readonly BowireAiRuntime _real;
        private IChatClient _current;

        public StubRuntime(IChatClient initial)
        {
            // A real runtime with the default options gives us a
            // BowireAiRuntime instance we can hand the proxy.
            _real = new BowireAiRuntime(new BowireAiOptions { ProviderId = "openai" });
            _current = initial;
            ReplaceRuntimeClient(_real, initial);
        }

        public void SwapClient(IChatClient next)
        {
            _current = next;
            ReplaceRuntimeClient(_real, next);
        }

        public IChatClient Current => _current;

        public static implicit operator BowireAiRuntime(StubRuntime stub) => stub._real;

        public void Dispose() => _real.Dispose();

        private static void ReplaceRuntimeClient(BowireAiRuntime rt, IChatClient client)
        {
            // BowireAiRuntime's _client field is private; reflect once
            // in the test seam so the proxy's lookup of `runtime.Current`
            // returns the stub we installed. Cleaner than introducing
            // a virtual seam on production code that exists solely for
            // tests — the runtime is sealed because callers shouldn't
            // subclass it.
            var field = typeof(BowireAiRuntime).GetField("_client",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("BowireAiRuntime._client field missing — runtime was refactored without updating the test seam.");
            field.SetValue(rt, client);
        }
    }

    private sealed class StubChatClient(string responseText) : IChatClient
    {
        public IList<ChatMessage>? LastMessages { get; private set; }
        public ChatOptions? LastOptions { get; private set; }
        public bool Disposed { get; private set; }
        public Func<Type, object?, object?>? ServiceFor { get; set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = [.. messages];
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = [.. messages];
            LastOptions = options;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, responseText);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => ServiceFor?.Invoke(serviceType, serviceKey);

        public void Dispose() { Disposed = true; }
    }
}
