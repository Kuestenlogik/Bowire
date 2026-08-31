// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
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
        // #637 — cancel when the command says it is up, not two seconds
        // after asking. The old version gave "start two listeners, load a
        // CA, then shut down" a fixed 2s budget, which on a loaded runner
        // put the cancellation inside StartAsync instead of after it — a
        // different code path, tested by accident, roughly once a week.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var options = new ProxyCommand.ProxyOptions { Port = 0, ApiPort = 0, Capacity = 50 };

        // Deliberately never disposed. ReadySignal owns nothing — a
        // StringBuilder and a TaskCompletionSource — so Dispose has nothing
        // to do, and CA2025 fires on *any* shape that hands a disposable to
        // a task and later disposes it, however provably ordered the awaits
        // are. Not holding the obligation is truer than arranging the code
        // to look like it discharges one.
        var ready = new ReadySignal("press Ctrl-C to stop");
        var run = ProxyCommand.RunAsync(options, stdout: ready, cancellationToken: cts.Token);

        await ready.Reached.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
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

    /// <summary>
    /// A <see cref="TextWriter"/> that completes a task the first time the
    /// command writes a given phrase.
    /// </summary>
    /// <remarks>
    /// Waiting for the process to say it is ready beats waiting for a
    /// duration to elapse: the duration is a guess about a machine nobody
    /// controls, and the banner is the thing that is actually true.
    /// </remarks>
    private sealed class ReadySignal(string phrase) : TextWriter
    {
        private readonly TaskCompletionSource _reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly StringBuilder _line = new();

        public Task Reached => _reached.Task;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                if (_line.ToString().Contains(phrase, StringComparison.Ordinal))
                {
                    _reached.TrySetResult();
                }
                _line.Clear();
                return;
            }
            _line.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var c in value) Write(c);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }
    }
}
