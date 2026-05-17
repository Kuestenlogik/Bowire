// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Semantics;
using Kuestenlogik.Bowire.Semantics.Detectors;

namespace Kuestenlogik.Bowire.Tests.Semantics;

/// <summary>
/// Coverage for <see cref="FrameProbingMiddleware"/> — the no-op
/// safety net around frame-prober invocation in the SSE / streaming
/// path. Verifies the three short-circuit guards (null prober, empty
/// frame, malformed JSON) and the happy-path call into
/// <see cref="IFrameProber.ObserveFrame"/>.
/// </summary>
public sealed class FrameProbingMiddlewareTests
{
    private sealed class CountingProber : IFrameProber
    {
        public int CallCount { get; private set; }
        public string? LastService { get; private set; }
        public string? LastMethod { get; private set; }
        public string? LastMessageType { get; private set; }

        public void ObserveFrame(in DetectionContext ctx)
        {
            CallCount++;
            LastService = ctx.ServiceId;
            LastMethod = ctx.MethodId;
            LastMessageType = ctx.MessageType;
        }
    }

    [Fact]
    public void Observe_NullProber_NoOp()
    {
        FrameProbingMiddleware.Observe(prober: null, "s", "m", "{\"x\":1}");
        // No throw, no observable side-effect.
    }

    [Fact]
    public void Observe_EmptyFrame_NoOp()
    {
        var prober = new CountingProber();
        FrameProbingMiddleware.Observe(prober, "s", "m", "");
        FrameProbingMiddleware.Observe(prober, "s", "m", null);
        Assert.Equal(0, prober.CallCount);
    }

    [Fact]
    public void Observe_MalformedJson_NoOp_NoThrow()
    {
        var prober = new CountingProber();
        FrameProbingMiddleware.Observe(prober, "s", "m", "{this is not json");
        Assert.Equal(0, prober.CallCount);
    }

    [Fact]
    public void Observe_ValidJson_InvokesProberWithWildcardMessageType()
    {
        var prober = new CountingProber();
        FrameProbingMiddleware.Observe(prober, "harbor.HarborService", "WatchCrane", "{\"position\":{\"lat\":54.1}}");
        Assert.Equal(1, prober.CallCount);
        Assert.Equal("harbor.HarborService", prober.LastService);
        Assert.Equal("WatchCrane", prober.LastMethod);
        Assert.Equal(AnnotationKey.Wildcard, prober.LastMessageType);
    }

    [Fact]
    public void Observe_ProberThrows_ExceptionPropagates()
    {
        // The middleware doesn't swallow detector exceptions; only the
        // *parse* failure-mode is silenced. Confirm prober errors bubble.
        var thrower = new ThrowingProber();
        Assert.Throws<InvalidOperationException>(() =>
            FrameProbingMiddleware.Observe(thrower, "s", "m", "{\"a\":1}"));
    }

    private sealed class ThrowingProber : IFrameProber
    {
        public void ObserveFrame(in DetectionContext ctx) =>
            throw new InvalidOperationException("boom");
    }
}
