// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using Kuestenlogik.Bowire.App.Cli;
using Microsoft.Extensions.Configuration;
using Kuestenlogik.Bowire.Tests.Plugins;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #684 — starting Bowire on a port that is already taken.
/// </summary>
/// <remarks>
/// The old behaviour was an unhandled <see cref="IOException"/> and forty
/// lines of stack trace, which is the default outcome of a second
/// double-click the moment there is a clickable launcher. These tests pin the
/// four cases: adopt a running Bowire, refuse a stranger, say what the adopted
/// instance cannot honour, and leave every other failure alone.
///
/// Shares the serial collection with <see cref="BrowserUiHostTests"/> because
/// the seams are process-global statics.
/// </remarks>
[Collection(BrowserUiHostTests.CollectionName)]
public sealed class BrowserUiHostPortInUseTests
{
    private static IConfiguration Config(Dictionary<string, string?> entries)
        => new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    /// <summary>The exception ASP.NET surfaces when Kestrel cannot bind.</summary>
    private static IOException AddressInUse()
        => new("Failed to bind to address", new SocketException((int)SocketError.AddressAlreadyInUse));

    [Fact]
    public void AddressInUse_RoundTripsThroughSocketErrorCode()
    {
        // The detection matches on the socket error code rather than on a
        // message, because the message comes from the OS in the user's
        // system language. If a platform ever maps this differently, fail
        // here rather than in the four tests below.
        var socket = Assert.IsType<SocketException>(AddressInUse().InnerException);
        Assert.Equal(SocketError.AddressAlreadyInUse, socket.SocketErrorCode);
    }

    [Fact]
    public async Task PortTakenByBowire_OpensTheRunningOneAndExitsZero()
    {
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var prevRunner = BrowserUiHost.HostRunner;
        var prevProbe = BrowserUiHost.ProbePortAsync;
        var opened = new List<string>();
        using var stdout = new StringWriter();
        try
        {
            BrowserUiHost.HostRunner = (_, _, _, _, _) => throw AddressInUse();
            BrowserUiHost.ProbePortAsync = (_, _) => Task.FromResult(BrowserUiHost.PortOccupant.Bowire);
            BrowserUiHost.OpenBrowserAsync = (url, _) => { opened.Add(url); return Task.CompletedTask; };

            // The adopt path opens a browser, and a runner suppresses that
            // through CI / DOTNET_RUNNING_IN_CONTAINER / UserInteractive —
            // so the launch half has to be enabled here or asked about.
            using var launch = BrowserLaunchEnvironment.Allow();

            var rc = await BrowserUiHost.RunAsync(
                [],
                Config(new() { ["Bowire:Port"] = "5080" }),
                plugins: TestPluginLoaders.None(),
                stdout: stdout,
                ct: CancellationToken.None);

            // Nothing went wrong: the operator asked for a workbench and has
            // one. A non-zero code here would fail a script that starts
            // Bowire idempotently.
            Assert.Equal(0, rc);
            if (BrowserLaunchEnvironment.LaunchExpected)
                Assert.Equal(["http://localhost:5080/"], opened);

            // The URL is on stdout either way — that is what makes the
            // suppressed-launch case usable rather than silent.
            Assert.Contains("already running", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("http://localhost:5080/", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.ProbePortAsync = prevProbe;
        }
    }

    // ---- the real probe -------------------------------------------------
    //
    // Every test above stubs ProbePortAsync, which means the default probe was
    // covered by nothing at all. The first version of it answered "in use" on
    // a FREE port -- it read "is anything there?" out of an HttpClient
    // exception, and a refused connection is not reliably distinguishable from
    // a timeout or a non-HTTP server that way. Bowire would have refused to
    // start, which is worse than the crash this issue is about. These two
    // tests use a real socket, because that is the part a stub cannot check.

    [Fact]
    public async Task DefaultProbe_OnAFreePort_SaysNothingIsThere()
    {
        var port = FreePort();
        var occupant = await BrowserUiHost.DefaultProbePort(port, TestContext.Current.CancellationToken);
        Assert.Equal(BrowserUiHost.PortOccupant.None, occupant);
    }

    [Fact]
    public async Task DefaultProbe_OnAListenerThatIsNotBowire_SaysOther()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var occupant = await BrowserUiHost.DefaultProbePort(port, TestContext.Current.CancellationToken);
            Assert.Equal(BrowserUiHost.PortOccupant.Other, occupant);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>A port nobody is listening on, found by binding and releasing.</summary>
    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task PortTaken_TheHostIsNeverBuilt()
    {
        // This is the whole reason the check is a PRE-flight. Catching the
        // bind failure afterwards is not enough: ASP.NET logs "Hosting failed
        // to start" with the full exception through its own logger on the way
        // out, so the operator gets forty lines of stack trace before our
        // tidy message. The only way to keep the console clean is not to
        // attempt the bind at all.
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var prevRunner = BrowserUiHost.HostRunner;
        var prevProbe = BrowserUiHost.ProbePortAsync;
        var runnerCalls = 0;
        try
        {
            BrowserUiHost.HostRunner = (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref runnerCalls);
                return Task.FromResult(0);
            };
            BrowserUiHost.ProbePortAsync = (_, _) => Task.FromResult(BrowserUiHost.PortOccupant.Bowire);
            BrowserUiHost.OpenBrowserAsync = (_, _) => Task.CompletedTask;

            var rc = await BrowserUiHost.RunAsync(
                [],
                Config(new() { ["Bowire:Port"] = "5080", ["Bowire:NoBrowser"] = "true" }),
                plugins: TestPluginLoaders.None(),
                ct: CancellationToken.None);

            Assert.Equal(0, rc);
            Assert.Equal(0, runnerCalls);
        }
        finally
        {
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.ProbePortAsync = prevProbe;
        }
    }

    [Fact]
    public async Task PlatformConfiguredAddress_SkipsThePreflightEntirely()
    {
        // When ASPNETCORE_URLS (or http_ports, or a Kestrel:Endpoints section)
        // names the address and the operator did not pass --port, Bowire does
        // not call UseUrls at all (#634) and Kestrel listens somewhere else.
        // Probing 5080 there would refuse to start over a port nobody was
        // going to use.
        var prevRunner = BrowserUiHost.HostRunner;
        var prevProbe = BrowserUiHost.ProbePortAsync;
        var probeCount = 0;
        try
        {
            BrowserUiHost.HostRunner = (_, _, _, _, _) => Task.FromResult(11);
            BrowserUiHost.ProbePortAsync = (_, _) =>
            {
                Interlocked.Increment(ref probeCount);
                return Task.FromResult(BrowserUiHost.PortOccupant.Other);
            };

            var rc = await BrowserUiHost.RunAsync(
                [],
                Config(new()
                {
                    ["urls"] = "http://localhost:8080",
                    ["Bowire:NoBrowser"] = "true",
                }),
                plugins: TestPluginLoaders.None(),
                ct: CancellationToken.None);

            Assert.Equal(11, rc);
            Assert.Equal(0, probeCount);
        }
        finally
        {
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.ProbePortAsync = prevProbe;
        }
    }

    [Fact]
    public async Task ExplicitPortBeatsAPlatformConfiguredAddress()
    {
        // --port is the operator saying "this one", which outranks the
        // platform's address (#634) — so the pre-flight applies again.
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var prevRunner = BrowserUiHost.HostRunner;
        var prevProbe = BrowserUiHost.ProbePortAsync;
        var runnerCalls = 0;
        try
        {
            BrowserUiHost.HostRunner = (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref runnerCalls);
                return Task.FromResult(0);
            };
            BrowserUiHost.ProbePortAsync = (_, _) => Task.FromResult(BrowserUiHost.PortOccupant.Bowire);
            BrowserUiHost.OpenBrowserAsync = (_, _) => Task.CompletedTask;

            var rc = await BrowserUiHost.RunAsync(
                [],
                Config(new()
                {
                    ["urls"] = "http://localhost:8080",
                    ["Bowire:Port"] = "5080",
                    ["Bowire:NoBrowser"] = "true",
                }),
                plugins: TestPluginLoaders.None(),
                ct: CancellationToken.None);

            Assert.Equal(0, rc);
            Assert.Equal(0, runnerCalls);
        }
        finally
        {
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.ProbePortAsync = prevProbe;
        }
    }

    [Fact]
    public async Task FreePort_StartsNormallyAndReturnsTheRunnersCode()
    {
        // The pre-flight must not get in the way of the ordinary start: a
        // refused connection means nobody is listening, and the run proceeds.
        var prevRunner = BrowserUiHost.HostRunner;
        var prevProbe = BrowserUiHost.ProbePortAsync;
        try
        {
            BrowserUiHost.HostRunner = (_, _, _, _, _) => Task.FromResult(7);
            BrowserUiHost.ProbePortAsync = (_, _) => Task.FromResult(BrowserUiHost.PortOccupant.None);

            var rc = await BrowserUiHost.RunAsync(
                [],
                Config(new() { ["Bowire:Port"] = "5080", ["Bowire:NoBrowser"] = "true" }),
                plugins: TestPluginLoaders.None(),
                ct: CancellationToken.None);

            Assert.Equal(7, rc);
        }
        finally
        {
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.ProbePortAsync = prevProbe;
        }
    }

    [Fact]
    public async Task PortTakenByBowire_WithNoBrowser_StillExitsZeroWithoutOpening()
    {
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var prevRunner = BrowserUiHost.HostRunner;
        var prevProbe = BrowserUiHost.ProbePortAsync;
        var openCount = 0;
        try
        {
            BrowserUiHost.HostRunner = (_, _, _, _, _) => throw AddressInUse();
            BrowserUiHost.ProbePortAsync = (_, _) => Task.FromResult(BrowserUiHost.PortOccupant.Bowire);
            BrowserUiHost.OpenBrowserAsync = (_, _) => { Interlocked.Increment(ref openCount); return Task.CompletedTask; };

            var rc = await BrowserUiHost.RunAsync(
                [],
                Config(new() { ["Bowire:Port"] = "5080", ["Bowire:NoBrowser"] = "true" }),
                plugins: TestPluginLoaders.None(),
                ct: CancellationToken.None);

            Assert.Equal(0, rc);
            Assert.Equal(0, openCount);
        }
        finally
        {
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.ProbePortAsync = prevProbe;
        }
    }

    [Fact]
    public async Task PortTakenByStranger_RefusesWithOneLineAndNoBrowser()
    {
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var prevRunner = BrowserUiHost.HostRunner;
        var prevProbe = BrowserUiHost.ProbePortAsync;
        var openCount = 0;
        using var stderr = new StringWriter();
        try
        {
            BrowserUiHost.HostRunner = (_, _, _, _, _) => throw AddressInUse();
            BrowserUiHost.ProbePortAsync = (_, _) => Task.FromResult(BrowserUiHost.PortOccupant.Other);
            BrowserUiHost.OpenBrowserAsync = (_, _) => { Interlocked.Increment(ref openCount); return Task.CompletedTask; };

            var rc = await BrowserUiHost.RunAsync(
                [],
                Config(new() { ["Bowire:Port"] = "5080" }),
                plugins: TestPluginLoaders.None(),
                stderr: stderr,
                ct: CancellationToken.None);

            Assert.Equal(1, rc);
            Assert.Equal(0, openCount);

            var text = stderr.ToString();
            Assert.Contains("5080", text, StringComparison.Ordinal);
            Assert.Contains("not Bowire", text, StringComparison.Ordinal);
            // Both ways out are named, so the operator does not have to go
            // looking for --help.
            Assert.Contains("--port", text, StringComparison.Ordinal);
            Assert.Contains("--port-file", text, StringComparison.Ordinal);
            // The whole point: no stack trace, and nothing from the OS in
            // whatever language the OS speaks.
            Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SocketException", text, StringComparison.Ordinal);
        }
        finally
        {
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.ProbePortAsync = prevProbe;
        }
    }

    [Fact]
    public async Task AdoptingAnInstance_SaysWhichServerFlagsItCannotHonour()
    {
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var prevRunner = BrowserUiHost.HostRunner;
        var prevProbe = BrowserUiHost.ProbePortAsync;
        using var stderr = new StringWriter();
        try
        {
            BrowserUiHost.HostRunner = (_, _, _, _, _) => throw AddressInUse();
            BrowserUiHost.ProbePortAsync = (_, _) => Task.FromResult(BrowserUiHost.PortOccupant.Bowire);
            BrowserUiHost.OpenBrowserAsync = (_, _) => Task.CompletedTask;

            var rc = await BrowserUiHost.RunAsync(
                [],
                Config(new()
                {
                    ["Bowire:Port"] = "5080",
                    ["Bowire:NoBrowser"] = "true",
                    ["Bowire:ServerUrl"] = "http://api.example.com",
                    ["Bowire:EnableMcpAdapter"] = "true",
                    ["Bowire:Title"] = "Staging",
                }),
                plugins: TestPluginLoaders.None(),
                stderr: stderr,
                ct: CancellationToken.None);

            // Adopting is still the right answer — but silently dropping what
            // the operator asked for is not.
            Assert.Equal(0, rc);
            var text = stderr.ToString();
            Assert.Contains("--url", text, StringComparison.Ordinal);
            Assert.Contains("--enable-mcp-adapter", text, StringComparison.Ordinal);
            Assert.Contains("--title", text, StringComparison.Ordinal);
        }
        finally
        {
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.ProbePortAsync = prevProbe;
        }
    }

    [Fact]
    public async Task AdoptingAnInstance_StaysQuietWhenNothingWasAskedFor()
    {
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var prevRunner = BrowserUiHost.HostRunner;
        var prevProbe = BrowserUiHost.ProbePortAsync;
        using var stderr = new StringWriter();
        try
        {
            BrowserUiHost.HostRunner = (_, _, _, _, _) => throw AddressInUse();
            BrowserUiHost.ProbePortAsync = (_, _) => Task.FromResult(BrowserUiHost.PortOccupant.Bowire);
            BrowserUiHost.OpenBrowserAsync = (_, _) => Task.CompletedTask;

            var rc = await BrowserUiHost.RunAsync(
                [],
                Config(new() { ["Bowire:Port"] = "5080", ["Bowire:NoBrowser"] = "true" }),
                plugins: TestPluginLoaders.None(),
                stderr: stderr,
                ct: CancellationToken.None);

            Assert.Equal(0, rc);
            // A plain double-click carries no server flags, so there is
            // nothing to warn about and the run says nothing on stderr.
            Assert.DoesNotContain("cannot be changed", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.ProbePortAsync = prevProbe;
        }
    }

    [Fact]
    public async Task PortZero_IsLeftAlone()
    {
        // --port 0 means "OS, pick a free one". There is nothing to adopt and
        // an in-use failure there is not the case this handles, so the
        // exception must still surface rather than be swallowed into a
        // confident "already running" that is not true.
        var prevRunner = BrowserUiHost.HostRunner;
        var prevProbe = BrowserUiHost.ProbePortAsync;
        var probeCount = 0;
        try
        {
            BrowserUiHost.HostRunner = (_, _, _, _, _) => throw AddressInUse();
            BrowserUiHost.ProbePortAsync = (_, _) =>
            {
                Interlocked.Increment(ref probeCount);
                return Task.FromResult(BrowserUiHost.PortOccupant.Bowire);
            };

            await Assert.ThrowsAsync<IOException>(() => BrowserUiHost.RunAsync(
                [],
                Config(new() { ["Bowire:Port"] = "0", ["Bowire:NoBrowser"] = "true" }),
                plugins: TestPluginLoaders.None(),
                ct: CancellationToken.None));

            Assert.Equal(0, probeCount);
        }
        finally
        {
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.ProbePortAsync = prevProbe;
        }
    }

    [Fact]
    public async Task AnyOtherBindFailure_StillSurfaces()
    {
        // Only "the port is taken" is interpreted. A permission problem, a
        // bad interface, a certificate that will not load — those are real
        // failures and must not be dressed up as an adopted instance.
        var prevRunner = BrowserUiHost.HostRunner;
        var prevProbe = BrowserUiHost.ProbePortAsync;
        var probeCount = 0;
        try
        {
            BrowserUiHost.HostRunner = (_, _, _, _, _) =>
                throw new IOException("Failed to bind", new SocketException((int)SocketError.AccessDenied));
            // The port is free, so the pre-flight waves the start through and
            // the runner's own failure is the one under test.
            BrowserUiHost.ProbePortAsync = (_, _) =>
            {
                Interlocked.Increment(ref probeCount);
                return Task.FromResult(BrowserUiHost.PortOccupant.None);
            };

            await Assert.ThrowsAsync<IOException>(() => BrowserUiHost.RunAsync(
                [],
                Config(new() { ["Bowire:Port"] = "5080", ["Bowire:NoBrowser"] = "true" }),
                plugins: TestPluginLoaders.None(),
                ct: CancellationToken.None));

            // Probed once by the pre-flight, and not again: an AccessDenied
            // is not the case this handles.
            Assert.Equal(1, probeCount);
        }
        finally
        {
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.ProbePortAsync = prevProbe;
        }
    }
}
