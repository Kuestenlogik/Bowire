// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Tests.Plugins;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The <c>--port-file</c> handoff as seen from <see cref="BrowserUiHost"/> (#615).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PortFileTests"/> covers the writer on its own; this covers when
/// it is called, which is the part with a contract attached: <b>the file
/// exists if and only if this run is bound</b>. A caller polls for the path
/// and navigates the moment it appears, so every way the file could exist and
/// be wrong is a caller opening a dead page.
/// </para>
/// <para>
/// Shares <see cref="BrowserUiHostTests.CollectionName"/> because the seams
/// these drive are static.
/// </para>
/// </remarks>
[Collection(BrowserUiHostTests.CollectionName)]
public sealed class BrowserUiHostPortFileTests
{
    private static IConfiguration Config(string? portFile = null, string? port = null)
    {
        var entries = new Dictionary<string, string?> { ["Bowire:NoBrowser"] = "true" };
        if (portFile is not null) entries["Bowire:PortFile"] = portFile;
        if (port is not null) entries["Bowire:Port"] = port;
        return new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "bowire-host-" + Guid.NewGuid().ToString("N") + ".json");

    private static void Cleanup(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Writes_The_PortFile_Only_Once_The_Host_Is_Listening()
    {
        var prevRunner = BrowserUiHost.HostRunner;
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var path = TempPath();
        try
        {
            BrowserUiHost.OpenBrowserAsync = (_, _) => Task.CompletedTask;

            var existedBeforeListening = true;
            string? whileRunning = null;
            BrowserUiHost.HostRunner = async (_, _, _, onListening, ct) =>
            {
                // Before the callback the host is still binding; a file here
                // would be a claim a caller acts on immediately.
                existedBeforeListening = File.Exists(path);
                await onListening("http://127.0.0.1:51999/", ct);
                // Read inside the host's lifetime — RunAsync removes the file
                // on the way out, which the next test is about.
                whileRunning = await File.ReadAllTextAsync(path, ct);
                return 0;
            };

            await BrowserUiHost.RunAsync(["--no-browser", "--port-file", path], Config(path),
                plugins: TestPluginLoaders.None(), ct: CancellationToken.None);

            Assert.False(existedBeforeListening);
            Assert.NotNull(whileRunning);
            Assert.Contains("51999", whileRunning, StringComparison.Ordinal);
        }
        finally
        {
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Removes_The_PortFile_On_An_Orderly_Shutdown()
    {
        var prevRunner = BrowserUiHost.HostRunner;
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var path = TempPath();
        try
        {
            BrowserUiHost.OpenBrowserAsync = (_, _) => Task.CompletedTask;
            BrowserUiHost.HostRunner = async (_, _, _, onListening, ct) =>
            {
                await onListening("http://127.0.0.1:52000/", ct);
                return 0;
            };

            await BrowserUiHost.RunAsync(["--no-browser", "--port-file", path], Config(path),
                plugins: TestPluginLoaders.None(), ct: CancellationToken.None);

            Assert.False(File.Exists(path));
        }
        finally
        {
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Removes_The_PortFile_When_The_Host_Throws()
    {
        // A host that dies after the address was announced. If the file
        // outlived it, the next reader would get a port nothing serves.
        var prevRunner = BrowserUiHost.HostRunner;
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var path = TempPath();
        try
        {
            BrowserUiHost.OpenBrowserAsync = (_, _) => Task.CompletedTask;
            BrowserUiHost.HostRunner = async (_, _, _, onListening, ct) =>
            {
                await onListening("http://127.0.0.1:52001/", ct);
                throw new InvalidOperationException("host died after announcing");
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                BrowserUiHost.RunAsync(["--no-browser", "--port-file", path], Config(path),
                    plugins: TestPluginLoaders.None(), ct: CancellationToken.None));

            Assert.False(File.Exists(path));
        }
        finally
        {
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Clears_A_Stale_PortFile_Before_Binding()
    {
        // What a hard-killed predecessor leaves behind — the one case no
        // in-process cleanup can cover. It has to be gone before the bind is
        // attempted rather than after it succeeds, because a caller may
        // already be polling the path.
        var prevRunner = BrowserUiHost.HostRunner;
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(path, """{"version":1,"url":"http://127.0.0.1:1/","pid":999999}""", TestContext.Current.CancellationToken);
            BrowserUiHost.OpenBrowserAsync = (_, _) => Task.CompletedTask;

            var staleReachedTheRunner = true;
            // Never announces — a host that fails during bind.
            BrowserUiHost.HostRunner = (_, _, _, _, _) =>
            {
                staleReachedTheRunner = File.Exists(path);
                return Task.FromResult(1);
            };

            await BrowserUiHost.RunAsync(["--no-browser", "--port-file", path], Config(path),
                plugins: TestPluginLoaders.None(), ct: CancellationToken.None);

            Assert.False(staleReachedTheRunner);
            Assert.False(File.Exists(path));
        }
        finally
        {
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Banner_Reports_The_Bound_Url_Not_The_Requested_Port()
    {
        // With --port 0 the requested port says nothing. This is the same
        // reason the banner moved behind the bind: printed before, it
        // announced an address a failing bind never made real.
        var prevRunner = BrowserUiHost.HostRunner;
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        using var stdout = new StringWriter();
        try
        {
            BrowserUiHost.OpenBrowserAsync = (_, _) => Task.CompletedTask;
            BrowserUiHost.HostRunner = async (_, _, _, onListening, ct) =>
            {
                await onListening("http://127.0.0.1:61234/", ct);
                return 0;
            };

            await BrowserUiHost.RunAsync(["--no-browser", "--port", "0"], Config(port: "0"),
                plugins: TestPluginLoaders.None(), stdout: stdout, ct: CancellationToken.None);

            var text = stdout.ToString();
            Assert.Contains("http://127.0.0.1:61234/", text, StringComparison.Ordinal);
            Assert.DoesNotContain(":0/", text, StringComparison.Ordinal);
        }
        finally
        {
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.OpenBrowserAsync = prevOpen;
        }
    }

    [Fact]
    public async Task Opens_The_Browser_At_The_Bound_Url()
    {
        // RunAsync suppresses the browser launch when CI or
        // DOTNET_RUNNING_IN_CONTAINER is set, or when the process is not
        // user-interactive — all true on a build runner, none true on a
        // developer's machine. Without clearing them this passes locally and
        // times out in CI, which is exactly what it did.
        var prevCi = Environment.GetEnvironmentVariable("CI");
        var prevContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        Environment.SetEnvironmentVariable("CI", null);
        Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", null);

        var prevRunner = BrowserUiHost.HostRunner;
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // A headless runner reports UserInteractive=false, which suppresses
        // the launch on its own and cannot be overridden. There the host
        // simply has to return cleanly.
        var launchExpected = Environment.UserInteractive;
        try
        {
            BrowserUiHost.OpenBrowserAsync = (url, _) => { opened.TrySetResult(url); return Task.CompletedTask; };
            BrowserUiHost.HostRunner = async (_, _, _, onListening, ct) =>
            {
                await onListening("http://127.0.0.1:61235/", ct);
                // The launch is fire-and-forget; hold the host open for it.
                if (launchExpected) await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return 0;
            };

            var rc = await BrowserUiHost.RunAsync([], new ConfigurationBuilder().Build(),
                plugins: TestPluginLoaders.None(), ct: CancellationToken.None);

            Assert.Equal(0, rc);
            if (launchExpected) Assert.Equal("http://127.0.0.1:61235/", await opened.Task);
        }
        finally
        {
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            Environment.SetEnvironmentVariable("CI", prevCi);
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", prevContainer);
        }
    }

    // ---- what the server reports vs. what a caller can navigate to ----
    //
    // Whatever comes out of here goes into the port file, and from there
    // straight into a browser. A wildcard that survives is a panel that
    // cannot connect.

    [Theory]
    [InlineData("http://[::]:5080", "http://localhost:5080/")]
    [InlineData("http://0.0.0.0:5080", "http://localhost:5080/")]
    [InlineData("http://+:5080", "http://localhost:5080/")]
    [InlineData("http://*:5080", "http://localhost:5080/")]
    public void Wildcard_Bindings_Become_Something_Connectable(string reported, string expected)
        => Assert.Equal(expected, BrowserUiHost.NormaliseBoundAddress(reported, 5080));

    [Theory]
    [InlineData("http://127.0.0.1:61234", "http://127.0.0.1:61234/")]
    [InlineData("http://127.0.0.1:61234/", "http://127.0.0.1:61234/")]
    [InlineData("http://localhost:5080", "http://localhost:5080/")]
    public void A_Concrete_Address_Survives_With_One_Trailing_Slash(string reported, string expected)
        => Assert.Equal(expected, BrowserUiHost.NormaliseBoundAddress(reported, 5080));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_Host_That_Reports_Nothing_Falls_Back_To_The_Requested_Port(string? reported)
    {
        // A TestServer, most likely: it exposes no addresses feature. The
        // port we asked for is then both the best answer available and the
        // correct one — with --port 0 there is nothing to fall back to, but
        // a host that binds no socket has no URL to report either.
        Assert.Equal("http://localhost:5080/", BrowserUiHost.NormaliseBoundAddress(reported, 5080));
    }
}
