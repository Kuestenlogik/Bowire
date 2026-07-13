// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// Coverage for <see cref="TransitionDetector"/> — a signal fires only on a
/// pass↔fail edge, and Error counts as not-passing.
/// </summary>
public sealed class TransitionDetectorTests
{
    [Fact]
    public void First_pass_is_no_signal()
        => Assert.Equal(ProbeTransition.None, TransitionDetector.Detect(null, ProbeResult.Pass));

    [Fact]
    public void First_failure_signals_failing()
        => Assert.Equal(ProbeTransition.ToFailing, TransitionDetector.Detect(null, ProbeResult.Fail));

    [Fact]
    public void First_error_signals_failing()
        => Assert.Equal(ProbeTransition.ToFailing, TransitionDetector.Detect(null, ProbeResult.Error));

    [Fact]
    public void Steady_pass_is_no_signal()
        => Assert.Equal(ProbeTransition.None, TransitionDetector.Detect(ProbeResult.Pass, ProbeResult.Pass));

    [Fact]
    public void Steady_fail_is_no_signal()
        => Assert.Equal(ProbeTransition.None, TransitionDetector.Detect(ProbeResult.Fail, ProbeResult.Fail));

    [Fact]
    public void Pass_to_fail_signals_failing()
        => Assert.Equal(ProbeTransition.ToFailing, TransitionDetector.Detect(ProbeResult.Pass, ProbeResult.Fail));

    [Fact]
    public void Pass_to_error_signals_failing()
        => Assert.Equal(ProbeTransition.ToFailing, TransitionDetector.Detect(ProbeResult.Pass, ProbeResult.Error));

    [Fact]
    public void Fail_to_pass_signals_recovery()
        => Assert.Equal(ProbeTransition.ToPassing, TransitionDetector.Detect(ProbeResult.Fail, ProbeResult.Pass));

    [Fact]
    public void Error_to_pass_signals_recovery()
        => Assert.Equal(ProbeTransition.ToPassing, TransitionDetector.Detect(ProbeResult.Error, ProbeResult.Pass));

    [Fact]
    public void Fail_to_error_stays_failing_no_edge()
        => Assert.Equal(ProbeTransition.None, TransitionDetector.Detect(ProbeResult.Fail, ProbeResult.Error));
}
