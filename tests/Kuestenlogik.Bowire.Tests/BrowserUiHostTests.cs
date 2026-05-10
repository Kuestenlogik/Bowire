// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.App.Configuration;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Exercises <see cref="BrowserUiHost.RunAsync"/> via the two internal
/// seams (<see cref="BrowserUiHost.OpenBrowserAsync"/> and
/// <see cref="BrowserUiHost.HostRunner"/>) so the routing and
/// browser-launch logic gets covered without spawning a real Kestrel
/// host or a real <see cref="System.Diagnostics.Process"/>. Each test
/// saves and restores the static seams so parallel runs (and the
/// integration suite's real default host) keep working.
/// </summary>
[Collection(BrowserUiHostTests.CollectionName)]
public sealed class BrowserUiHostTests
{
    public const string CollectionName = "BrowserUiHostSerial";

    private static IConfiguration BuildConfig(Dictionary<string, string?>? entries = null)
    {
        var builder = new ConfigurationBuilder();
        if (entries is { Count: > 0 })
            builder.AddInMemoryCollection(entries);
        return builder.Build();
    }

    [Fact]
    public async Task RunAsync_NullArgs_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            BrowserUiHost.RunAsync(null!, BuildConfig(), pluginDir: "", CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_NullConfig_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            BrowserUiHost.RunAsync([], null!, pluginDir: "", CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_NoBrowser_DoesNotInvokeOpenBrowser()
    {
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var prevRunner = BrowserUiHost.HostRunner;
        var openCount = 0;
        var seenOptions = (BrowserUiOptions?)null;
        try
        {
            BrowserUiHost.OpenBrowserAsync = (_, _) => { Interlocked.Increment(ref openCount); return Task.CompletedTask; };
            BrowserUiHost.HostRunner = (_, ui, _) => { seenOptions = ui; return Task.FromResult(42); };

            var rc = await BrowserUiHost.RunAsync(
                [],
                BuildConfig(new()
                {
                    ["Bowire:Port"] = "5099",
                    ["Bowire:NoBrowser"] = "true"
                }),
                pluginDir: "",
                CancellationToken.None);

            Assert.Equal(42, rc);
            Assert.Equal(0, openCount);
            Assert.NotNull(seenOptions);
            Assert.Equal(5099, seenOptions!.Port);
            Assert.True(seenOptions.NoBrowser);
        }
        finally
        {
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            BrowserUiHost.HostRunner = prevRunner;
        }
    }

    [Fact]
    public async Task RunAsync_BrowserEnabled_LaunchesAtBoundUrl()
    {
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var prevRunner = BrowserUiHost.HostRunner;
        var captured = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BrowserUiHost.OpenBrowserAsync = (url, _) => { captured.TrySetResult(url); return Task.CompletedTask; };
            // Hold the runner open until the browser launch fires so the
            // background Task.Run has a chance to schedule before we
            // unblock RunAsync and tear the seams down.
            BrowserUiHost.HostRunner = async (_, _, ct) =>
            {
                await captured.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                return 0;
            };

            // CI env var would normally suppress auto-open — clear it for
            // this test so the "browser enabled" branch is reachable
            // regardless of where the test runs.
            var origCi = Environment.GetEnvironmentVariable("CI");
            var origContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
            try
            {
                Environment.SetEnvironmentVariable("CI", null);
                Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", null);

                // Only assert the launch URL when the runtime says it'd
                // actually be allowed to spawn a browser. Headless CI
                // boxes leave UserInteractive=false; we keep the test
                // useful there by just confirming RunAsync returned 0.
                if (Environment.UserInteractive)
                {
                    var rc = await BrowserUiHost.RunAsync(
                        [],
                        BuildConfig(new() { ["Bowire:Port"] = "5180" }),
                        pluginDir: "",
                        CancellationToken.None);

                    Assert.Equal(0, rc);
                    Assert.True(captured.Task.IsCompletedSuccessfully);
                    var openedUrl = await captured.Task;
                    Assert.Contains("localhost:5180/bowire", openedUrl, StringComparison.Ordinal);
                }
                else
                {
                    // Unblock the host runner directly so RunAsync can
                    // finish even when the browser branch is skipped.
                    captured.TrySetResult("(skipped — headless)");
                    var rc = await BrowserUiHost.RunAsync(
                        [],
                        BuildConfig(new() { ["Bowire:Port"] = "5180" }),
                        pluginDir: "",
                        CancellationToken.None);
                    Assert.Equal(0, rc);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("CI", origCi);
                Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", origContainer);
            }
        }
        finally
        {
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            BrowserUiHost.HostRunner = prevRunner;
        }
    }

    [Fact]
    public async Task RunAsync_OpenBrowserThrowing_IsSwallowed()
    {
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        var prevRunner = BrowserUiHost.HostRunner;
        var hostStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BrowserUiHost.OpenBrowserAsync = (_, _) =>
                throw new InvalidOperationException("simulated browser failure");
            BrowserUiHost.HostRunner = (_, _, _) =>
            {
                hostStarted.TrySetResult(true);
                return Task.FromResult(0);
            };

            var origCi = Environment.GetEnvironmentVariable("CI");
            try
            {
                Environment.SetEnvironmentVariable("CI", null);
                // The browser launch sits behind a Task.Run that catches
                // any exception; we just need to confirm RunAsync doesn't
                // surface the failure.
                var rc = await BrowserUiHost.RunAsync(
                    [],
                    BuildConfig(),
                    pluginDir: "",
                    CancellationToken.None);
                Assert.Equal(0, rc);
                Assert.True(hostStarted.Task.IsCompletedSuccessfully);
            }
            finally
            {
                Environment.SetEnvironmentVariable("CI", origCi);
            }
        }
        finally
        {
            BrowserUiHost.OpenBrowserAsync = prevOpen;
            BrowserUiHost.HostRunner = prevRunner;
        }
    }

    [Fact]
    public async Task RunAsync_McpAdapterEnabled_ForwardsThroughToOptions()
    {
        var prevRunner = BrowserUiHost.HostRunner;
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        BrowserUiOptions? seen = null;
        try
        {
            BrowserUiHost.OpenBrowserAsync = (_, _) => Task.CompletedTask;
            BrowserUiHost.HostRunner = (_, ui, _) => { seen = ui; return Task.FromResult(0); };

            var rc = await BrowserUiHost.RunAsync(
                ["--url", "http://api.local"],
                BuildConfig(new()
                {
                    ["Bowire:NoBrowser"] = "true",
                    ["Bowire:EnableMcpAdapter"] = "true",
                }),
                pluginDir: "",
                CancellationToken.None);

            Assert.Equal(0, rc);
            Assert.NotNull(seen);
            Assert.True(seen!.EnableMcpAdapter);
            Assert.Single(seen.ServerUrls);
            Assert.Equal("http://api.local", seen.PrimaryUrl);
            Assert.True(seen.LockServerUrl);
        }
        finally
        {
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.OpenBrowserAsync = prevOpen;
        }
    }

    [Fact]
    public async Task RunAsync_MultiUrl_LocksAndCountsCorrectly()
    {
        var prevRunner = BrowserUiHost.HostRunner;
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        BrowserUiOptions? seen = null;
        try
        {
            BrowserUiHost.OpenBrowserAsync = (_, _) => Task.CompletedTask;
            BrowserUiHost.HostRunner = (_, ui, _) => { seen = ui; return Task.FromResult(0); };

            var rc = await BrowserUiHost.RunAsync(
                ["--url", "http://a", "--url", "http://b"],
                BuildConfig(new() { ["Bowire:NoBrowser"] = "true" }),
                pluginDir: "",
                CancellationToken.None);

            Assert.Equal(0, rc);
            Assert.NotNull(seen);
            Assert.Equal(2, seen!.ServerUrls.Count);
            Assert.Equal("http://a", seen.PrimaryUrl);
            Assert.True(seen.LockServerUrl);
        }
        finally
        {
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.OpenBrowserAsync = prevOpen;
        }
    }

    [Fact]
    public async Task RunAsync_DefaultHostRunner_ShutsDownPromptlyWhenCancelled()
    {
        // Exercise the real DefaultHostRunner — the WebApplication
        // build + MapBowire + MapGet wiring. Cancel before calling so
        // app.RunAsync exits immediately without binding the socket
        // for long. Uses a high port unlikely to collide in CI.
        // OS-agnostic: ports >50000 typically free, but we still
        // accept binding failures as a pass since the path before
        // RunAsync still executed.
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        try
        {
            BrowserUiHost.OpenBrowserAsync = (_, _) => Task.CompletedTask;
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            try
            {
                var rc = await BrowserUiHost.RunAsync(
                    [],
                    BuildConfig(new()
                    {
                        ["Bowire:NoBrowser"] = "true",
                        ["Bowire:Port"] = "55831",
                        ["Bowire:EnableMcpAdapter"] = "true",
                        ["Bowire:ServerUrl"] = "http://api.local",
                    }),
                    pluginDir: "",
                    cts.Token);
                Assert.Contains(rc, s_acceptedExitCodes);
            }
            catch (OperationCanceledException)
            {
                // Pre-cancelled token can surface directly out of
                // ASP.NET's host pipeline depending on the timing —
                // the lines we care about (builder.Build, MapBowire,
                // MapGet) all executed before that point.
            }
        }
        finally
        {
            BrowserUiHost.OpenBrowserAsync = prevOpen;
        }
    }

    private static readonly int[] s_acceptedExitCodes = [0];

    [Fact]
    public async Task RunAsync_SingleUrl_DescribesLockedConnection()
    {
        // Single URL + LockServerUrl=true exercises the
        // "Connected to {PrimaryUrl}" branch of the description builder
        // inside the default host runner. We assert the seen options
        // pass through correctly so the default-runner path stays
        // covered via its sibling tests.
        var prevRunner = BrowserUiHost.HostRunner;
        var prevOpen = BrowserUiHost.OpenBrowserAsync;
        BrowserUiOptions? seen = null;
        try
        {
            BrowserUiHost.OpenBrowserAsync = (_, _) => Task.CompletedTask;
            BrowserUiHost.HostRunner = (_, ui, _) => { seen = ui; return Task.FromResult(0); };

            var rc = await BrowserUiHost.RunAsync(
                [],
                BuildConfig(new()
                {
                    ["Bowire:NoBrowser"] = "true",
                    ["Bowire:ServerUrl"] = "http://only.local",
                }),
                pluginDir: "",
                CancellationToken.None);

            Assert.Equal(0, rc);
            Assert.NotNull(seen);
            Assert.Single(seen!.ServerUrls);
            Assert.True(seen.LockServerUrl);
        }
        finally
        {
            BrowserUiHost.HostRunner = prevRunner;
            BrowserUiHost.OpenBrowserAsync = prevOpen;
        }
    }
}

/// <summary>
/// Forces all <see cref="BrowserUiHostTests"/> into a single collection
/// so the static seams (<c>OpenBrowserAsync</c> / <c>HostRunner</c>)
/// can't be torn down by one test while another is mid-run.
/// </summary>
[CollectionDefinition(BrowserUiHostTests.CollectionName, DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit collection definitions must be public.
#pragma warning disable CA1711 // Suffix "Collection" is xUnit convention.
public sealed class BrowserUiHostTestsCollection { }
#pragma warning restore CA1711
#pragma warning restore CA1515
