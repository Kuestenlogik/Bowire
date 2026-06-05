// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Ai;
using Kuestenlogik.Bowire.Auth;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the #63 Settings-UI substrate: <see cref="BowireAiRuntime"/>
/// hot-swap semantics, <see cref="BowireAiUserConfigStore"/> round-trip,
/// and the AddBowireAi overlay precedence (defaults → IConfiguration →
/// configure callback → user-config file).
/// </summary>
public sealed class BowireAiRuntimeTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _tempRoot;

    public BowireAiRuntimeTests()
    {
        _originalStore = BowireUserContext.Current;
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"bowire-ai-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        BowireUserContext.Current = new TempUserStore(_tempRoot);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _originalStore;
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Runtime_Update_Replaces_Options_And_Client()
    {
        var rt = new BowireAiRuntime(new BowireAiOptions
        {
            ProviderId = "ollama",
            Endpoint = "http://localhost:11434",
            Model = "llama3.2:3b",
        });

        var before = rt.Current;
        Assert.NotNull(before);
        Assert.Equal("llama3.2:3b", rt.Options.Model);

        var applied = rt.Update(new BowireAiOptions
        {
            ProviderId = "ollama",
            Endpoint = "http://localhost:11434",
            Model = "qwen2.5:7b",
            AutoDetectLocal = false,
        });

        Assert.Equal("qwen2.5:7b", applied.Model);
        Assert.False(applied.AutoDetectLocal);
        Assert.Equal("qwen2.5:7b", rt.Options.Model);
        Assert.NotSame(before, rt.Current);
    }

    [Fact]
    public void Runtime_Unknown_Provider_Yields_Null_Client_Not_Throw()
    {
        // Phase 3 will widen the switch — until then a cloud provider
        // id should park Current at null instead of throwing during
        // host startup, so the workbench can render its "no client" UI
        // and the user can fix it via Settings → AI.
        var rt = new BowireAiRuntime(new BowireAiOptions { ProviderId = "openai" });
        Assert.Null(rt.Current);
        Assert.Equal("openai", rt.Options.ProviderId);
    }

    [Fact]
    public void Mutable_Chat_Client_Throws_Clear_Error_When_Runtime_Has_No_Client()
    {
        var rt = new BowireAiRuntime(new BowireAiOptions { ProviderId = "openai" });
        using var proxy = new MutableChatClient(rt);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            proxy.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: TestContext.Current.CancellationToken).GetAwaiter().GetResult());
        Assert.Contains("Bowire AI", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UserConfigStore_Roundtrips_Through_IBowireUserStore()
    {
        var saved = new BowireAiOptions
        {
            ProviderId = "lmstudio",
            Endpoint = "http://localhost:1234",
            Model = "mistral-7b-instruct",
            AutoDetectLocal = false,
        };
        BowireAiUserConfigStore.Save(saved);

        var loaded = BowireAiUserConfigStore.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal("lmstudio", loaded!.ProviderId);
        Assert.Equal("http://localhost:1234", loaded.Endpoint);
        Assert.Equal("mistral-7b-instruct", loaded.Model);
        Assert.False(loaded.AutoDetectLocal);
    }

    [Fact]
    public void UserConfigStore_Missing_File_Returns_Null()
    {
        Assert.Null(BowireAiUserConfigStore.TryLoad());
    }

    [Fact]
    public void UserConfigStore_Corrupted_File_Returns_Null_Without_Throwing()
    {
        // A corrupted file shouldn't take the workbench down on
        // startup; the loader is expected to swallow + fall back.
        var path = BowireUserContext.GetUserPath("ai-config.json");
        File.WriteAllText(path, "{not valid json");
        Assert.Null(BowireAiUserConfigStore.TryLoad());
    }

    [Fact]
    public void AddBowireAi_Overlays_UserConfig_Over_Configuration()
    {
        // Configuration says ollama / qwen — user-config file overrides
        // model. The store wins because it represents an explicit
        // Settings-UI pick.
        BowireAiUserConfigStore.Save(new BowireAiOptions
        {
            ProviderId = "ollama",
            Endpoint = "http://localhost:11434",
            Model = "user-picked-model:latest",
            AutoDetectLocal = true,
        });

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Ai:ProviderId"] = "ollama",
                ["Bowire:Ai:Model"] = "qwen2.5:7b",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddBowireAi(cfg);
        using var sp = services.BuildServiceProvider();

        var opts = sp.GetRequiredService<BowireAiOptions>();
        Assert.Equal("user-picked-model:latest", opts.Model);
    }

    [Fact]
    public void AddBowireAi_Respects_Host_Supplied_IChatClient()
    {
        using var hostClient = new NullChatClient();
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(hostClient);
        services.AddBowireAi(new ConfigurationBuilder().Build());
        using var sp = services.BuildServiceProvider();

        Assert.Same(hostClient, sp.GetRequiredService<IChatClient>());
    }

    [Fact]
    public void AddBowireAi_Without_Host_Client_Uses_MutableChatClient_Proxy()
    {
        var services = new ServiceCollection();
        services.AddBowireAi(new ConfigurationBuilder().Build());
        using var sp = services.BuildServiceProvider();

        var client = sp.GetRequiredService<IChatClient>();
        // Internal type — exposed via InternalsVisibleTo on
        // Kuestenlogik.Bowire.Ai. The proxy is what lets the Settings
        // UI hot-swap providers without restarting the host.
        Assert.Equal("MutableChatClient", client.GetType().Name);
    }

    private sealed class TempUserStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }

    private sealed class NullChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static class AsyncEnumerable
    {
        public static IAsyncEnumerable<T> Empty<T>() => EmptyImpl<T>.Instance;
        private sealed class EmptyImpl<T> : IAsyncEnumerable<T>
        {
            public static readonly EmptyImpl<T> Instance = new();
            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
                => new EmptyEnumerator();
            private sealed class EmptyEnumerator : IAsyncEnumerator<T>
            {
                public T Current => default!;
                public ValueTask DisposeAsync() => default;
                public ValueTask<bool> MoveNextAsync() => new(false);
            }
        }
    }
}
