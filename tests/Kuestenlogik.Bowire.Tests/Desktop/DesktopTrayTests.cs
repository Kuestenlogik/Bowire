// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Tests.Desktop;

/// <summary>
/// #687 — how the tool wires the tray: the status names the port, Quit stops
/// the host the way Ctrl+C does, Open opens the browser at the workbench, and
/// only the windowed launcher shows a tray at all.
/// </summary>
public sealed class DesktopTrayTests
{
    [Fact]
    public async Task Quit_Stops_The_Host_And_Open_Opens_The_Workbench()
    {
        var lifetime = new RecordingLifetime();
        var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var options = DesktopTray.Options("http://localhost:5080/", lifetime, (url, _) =>
        {
            opened.TrySetResult(url);
            return Task.CompletedTask;
        });

        Assert.Equal("Bowire on port 5080", options.Status);
        options.Quit();
        Assert.True(lifetime.Stopped);
        options.Open();
        Assert.Equal("http://localhost:5080/", await opened.Task.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Start_At_Login_Runs_The_Launcher_That_Is_Running()
    {
        var options = DesktopTray.Options("http://localhost:5080/", new RecordingLifetime(), (_, _) => Task.CompletedTask);

        if (OperatingSystem.IsWindows()) Assert.NotNull(options.StartAtLogin);
        else Assert.Null(options.StartAtLogin);
    }

    [Fact]
    public void Only_The_Windowed_Launcher_Shows_A_Tray()
    {
        // The test host is not bowirew: no tray, nothing reported.
        var reported = false;
        Assert.False(DesktopTray.IsWindowedLaunch);
        Assert.Null(DesktopTray.TryStart("http://localhost:5080/", new RecordingLifetime(), (_, _) => Task.CompletedTask, _ => reported = true));
        Assert.False(reported);
    }

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopped = true;
    }
}
