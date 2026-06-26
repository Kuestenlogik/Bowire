// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.App;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Smoke test for the <c>bowire interceptor</c> CLI subcommand (#307 —
/// Phase C of #153). Mirrors <see cref="Proxy.ProxyCommandTests"/>: boots
/// on dynamic ports, exercises the validation paths, and asserts the
/// graceful shutdown / port-collision branches return the expected exit
/// codes so a CI shell can branch on them.
/// </summary>
public sealed class InterceptorCommandTests
{
    [Fact]
    public async Task RunAsync_NullOptions_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await InterceptorCommand.RunAsync(null!, cancellationToken: ct));
    }

    [Fact]
    public async Task RunAsync_MissingUpstream_ReturnsUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await InterceptorCommand.RunAsync(
            new InterceptorCommand.InterceptorOptions { Upstream = "" },
            stdout, stderr, ct);
        Assert.Equal(64, code);
        Assert.Contains("--upstream is required", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_InvalidUpstreamScheme_ReturnsUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await InterceptorCommand.RunAsync(
            new InterceptorCommand.InterceptorOptions { Upstream = "ftp://example.com" },
            stdout, stderr, ct);
        Assert.Equal(64, code);
    }

    [Fact]
    public async Task RunAsync_BadListen_ReturnsUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await InterceptorCommand.RunAsync(
            new InterceptorCommand.InterceptorOptions
            {
                Upstream = "http://localhost:9999",
                Listen = "not-a-host-port",
            },
            stdout, stderr, ct);
        Assert.Equal(64, code);
    }

    [Fact]
    public async Task RunAsync_StartsOnDynamicPortsAndExitsGracefully()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        var options = new InterceptorCommand.InterceptorOptions
        {
            Upstream = "http://127.0.0.1:9", // discard service — never accepts
            Listen = "127.0.0.1:0",
            ApiPort = 0,
            Capacity = 10,
        };
        var code = await InterceptorCommand.RunAsync(options, cancellationToken: cts.Token);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task RunAsync_ApiPortInUse_ReturnsErrorCode1()
    {
        var ct = TestContext.Current.CancellationToken;
        using var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var occupied = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var options = new InterceptorCommand.InterceptorOptions
        {
            Upstream = "http://127.0.0.1:9",
            Listen = "127.0.0.1:0",
            ApiPort = occupied,
            Capacity = 10,
        };
        var code = await InterceptorCommand.RunAsync(options, cancellationToken: ct);
        Assert.Equal(1, code);
    }

    [Theory]
    [InlineData("127.0.0.1:8080", "127.0.0.1", 8080)]
    [InlineData("0.0.0.0:9000", "0.0.0.0", 9000)]
    [InlineData(":8080", "127.0.0.1", 8080)]
    public void TryParseListen_ValidValues_Parse(string raw, string expectedHost, int expectedPort)
    {
        Assert.True(InterceptorCommand.TryParseListen(raw, out var addr, out var port, out _));
        Assert.Equal(IPAddress.Parse(expectedHost), addr);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-colon")]
    [InlineData("host:not-a-number")]
    [InlineData("999.999.999.999:8080")]
    [InlineData("127.0.0.1:99999")]
    public void TryParseListen_InvalidValues_Fail(string raw)
    {
        Assert.False(InterceptorCommand.TryParseListen(raw, out _, out _, out var error));
        Assert.NotEmpty(error);
    }
}
