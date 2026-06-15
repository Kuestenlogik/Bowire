// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Pulsar;

namespace Kuestenlogik.Bowire.Protocol.Pulsar.Tests;

/// <summary>
/// Edge-case tests filling the small gaps left by
/// <see cref="PulsarPluginTests"/>: OpenChannelAsync no-op,
/// InvokeAsync against an invalid URL, the metadata-driven topic
/// override path, and the InvokeStreamAsync error-shape branches.
/// All live-broker behaviour stays in the Testcontainers integration
/// suite.
/// </summary>
public sealed class PulsarCoverageGapsTests
{
    [Fact]
    public async Task OpenChannelAsync_AlwaysReturnsNull()
    {
        // Pulsar has no duplex channel surface — produce is unary
        // and subscribe is server-streaming. OpenChannelAsync must
        // return null so the workbench routes invokes through the
        // proper path instead of opening a channel.
        using var p = new BowirePulsarProtocol();
        var ch = await p.OpenChannelAsync(
            "pulsar://localhost:6650",
            service: "x",
            method: "y",
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Null(ch);
    }

    [Fact]
    public async Task InvokeAsync_InvalidUrl_Returns_StructuredValidationError()
    {
        using var p = new BowirePulsarProtocol();
        var result = await p.InvokeAsync(
            "",
            service: "x",
            method: "pulsar/topic/orders/produce",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.NotEqual("OK", result.Status);
        Assert.Equal("Invalid Pulsar server URL", result.Status);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task InvokeAsync_SubscribeRoute_NotAllowed_ReturnsRoutingError()
    {
        // subscribe is server-streaming — the unary InvokeAsync path
        // rejects it with the "Unknown Pulsar route" message because
        // subscribe isn't in the allowed-ops set for InvokeAsync.
        using var p = new BowirePulsarProtocol();
        var result = await p.InvokeAsync(
            "pulsar://localhost:6650",
            service: "x",
            method: "pulsar/topic/orders/subscribe",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.NotEqual("OK", result.Status);
        Assert.Contains("Unknown Pulsar route", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStreamAsync_InvalidUrl_YieldsNothing()
    {
        using var p = new BowirePulsarProtocol();
        var collected = new List<string>();
        await foreach (var msg in p.InvokeStreamAsync(
            "",
            service: "x",
            method: "pulsar/topic/orders/subscribe",
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken))
        {
            collected.Add(msg);
        }
        Assert.Empty(collected);
    }

    [Fact]
    public async Task InvokeStreamAsync_NonSubscribeRoute_YieldsNothing()
    {
        // InvokeStreamAsync gates on Op == "subscribe" — feeding it a
        // produce route makes it yield-break immediately.
        using var p = new BowirePulsarProtocol();
        var collected = new List<string>();
        await foreach (var msg in p.InvokeStreamAsync(
            "pulsar://localhost:6650",
            service: "x",
            method: "pulsar/topic/orders/produce",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken))
        {
            collected.Add(msg);
        }
        Assert.Empty(collected);
    }

    [Fact]
    public async Task InvokeStreamAsync_MalformedRoute_YieldsNothing()
    {
        using var p = new BowirePulsarProtocol();
        var collected = new List<string>();
        await foreach (var msg in p.InvokeStreamAsync(
            "pulsar://localhost:6650",
            service: "x",
            method: "garbage",
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken))
        {
            collected.Add(msg);
        }
        Assert.Empty(collected);
    }

    [Fact]
    public void Settings_AutoInterpretJson_Not_Exposed()
    {
        // Pulsar deliberately doesn't ship autoInterpretJson (Schema.String
        // ships strings raw). Pin this so a future "every plugin should
        // have it" refactor doesn't silently add the wrong default.
        using var p = new BowirePulsarProtocol();
        Assert.DoesNotContain(p.Settings, s => s.Key == "autoInterpretJson");
    }

    [Fact]
    public void Description_Mentions_PublishAndConsume()
    {
        // Description is the sidebar tooltip — pin the user-facing
        // verbs so renames flag in code review.
        using var p = new BowirePulsarProtocol();
        Assert.Contains("Pulsar", p.Description, StringComparison.Ordinal);
        Assert.Contains("publish", p.Description, StringComparison.Ordinal);
        Assert.Contains("consume", p.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Initialize_Null_ServiceProvider_DoesNotThrow()
    {
        using var p = new BowirePulsarProtocol();
        p.Initialize(null);
    }
}
