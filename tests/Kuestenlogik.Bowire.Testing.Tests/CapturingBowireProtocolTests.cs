// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Testing;

namespace Kuestenlogik.Bowire.Testing.Tests;

/// <summary>
/// Behaviour tests for <see cref="CapturingBowireProtocol"/>: the
/// IBowireProtocol identity contract (Id / Name / IconSvg), the
/// per-invocation capture surface (Last* fields + InvokeCount), the
/// defensive-copy semantics on the captured collections, the fixed
/// InvokeResult shape returned by InvokeAsync, and the no-op default
/// shape of DiscoverAsync / InvokeStreamAsync / OpenChannelAsync.
/// </summary>
public sealed class CapturingBowireProtocolTests
{
    // CA1861 — hoist literal arrays out of the assertion call sites
    // so the analyser doesn't see a fresh `new[] { ... }` per call.
    private static readonly string[] TwoMessages    = ["{\"k\":1}", "{\"k\":2}"];
    private static readonly string[] SecondCallBody = ["b"];
    private static readonly string[] FirstMessage   = ["first"];

    // ─── Identity contract ────────────────────────────────────────

    [Fact]
    public void Id_ReflectsConstructorArgument()
    {
        var sut = new CapturingBowireProtocol("kafka");
        Assert.Equal("kafka", sut.Id);
    }

    [Fact]
    public void Name_PrefixesIdWithCapturing()
    {
        // Name is a convention pin: upstream UI/log assertions key on
        // the "Capturing " prefix to distinguish the fixture from the
        // real plugin in mixed-registry tests.
        var sut = new CapturingBowireProtocol("nats");
        Assert.Equal("Capturing nats", sut.Name);
    }

    [Fact]
    public void IconSvg_DefaultsToPlaceholderSvgWhenNotProvided()
    {
        var sut = new CapturingBowireProtocol("amqp");
        Assert.Equal("<svg/>", sut.IconSvg);
    }

    [Fact]
    public void IconSvg_ReflectsConstructorArgumentWhenProvided()
    {
        var sut = new CapturingBowireProtocol("amqp", "<svg id=\"x\"/>");
        Assert.Equal("<svg id=\"x\"/>", sut.IconSvg);
    }

    [Fact]
    public void IconSvg_NullArgument_FallsBackToPlaceholder()
    {
        // Explicit null is equivalent to "not provided" — the
        // null-coalesce branch in the primary constructor is the one
        // upstream resolver tests rely on when they don't care about
        // the icon.
        var sut = new CapturingBowireProtocol("any", iconSvg: null);
        Assert.Equal("<svg/>", sut.IconSvg);
    }

    // ─── Initial state ────────────────────────────────────────────

    [Fact]
    public void FreshInstance_HasNoCapturedInvocation()
    {
        var sut = new CapturingBowireProtocol("grpc");
        Assert.Null(sut.LastServerUrl);
        Assert.Null(sut.LastService);
        Assert.Null(sut.LastMethod);
        Assert.Null(sut.LastJsonMessages);
        Assert.Null(sut.LastMetadata);
        Assert.Equal(0, sut.InvokeCount);
    }

    // ─── InvokeAsync capture surface ──────────────────────────────

    [Fact]
    public async Task InvokeAsync_RecordsArgumentsOnLastFields()
    {
        var sut = new CapturingBowireProtocol("kafka");
        var messages = new List<string> { "{\"k\":1}", "{\"k\":2}" };
        var metadata = new Dictionary<string, string> { ["x-key"] = "v" };

        await sut.InvokeAsync(
            serverUrl: "kafka://broker:9092",
            service: "orders",
            method: "Place",
            jsonMessages: messages,
            showInternalServices: false,
            metadata: metadata,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("kafka://broker:9092", sut.LastServerUrl);
        Assert.Equal("orders",              sut.LastService);
        Assert.Equal("Place",               sut.LastMethod);
        Assert.NotNull(sut.LastJsonMessages);
        Assert.Equal(TwoMessages, sut.LastJsonMessages!);
        Assert.NotNull(sut.LastMetadata);
        Assert.Equal("v", sut.LastMetadata!["x-key"]);
    }

    [Fact]
    public async Task InvokeAsync_IncrementsInvokeCountByOnePerCall()
    {
        var sut = new CapturingBowireProtocol("nats");
        var ct = TestContext.Current.CancellationToken;

        await sut.InvokeAsync("u1", "s", "m", new List<string>(), false, ct: ct);
        Assert.Equal(1, sut.InvokeCount);

        await sut.InvokeAsync("u2", "s", "m", new List<string>(), false, ct: ct);
        Assert.Equal(2, sut.InvokeCount);

        await sut.InvokeAsync("u3", "s", "m", new List<string>(), false, ct: ct);
        Assert.Equal(3, sut.InvokeCount);
    }

    [Fact]
    public async Task InvokeAsync_SecondCall_OverwritesLastFieldsWithMostRecentArgs()
    {
        // "Last*" is a single-slot capture, not a history buffer. The
        // contract this pins matters: tests that assert on Last* after
        // a multi-call sequence must see the *final* call's args, not
        // the first.
        var sut = new CapturingBowireProtocol("p");
        var ct = TestContext.Current.CancellationToken;

        await sut.InvokeAsync("u1", "s1", "m1", new List<string> { "a" }, false, ct: ct);
        await sut.InvokeAsync("u2", "s2", "m2", new List<string> { "b" }, true,
            new Dictionary<string, string> { ["h"] = "1" }, ct: ct);

        Assert.Equal("u2", sut.LastServerUrl);
        Assert.Equal("s2", sut.LastService);
        Assert.Equal("m2", sut.LastMethod);
        Assert.Equal(SecondCallBody, sut.LastJsonMessages!);
        Assert.Equal("1", sut.LastMetadata!["h"]);
    }

    [Fact]
    public async Task InvokeAsync_NullMetadata_LeavesLastMetadataNull()
    {
        // The defensive copy branch must distinguish null (no
        // metadata) from empty dictionary (caller-supplied empty
        // metadata). Tests asserting on protocol-level header
        // presence rely on this: a null-vs-empty mix-up would silently
        // turn "no headers were sent" assertions into false greens.
        var sut = new CapturingBowireProtocol("p");

        await sut.InvokeAsync("u", "s", "m", new List<string>(), false,
            metadata: null, ct: TestContext.Current.CancellationToken);

        Assert.Null(sut.LastMetadata);
    }

    [Fact]
    public async Task InvokeAsync_EmptyMetadata_LeavesLastMetadataNonNullAndEmpty()
    {
        var sut = new CapturingBowireProtocol("p");

        await sut.InvokeAsync("u", "s", "m", new List<string>(), false,
            metadata: new Dictionary<string, string>(),
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(sut.LastMetadata);
        Assert.Empty(sut.LastMetadata!);
    }

    [Fact]
    public async Task InvokeAsync_DefensivelyCopiesJsonMessages()
    {
        // The contract is "the test sees what the SUT *sent*, not what
        // the caller's list looks like at assertion time." The fixture
        // achieves this by copying the list at capture time. Mutating
        // the caller's list afterwards must not leak into Last*.
        var sut = new CapturingBowireProtocol("p");
        var messages = new List<string> { "first" };

        await sut.InvokeAsync("u", "s", "m", messages, false,
            ct: TestContext.Current.CancellationToken);
        messages.Add("late-add");
        messages[0] = "mutated";

        Assert.Equal(FirstMessage, sut.LastJsonMessages!);
    }

    [Fact]
    public async Task InvokeAsync_DefensivelyCopiesMetadata()
    {
        var sut = new CapturingBowireProtocol("p");
        var metadata = new Dictionary<string, string> { ["k"] = "v" };

        await sut.InvokeAsync("u", "s", "m", new List<string>(), false, metadata,
            ct: TestContext.Current.CancellationToken);
        metadata["k"] = "mutated";
        metadata["new"] = "added";

        Assert.Equal("v", sut.LastMetadata!["k"]);
        Assert.False(sut.LastMetadata.ContainsKey("new"));
    }

    // ─── InvokeAsync return shape ─────────────────────────────────

    [Fact]
    public async Task InvokeAsync_ReturnsCannedOkInvokeResult()
    {
        // The fixture's job is to capture inputs — the response is a
        // fixed "{}" + OK so upstream tests can focus on what was
        // *sent*, not the shape of the reply. We pin the canned shape
        // here so a future "let's add randomness" tweak surfaces in
        // CI rather than as drift in dependent test suites.
        var sut = new CapturingBowireProtocol("p");

        var result = await sut.InvokeAsync("u", "s", "m", new List<string>(), false,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("{}",  result.Response);
        Assert.Equal(1,     result.DurationMs);
        Assert.Equal("OK",  result.Status);
        Assert.NotNull(result.Metadata);
        Assert.Empty(result.Metadata);
        Assert.Null(result.ResponseBinary);
    }

    [Fact]
    public async Task InvokeAsync_EachCall_ReturnsFreshMetadataDictionary()
    {
        // Two calls must not share the response Metadata reference —
        // a caller that mutates the returned dictionary on call 1
        // would otherwise pollute call 2's view.
        var sut = new CapturingBowireProtocol("p");
        var ct = TestContext.Current.CancellationToken;

        var r1 = await sut.InvokeAsync("u", "s", "m", new List<string>(), false, ct: ct);
        var r2 = await sut.InvokeAsync("u", "s", "m", new List<string>(), false, ct: ct);

        Assert.NotSame(r1.Metadata, r2.Metadata);
    }

    // ─── DiscoverAsync default ────────────────────────────────────

    [Fact]
    public async Task DiscoverAsync_ReturnsEmptyList()
    {
        var sut = new CapturingBowireProtocol("p");

        var services = await sut.DiscoverAsync("u", showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(services);
        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_DoesNotIncrementInvokeCount()
    {
        // InvokeCount tracks InvokeAsync exclusively. Discover /
        // stream / channel paths must stay off that counter so
        // "InvokeAsync was called exactly once" assertions in upstream
        // tests stay decoupled from incidental discovery traffic.
        var sut = new CapturingBowireProtocol("p");

        await sut.DiscoverAsync("u", false, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, sut.InvokeCount);
    }

    // ─── InvokeStreamAsync default ────────────────────────────────

    [Fact]
    public async Task InvokeStreamAsync_YieldsNoItems()
    {
        var sut = new CapturingBowireProtocol("p");

        var collected = new List<string>();
        await foreach (var item in sut.InvokeStreamAsync(
            "u", "s", "m", new List<string>(), false,
            ct: TestContext.Current.CancellationToken))
        {
            collected.Add(item);
        }

        Assert.Empty(collected);
    }

    [Fact]
    public async Task InvokeStreamAsync_DoesNotMutateCaptureSurface()
    {
        // The streaming default is a no-op for this fixture: it
        // doesn't pretend to capture stream args, so Last* and
        // InvokeCount must stay untouched after iterating it.
        var sut = new CapturingBowireProtocol("p");

        await foreach (var _ in sut.InvokeStreamAsync(
            "u", "s", "m", new List<string> { "x" }, false,
            new Dictionary<string, string> { ["h"] = "1" },
            ct: TestContext.Current.CancellationToken))
        {
            // empty
        }

        Assert.Null(sut.LastServerUrl);
        Assert.Null(sut.LastService);
        Assert.Null(sut.LastMethod);
        Assert.Null(sut.LastJsonMessages);
        Assert.Null(sut.LastMetadata);
        Assert.Equal(0, sut.InvokeCount);
    }

    // ─── OpenChannelAsync default ─────────────────────────────────

    [Fact]
    public async Task OpenChannelAsync_ReturnsNull()
    {
        // The fixture has no interactive channel — duplex resolvers
        // that exercise the channel path are expected to substitute a
        // dedicated fake. Returning null here is the deliberate
        // "this fixture doesn't speak duplex" signal.
        var sut = new CapturingBowireProtocol("p");

        var channel = await sut.OpenChannelAsync("u", "s", "m", false,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(channel);
    }

    [Fact]
    public async Task OpenChannelAsync_WithMetadata_ReturnsNullAndDoesNotMutateCaptureSurface()
    {
        var sut = new CapturingBowireProtocol("p");

        var channel = await sut.OpenChannelAsync(
            "u", "s", "m", false,
            new Dictionary<string, string> { ["h"] = "1" },
            ct: TestContext.Current.CancellationToken);

        Assert.Null(channel);
        Assert.Null(sut.LastServerUrl);
        Assert.Equal(0, sut.InvokeCount);
    }

    // ─── Cancellation pass-through smoke ──────────────────────────

    [Fact]
    public async Task InvokeAsync_HonoursACancellableSignatureWithoutThrowingOnUncancelledToken()
    {
        // We're not asserting that the fixture actually throws on
        // cancellation (it doesn't — the body is synchronous capture
        // + return). We're pinning that the CT-overload signature
        // matches the interface and an uncancelled token flows
        // through cleanly. Catches accidental signature drift.
        var sut = new CapturingBowireProtocol("p");
        using var cts = new CancellationTokenSource();

        var result = await sut.InvokeAsync(
            "u", "s", "m", new List<string>(), false, metadata: null, ct: cts.Token);

        Assert.Equal("OK", result.Status);
        Assert.Equal(1,    sut.InvokeCount);
    }

    [Fact]
    public void Implements_IBowireProtocol_Contract()
    {
        // Belt-and-braces: the upstream resolver tests bind to
        // IBowireProtocol, not the concrete fixture type. If a future
        // tweak accidentally drops the interface declaration, every
        // consumer would break in a confusing way at registration
        // time — this test surfaces it as a single targeted failure.
        var sut = new CapturingBowireProtocol("p");
        Assert.IsAssignableFrom<IBowireProtocol>(sut);
    }
}
