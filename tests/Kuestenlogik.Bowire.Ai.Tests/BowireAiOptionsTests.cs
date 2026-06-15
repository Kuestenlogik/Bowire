// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Ai.Tests;

/// <summary>
/// Pins the privacy-first default values on <see cref="BowireAiOptions"/>
/// and the <c>Bowire:Ai</c> configuration binding shape. The defaults
/// matter — they're the documented "nothing leaves the machine"
/// stance the package ADR commits to. A silent default-flip is a
/// behaviour change for every existing install.
/// </summary>
public sealed class BowireAiOptionsTests
{
    [Fact]
    public void Defaults_Match_PrivacyFirst_OllamaLocalhost()
    {
        var opts = new BowireAiOptions();

        Assert.Equal("ollama", opts.ProviderId);
        Assert.Equal("http://localhost:11434", opts.Endpoint);
        Assert.Equal("llama3.2:3b", opts.Model);
        Assert.True(opts.AutoDetectLocal);
    }

    [Fact]
    public void Bowire_Ai_Section_Binds_All_Fields()
    {
        // The CLI's --ai-provider / --ai-endpoint / --ai-model flags
        // feed the same Bowire:Ai keys via the in-memory configuration
        // overlay, so the binding shape is part of the public contract.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Ai:ProviderId"] = "lmstudio",
                ["Bowire:Ai:Endpoint"] = "http://localhost:1234",
                ["Bowire:Ai:Model"] = "qwen2.5:7b",
                ["Bowire:Ai:AutoDetectLocal"] = "false",
            })
            .Build();

        var opts = new BowireAiOptions();
        cfg.GetSection("Bowire:Ai").Bind(opts);

        Assert.Equal("lmstudio", opts.ProviderId);
        Assert.Equal("http://localhost:1234", opts.Endpoint);
        Assert.Equal("qwen2.5:7b", opts.Model);
        Assert.False(opts.AutoDetectLocal);
    }

    [Fact]
    public void Bowire_Ai_PartialSection_Preserves_Unbound_Defaults()
    {
        // Partial overrides (Settings UI sends a patch) shouldn't blow
        // away fields the user didn't touch — Bind merges over the
        // existing instance, so unset keys stay at their default value.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Ai:Model"] = "tiny-model:1b",
            })
            .Build();

        var opts = new BowireAiOptions();
        cfg.GetSection("Bowire:Ai").Bind(opts);

        Assert.Equal("tiny-model:1b", opts.Model);
        Assert.Equal("ollama", opts.ProviderId);
        Assert.Equal("http://localhost:11434", opts.Endpoint);
        Assert.True(opts.AutoDetectLocal);
    }

    [Fact]
    public void Empty_Configuration_Leaves_Defaults_Intact()
    {
        var cfg = new ConfigurationBuilder().Build();
        var opts = new BowireAiOptions();
        cfg.GetSection("Bowire:Ai").Bind(opts);

        Assert.Equal("ollama", opts.ProviderId);
        Assert.Equal("llama3.2:3b", opts.Model);
    }
}
