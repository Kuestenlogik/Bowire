// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Ai.Tests;

/// <summary>
/// Pins the public AddBowireAi DI contract — argument-null guards,
/// configuration binding shape, the documented overlay precedence
/// (defaults → IConfiguration → configure callback → user-config
/// file), idempotency (re-registration is a no-op), and the
/// host-IChatClient-wins integration point that embedded hosts rely
/// on to route Bowire's chat calls through their own infrastructure.
/// </summary>
[Collection("BowireUserContext")]
public sealed class BowireAiServiceCollectionExtensionsTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _tempRoot;

    public BowireAiServiceCollectionExtensionsTests()
    {
        _originalStore = BowireUserContext.Current;
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"bowire-ai-di-test-{Guid.NewGuid():N}");
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
    public void AddBowireAi_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => BowireAiServiceCollectionExtensions.AddBowireAi(
                null!, new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void AddBowireAi_NullConfiguration_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddBowireAi(null!));
    }

    [Fact]
    public void AddBowireAi_RegistersOptions_FromConfigurationSection()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Ai:ProviderId"] = "lmstudio",
                ["Bowire:Ai:Endpoint"] = "http://localhost:1234",
                ["Bowire:Ai:Model"] = "qwen2.5:7b",
                ["Bowire:Ai:AutoDetectLocal"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddBowireAi(cfg);
        using var sp = services.BuildServiceProvider();

        var opts = sp.GetRequiredService<BowireAiOptions>();
        Assert.Equal("lmstudio", opts.ProviderId);
        Assert.Equal("http://localhost:1234", opts.Endpoint);
        Assert.Equal("qwen2.5:7b", opts.Model);
        Assert.False(opts.AutoDetectLocal);
    }

    [Fact]
    public void AddBowireAi_RegistersRuntime_AsSingleton()
    {
        var services = new ServiceCollection();
        services.AddBowireAi(new ConfigurationBuilder().Build());
        using var sp = services.BuildServiceProvider();

        var rt1 = sp.GetRequiredService<BowireAiRuntime>();
        var rt2 = sp.GetRequiredService<BowireAiRuntime>();
        Assert.Same(rt1, rt2);
    }

    [Fact]
    public void AddBowireAi_RegistersChatClient_AsSingleton()
    {
        var services = new ServiceCollection();
        services.AddBowireAi(new ConfigurationBuilder().Build());
        using var sp = services.BuildServiceProvider();

        var c1 = sp.GetRequiredService<IChatClient>();
        var c2 = sp.GetRequiredService<IChatClient>();
        Assert.Same(c1, c2);
    }

    [Fact]
    public void AddBowireAi_DefaultChatClient_IsMutableProxy()
    {
        // The default registration installs MutableChatClient so the
        // Settings UI can hot-swap providers without restarting the
        // host. Verified by name to avoid the internal-type assertion
        // failing if InternalsVisibleTo regresses.
        var services = new ServiceCollection();
        services.AddBowireAi(new ConfigurationBuilder().Build());
        using var sp = services.BuildServiceProvider();

        var client = sp.GetRequiredService<IChatClient>();
        Assert.Equal("MutableChatClient", client.GetType().Name);
    }

    [Fact]
    public void AddBowireAi_HostSuppliedChatClient_Wins()
    {
        // TryAdd contract: an embedded host that registered its own
        // IChatClient before AddBowireAi keeps owning the chat path.
        // The Settings UI still renders for picks-on-disk, but the
        // live IChatClient handed out by DI is the host's.
        using var hostClient = new NullChatClient();
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(hostClient);
        services.AddBowireAi(new ConfigurationBuilder().Build());
        using var sp = services.BuildServiceProvider();

        Assert.Same(hostClient, sp.GetRequiredService<IChatClient>());
    }

    [Fact]
    public void AddBowireAi_ConfigureCallback_OverridesConfiguration()
    {
        // Overlay precedence step 3: the configure callback fires after
        // the IConfiguration bind, so callback-supplied values win
        // unless the user-config file overrides them.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Ai:Model"] = "from-config:1b",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddBowireAi(cfg, o => o.Model = "from-callback:1b");
        using var sp = services.BuildServiceProvider();

        var opts = sp.GetRequiredService<BowireAiOptions>();
        Assert.Equal("from-callback:1b", opts.Model);
    }

    [Fact]
    public void AddBowireAi_UserConfigFile_OverridesCallbackAndConfiguration()
    {
        // Overlay precedence step 4 (top): the on-disk pick from the
        // Settings UI wins over every other layer. This is the
        // contract that lets a temporary --ai-model CLI flag pin a
        // workshop demo without silently destroying the user's
        // saved preference.
        BowireAiUserConfigStore.Save(new BowireAiOptions
        {
            ProviderId = "ollama",
            Endpoint = "http://localhost:11434",
            Model = "from-disk:7b",
            AutoDetectLocal = true,
        });

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Ai:Model"] = "from-config:1b",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddBowireAi(cfg, o => o.Model = "from-callback:1b");
        using var sp = services.BuildServiceProvider();

        var opts = sp.GetRequiredService<BowireAiOptions>();
        Assert.Equal("from-disk:7b", opts.Model);
    }

    [Fact]
    public void AddBowireAi_UserConfig_PartialFile_PreservesUnsetLowerLayers()
    {
        // Field-by-field overlay: when the persisted file leaves a
        // property blank (e.g. "ProviderId":"") the lower layers
        // (configure callback, IConfiguration, defaults) fill in.
        // Empty-string vs. null distinction matters because users
        // who half-fill the file shouldn't see unrelated fields snap
        // back to "ollama".
        var path = Path.Combine(_tempRoot, "ai-config.json");
        File.WriteAllText(path, """
            {
              "providerId": "",
              "endpoint": "",
              "model": "user-typed-this:7b",
              "autoDetectLocal": false
            }
            """);

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Ai:ProviderId"] = "lmstudio",
                ["Bowire:Ai:Endpoint"] = "http://localhost:1234",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddBowireAi(cfg);
        using var sp = services.BuildServiceProvider();

        var opts = sp.GetRequiredService<BowireAiOptions>();
        Assert.Equal("lmstudio", opts.ProviderId);            // from IConfiguration
        Assert.Equal("http://localhost:1234", opts.Endpoint); // from IConfiguration
        Assert.Equal("user-typed-this:7b", opts.Model);       // from disk
        Assert.False(opts.AutoDetectLocal);                   // from disk
    }

    [Fact]
    public void AddBowireAi_NoUserConfigFile_KeepsConfigurationValues()
    {
        // Absent file = no overlay; the binding layer wins. Pins the
        // path that distinguishes "user hasn't saved a pick yet" from
        // "user saved these defaults": the former leaves Configuration
        // intact, the latter overwrites with the persisted record.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Ai:Model"] = "config-only:1b",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddBowireAi(cfg);
        using var sp = services.BuildServiceProvider();

        var opts = sp.GetRequiredService<BowireAiOptions>();
        Assert.Equal("config-only:1b", opts.Model);
    }

    [Fact]
    public void AddBowireAi_Idempotent_ReturnsSameOptionsInstance()
    {
        // TryAddSingleton on second AddBowireAi: second call should
        // be a no-op so a host wiring AddBowireAi twice doesn't end
        // up with two different BowireAiOptions instances racing
        // each other.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Ai:Model"] = "first:1b",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddBowireAi(cfg);
        // Second call with different config — TryAdd means the
        // existing registration sticks; the second call's options
        // get discarded.
        services.AddBowireAi(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Ai:Model"] = "second:1b",
            }).Build());

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<BowireAiOptions>();
        Assert.Equal("first:1b", opts.Model);
    }

    [Fact]
    public void AddBowireAi_ReturnsTheSameServicesInstance_ForChaining()
    {
        var services = new ServiceCollection();
        var result = services.AddBowireAi(new ConfigurationBuilder().Build());
        Assert.Same(services, result);
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
            => AsyncEnumerable<ChatResponseUpdate>.Empty;
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static class AsyncEnumerable<T>
    {
        public static readonly IAsyncEnumerable<T> Empty = new EmptyImpl();
        private sealed class EmptyImpl : IAsyncEnumerable<T>
        {
            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new EmptyEnumerator();
            private sealed class EmptyEnumerator : IAsyncEnumerator<T>
            {
                public T Current => default!;
                public ValueTask DisposeAsync() => default;
                public ValueTask<bool> MoveNextAsync() => new(false);
            }
        }
    }
}
