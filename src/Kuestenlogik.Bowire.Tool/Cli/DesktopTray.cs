// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Desktop;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// #687 — the tray item of a windowed launch. <c>bowirew.exe</c> opens no
/// console, so the tray is how the user sees that Bowire runs and how they
/// stop it. <c>bowire.exe</c> in a terminal shows none: its console is its
/// handle, as before.
/// </summary>
internal static class DesktopTray
{
    /// <summary>The windowed launcher's name, beside <c>bowire.exe</c>.</summary>
    public const string WindowedLauncher = "bowirew";

    /// <summary>Whether this process was started as the windowed launcher.</summary>
    public static bool IsWindowedLaunch =>
        string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), WindowedLauncher, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Show the tray item for the workbench at <paramref name="url"/>; Quit
    /// stops the host the way Ctrl+C does. Null when there is no tray: not a
    /// windowed launch, a platform without one, or the desktop package not
    /// deployed, in which case the tool runs exactly as it did without it.
    /// </summary>
    public static IDisposable? TryStart(string url, IHostApplicationLifetime lifetime, Func<string, CancellationToken, Task> openBrowser, Action<string>? report = null)
    {
        if (!IsWindowedLaunch) return null;
        try
        {
            var tray = Start(url, lifetime, openBrowser);
            if (tray is null) report?.Invoke("this platform or desktop session offers no notification area");
            return tray;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            report?.Invoke("Kuestenlogik.Bowire.Desktop is not deployed (" + ex.GetType().Name + ")");
            return null;
        }
    }

    // Its own method, not inlined: a missing Kuestenlogik.Bowire.Desktop
    // assembly fails when this is compiled, inside the catch above.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDisposable? Start(string url, IHostApplicationLifetime lifetime, Func<string, CancellationToken, Task> openBrowser)
        => BowireTray.Start(Options(url, lifetime, openBrowser));

    /// <summary>What the item shows and does for the workbench at <paramref name="url"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static BowireTrayOptions Options(string url, IHostApplicationLifetime lifetime, Func<string, CancellationToken, Task> openBrowser)
    {
        var port = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Port : 0;
        return new BowireTrayOptions
        {
            Status = port > 0 ? $"Bowire on port {port}" : "Bowire",
            Open = () => _ = Task.Run(() => openBrowser(url, CancellationToken.None)),
            Quit = lifetime.StopApplication,
            StartAtLogin = OperatingSystem.IsWindows() && Environment.ProcessPath is { } path
                ? new StartAtLogin("Bowire", "\"" + path + "\"")
                : null,
        };
    }
}
