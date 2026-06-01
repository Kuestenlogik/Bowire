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
        var code = await ProxyCommand.RunAsync(options, cancellationToken: cts.Token);

        // Graceful shutdown via cancellation returns 0.
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task RunAsync_NullOptions_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await ProxyCommand.RunAsync(null!, cancellationToken: ct));
    }

    [Fact]
    public async Task RunAsync_ProxyPortAlreadyInUse_ReturnsErrorCode1()
    {
        var ct = TestContext.Current.CancellationToken;
        // Bind a TcpListener on a dynamic port, then ask ProxyCommand
        // to bind the SAME port — should fail cleanly with exit 1.
        using var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        blocker.Start();
        var occupied = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port;

        var options = new ProxyCommand.ProxyOptions { Port = occupied, ApiPort = 0, Capacity = 10 };
        var code = await ProxyCommand.RunAsync(options, cancellationToken: ct);
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task RunAsync_ExportCa_WritesPublicCertAndExits()
    {
        var ct = TestContext.Current.CancellationToken;
        var caDir = Path.Combine(Path.GetTempPath(), $"bowire-proxy-test-ca-{Guid.NewGuid():N}");
        var exportPath = Path.Combine(caDir, "out", "bowire-ca.crt");
        try
        {
            var options = new ProxyCommand.ProxyOptions
            {
                Port = 0,
                ApiPort = 0,
                Capacity = 10,
                CaDir = caDir,
                ExportCa = exportPath,
            };
            var code = await ProxyCommand.RunAsync(options, cancellationToken: ct);
            Assert.Equal(0, code);
            Assert.True(File.Exists(exportPath));
        }
        finally
        {
            if (Directory.Exists(caDir)) Directory.Delete(caDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MitmDisabled_StartsAndExitsCleanlyOnCancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        var options = new ProxyCommand.ProxyOptions
        {
            Port = 0,
            ApiPort = 0,
            Capacity = 10,
            MitmHttps = false,    // hits the no-CA branch
        };
        var code = await ProxyCommand.RunAsync(options, cancellationToken: cts.Token);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task RunAsync_ApiPortInUse_ReturnsErrorCode1()
    {
        var ct = TestContext.Current.CancellationToken;
        // Hold the API port, then ask ProxyCommand to bind the same one.
        using var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        blocker.Start();
        var occupied = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port;

        // Use a fresh CA dir so we don't pollute ~/.bowire on the test machine.
        var caDir = Path.Combine(Path.GetTempPath(), $"bowire-proxy-api-test-ca-{Guid.NewGuid():N}");
        try
        {
            var options = new ProxyCommand.ProxyOptions
            {
                Port = 0,
                ApiPort = occupied,
                Capacity = 10,
                CaDir = caDir,
            };
            var code = await ProxyCommand.RunAsync(options, cancellationToken: ct);
            Assert.Equal(1, code);
        }
        finally
        {
            if (Directory.Exists(caDir)) Directory.Delete(caDir, recursive: true);
        }
    }
}
