// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #563: the Scanner-backed <see cref="AuthFlowCapturer"/> translates a
/// misconfigured / failing flow into a Core-visible
/// <see cref="AuthFlowCaptureException"/> (so the auth-recording endpoint can
/// surface a clean error without referencing the Scanner sibling). The
/// happy-path flow execution itself is covered by the AuthFlowRunner tests;
/// these pin the error-boundary that is unique to the adapter and run with no
/// network (they fail before any request is sent).
/// </summary>
public sealed class AuthFlowCapturerTests
{
    [Fact]
    public async Task Malformed_Flow_Json_Throws_AuthFlowCaptureException()
    {
        await Assert.ThrowsAsync<AuthFlowCaptureException>(() =>
            new AuthFlowCapturer().CaptureAsync("not json at all", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Flow_With_No_Steps_Throws_AuthFlowCaptureException()
    {
        // Valid JSON, but the flow yields no token → the adapter wraps the
        // AuthFlowException as a Core-visible AuthFlowCaptureException. No
        // request is sent (it fails before the first step).
        await Assert.ThrowsAsync<AuthFlowCaptureException>(() =>
            new AuthFlowCapturer().CaptureAsync("""{"steps":[]}""", TestContext.Current.CancellationToken));
    }
}
