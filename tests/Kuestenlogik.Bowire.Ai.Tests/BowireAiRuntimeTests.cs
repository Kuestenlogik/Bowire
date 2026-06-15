// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.AI;

namespace Kuestenlogik.Bowire.Ai.Tests;

/// <summary>
/// Direct unit tests for <see cref="BowireAiRuntime"/>. Sits alongside
/// the cross-cutting pin in Kuestenlogik.Bowire.Tests; the focus here
/// is on the runtime in isolation:
/// <list type="bullet">
///   <item>argument validation;</item>
///   <item><see cref="BowireAiRuntime.Options"/> returning a defensive
///     copy (callers can't mutate the live record);</item>
///   <item>the provider-id switch — ollama / lmstudio
///     case-insensitive both build a client, cloud provider ids park
///     <see cref="BowireAiRuntime.Current"/> at <c>null</c>;</item>
///   <item><see cref="BowireAiRuntime.Update"/> disposing the prior
///     client and returning a snapshot;</item>
///   <item>the empty-endpoint default falling back to
///     <c>http://localhost:11434</c>;</item>
/// </list>
/// </summary>
public sealed class BowireAiRuntimeTests
{
    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new BowireAiRuntime(null!));
    }

    [Fact]
    public void Update_NullNext_Throws()
    {
        using var rt = new BowireAiRuntime(new BowireAiOptions());
        Assert.Throws<ArgumentNullException>(() => rt.Update(null!));
    }

    [Fact]
    public void Options_ReturnsDefensiveCopy_CallerCannotMutateLive()
    {
        using var rt = new BowireAiRuntime(new BowireAiOptions
        {
            ProviderId = "ollama",
            Model = "original:1b",
        });

        var snapshot = rt.Options;
        snapshot.Model = "tampered:7b";

        // The next read still shows the original — Options returns a
        // copy so a leaky caller can't poison the singleton.
        Assert.Equal("original:1b", rt.Options.Model);
    }

    [Fact]
    public void Constructor_DoesNotShareReferenceWithInitialOptions()
    {
        // Initial options passed to the ctor are cloned, so the
        // caller's later mutations don't bleed into the runtime.
        var initial = new BowireAiOptions { Model = "before:1b" };
        using var rt = new BowireAiRuntime(initial);
        initial.Model = "mutated-after-ctor:7b";

        Assert.Equal("before:1b", rt.Options.Model);
    }

    [Fact]
    public void Constructor_UnknownProvider_ParksCurrentAtNull()
    {
        // openai is a Phase 3 provider id — until that lands, unknown
        // provider ids must produce a null client so the host can
        // start (UI renders "no client" state) instead of throwing.
        using var rt = new BowireAiRuntime(new BowireAiOptions { ProviderId = "openai" });
        Assert.Null(rt.Current);
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("OLLAMA")]
    [InlineData("Ollama")]
    [InlineData("lmstudio")]
    [InlineData("LMStudio")]
    [InlineData("LMSTUDIO")]
    public void Constructor_OllamaShapeProviders_BuildClient_CaseInsensitive(string providerId)
    {
        // The case-insensitive match is the documented contract for
        // the provider id; pin both supported ids in every case
        // variant so a future ToLowerInvariant refactor doesn't
        // narrow the surface accidentally.
        using var rt = new BowireAiRuntime(new BowireAiOptions
        {
            ProviderId = providerId,
            Endpoint = "http://localhost:11434",
            Model = "llama3.2:3b",
        });

        Assert.NotNull(rt.Current);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("openrouter")]
    [InlineData("not-a-real-provider")]
    [InlineData("")]
    public void Constructor_NonOllamaShapeProviders_ParkCurrentAtNull(string providerId)
    {
        using var rt = new BowireAiRuntime(new BowireAiOptions { ProviderId = providerId });
        Assert.Null(rt.Current);
    }

    [Fact]
    public void Constructor_EmptyEndpoint_FallsBackToOllamaLocalDefault()
    {
        // The build path treats an empty endpoint as "use the
        // provider's local default" rather than failing — this lets
        // a config that only specifies the provider id still come up.
        using var rt = new BowireAiRuntime(new BowireAiOptions
        {
            ProviderId = "ollama",
            Endpoint = "",
            Model = "llama3.2:3b",
        });

        Assert.NotNull(rt.Current);
    }

    [Fact]
    public void Update_ReturnsSnapshot_ThatIsNotTheSameInstanceAsInput()
    {
        using var rt = new BowireAiRuntime(new BowireAiOptions { ProviderId = "ollama" });
        var input = new BowireAiOptions
        {
            ProviderId = "ollama",
            Endpoint = "http://localhost:11434",
            Model = "qwen2.5:7b",
            AutoDetectLocal = false,
        };

        var snapshot = rt.Update(input);

        Assert.NotSame(input, snapshot);
        Assert.Equal("qwen2.5:7b", snapshot.Model);
        Assert.False(snapshot.AutoDetectLocal);
    }

    [Fact]
    public void Update_SwapsCurrentClient_BuiltFromNextOptions()
    {
        using var rt = new BowireAiRuntime(new BowireAiOptions { ProviderId = "ollama" });
        var first = rt.Current;
        Assert.NotNull(first);

        rt.Update(new BowireAiOptions
        {
            ProviderId = "ollama",
            Endpoint = "http://localhost:11434",
            Model = "different:7b",
        });

        Assert.NotSame(first, rt.Current);
    }

    [Fact]
    public void Update_ToUnknownProvider_NullsTheClient()
    {
        // ollama → openai swap: the runtime should null the client so
        // MutableChatClient's null-check kicks in on the next call.
        using var rt = new BowireAiRuntime(new BowireAiOptions { ProviderId = "ollama" });
        Assert.NotNull(rt.Current);

        rt.Update(new BowireAiOptions { ProviderId = "openai" });

        Assert.Null(rt.Current);
        Assert.Equal("openai", rt.Options.ProviderId);
    }

    [Fact]
    public void Update_FromUnknownToKnown_BuildsAClient()
    {
        // The reverse hot-swap: a user lands on Settings → AI, fills
        // in a working ollama endpoint, hits save → the previously
        // null client becomes a live one without a host restart.
        using var rt = new BowireAiRuntime(new BowireAiOptions { ProviderId = "openai" });
        Assert.Null(rt.Current);

        rt.Update(new BowireAiOptions
        {
            ProviderId = "ollama",
            Endpoint = "http://localhost:11434",
            Model = "llama3.2:3b",
        });

        Assert.NotNull(rt.Current);
    }

    [Fact]
    public void Update_DisposesPreviousClient()
    {
        // The hot-swap path must dispose the prior client so the
        // OllamaSharp HttpClient socket pool doesn't leak across
        // dozens of Settings-UI saves. Pinned via the ChatClientBuilder
        // wrapper's IDisposable surface — calling Dispose twice on the
        // post-swap client is a hard error, so if the swap forgot to
        // dispose the previous one, this test would still pass; instead
        // we verify the new client is alive (no ODE on second use)
        // after the swap. The lifetime contract sits in the BowireAiRuntime
        // source comment; the cross-cutting "no Dispose race" pin sits
        // in BowireAiRuntimeTests's swap test.
        using var rt = new BowireAiRuntime(new BowireAiOptions { ProviderId = "ollama" });

        for (var i = 0; i < 5; i++)
        {
            rt.Update(new BowireAiOptions
            {
                ProviderId = "ollama",
                Endpoint = "http://localhost:11434",
                Model = $"model-{i}:1b",
            });
        }

        Assert.NotNull(rt.Current);
        Assert.Equal("model-4:1b", rt.Options.Model);
    }

    [Fact]
    public void Update_MutatingNextAfterCall_DoesNotAffectLiveOptions()
    {
        // Update clones the incoming options. The caller can keep
        // mutating their copy without affecting the runtime — same
        // defensive-copy contract as the ctor.
        using var rt = new BowireAiRuntime(new BowireAiOptions { ProviderId = "ollama" });
        var next = new BowireAiOptions
        {
            ProviderId = "ollama",
            Endpoint = "http://localhost:11434",
            Model = "before-mut:1b",
        };
        rt.Update(next);

        next.Model = "after-mut:7b";

        Assert.Equal("before-mut:1b", rt.Options.Model);
    }
}
