// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Tests.Plugins;
using Microsoft.Extensions.Configuration;

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
        // #637 — cancel when the command says it is up, not two seconds
        // after asking. The old version gave "start two listeners, load a
        // CA, then shut down" a fixed 2s budget, which on a loaded runner
        // put the cancellation inside StartAsync instead of after it — a
        // different code path, tested by accident, roughly once a week.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var options = new ProxyCommand.ProxyOptions { Port = 0, ApiPort = 0, Capacity = 50 };

        // Through the command's own readiness callback rather than by
        // scraping its stdout: a TextWriter is IDisposable, and handing one
        // to a task is a shape the analyzers reject in every arrangement —
        // which was the nudge to give ProxyCommand the seam it owed anyway.
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = ProxyCommand.RunAsync(
            options,
            onListening: (_, _) => listening.TrySetResult(),
            cancellationToken: cts.Token);

        await listening.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        var code = await run;

        // Graceful shutdown via cancellation returns 0.
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task RunAsync_CancelledBeforeItFinishesStarting_StillExitsCleanly()
    {
        // The path the flake was landing on. Stopping before you have
        // started is the same event as stopping after, and it used to throw
        // OperationCanceledException out of RunAsync — an unhandled
        // exception and a stack trace where a clean exit belongs. It also
        // left the certificate authority undisposed.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var options = new ProxyCommand.ProxyOptions { Port = 0, ApiPort = 0, Capacity = 10 };
        var code = await ProxyCommand.RunAsync(options, cancellationToken: cts.Token);

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task ProxySubcommand_WiresTheParsedOptionsIntoTheCommand()
    {
        // The `proxy` CLI action had no test at all, because the only way to
        // reach it was to start a proxy and never stop it. With a token that
        // reaches the handler, a pre-cancelled run exercises the wiring and
        // returns through the cancelled-before-started path.
        //
        // Worth having for a reason this session demonstrated: the action
        // passed its cancellation token positionally, so adding a parameter
        // to ProxyCommand.RunAsync silently rebound it. That is a compile
        // error today and would have been a runtime one with a different
        // signature — either way, nothing was watching this call.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        // Concrete ports, not 0: the proxy subcommand's --port validation has
        // no ephemeral allowance (the workbench's does, because --port-file
        // gives 0 a way to report itself), so "0" is a parse error here and
        // would never reach the action this test exists to cover.
        var port = GetFreePort();
        var apiPort = GetFreePort();

        var rc = await BowireCli.RunAsync(
            ["proxy", "--port", port.ToString(CultureInfo.InvariantCulture),
             "--api-port", apiPort.ToString(CultureInfo.InvariantCulture), "--no-mitm"],
            new ConfigurationBuilder().Build(),
            plugins: TestPluginLoaders.None(),
            stdout: stdout,
            stderr: stderr,
            cancellationToken: cts.Token);

        Assert.Equal(0, rc);
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
    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

}
