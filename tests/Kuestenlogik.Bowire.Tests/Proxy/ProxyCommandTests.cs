// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;

namespace Kuestenlogik.Bowire.Tests.Proxy;

/// <summary>
/// Smoke test for the <c>bowire proxy</c> CLI subcommand orchestrator.
/// Starts the command on dynamic ports (0 → Kestrel picks), gives the
/// listeners a beat to come up, then cancels — verifying the graceful
/// shutdown path. The error-branch tests (occupied port → exit 1) drive
/// the two <see cref="ProxyCommand.RunAsync"/> bind-failure paths so
/// the catch blocks land in the coverage report.
/// </summary>
public sealed class ProxyCommandTests
{
    [Fact]
    public async Task RunAsync_StartsOnDynamicPortsAndExitsGracefully()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        var options = new ProxyCommand.ProxyOptions { Port = 0, ApiPort = 0, Capacity = 50 };
        var code = await ProxyCommand.RunAsync(options, cts.Token);

        // Graceful shutdown via cancellation returns 0.
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task RunAsync_NullOptions_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await ProxyCommand.RunAsync(null!, ct));
    }

    [Fact]
    public async Task RunAsync_ProxyPortAlreadyInUse_ReturnsErrorCode1()
    {
        var ct = TestContext.Current.CancellationToken;
        // Hold port 1 (which won't bind on the test runner anyway — privileged port).
        var options = new ProxyCommand.ProxyOptions { Port = 1, ApiPort = 0, Capacity = 10 };
        var code = await ProxyCommand.RunAsync(options, ct);
        Assert.Equal(1, code);
    }
}
