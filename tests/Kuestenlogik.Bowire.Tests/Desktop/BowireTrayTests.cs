// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using Kuestenlogik.Bowire.Desktop;
using Microsoft.Win32;

namespace Kuestenlogik.Bowire.Tests.Desktop;

/// <summary>
/// #687 — the tray item: it appears, its menu commands reach the callbacks
/// (posted to its window the way a menu click arrives), and Start at login
/// writes and removes one value under the user's key. Windows only; on other
/// platforms <see cref="BowireTray.Start"/> answers null, which is tested too.
/// </summary>
public sealed class BowireTrayTests
{
    [Fact]
    public void On_A_Platform_Without_A_Tray_Start_Answers_Null_And_Nothing_Breaks()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Null(BowireTray.Start(new BowireTrayOptions { Status = "Bowire", Open = () => { }, Quit = () => { } }));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void The_Item_Appears_And_Its_Commands_Reach_The_Callbacks()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var opened = new ManualResetEventSlim();
        using var quit = new ManualResetEventSlim();

        using var tray = (WindowsTray?)BowireTray.Start(new BowireTrayOptions
        {
            Status = "Bowire on port 5080",
            Open = opened.Set,
            Quit = quit.Set,
        });

        Assert.NotNull(tray);
        Assert.True(tray.IsShown);
        Assert.NotEqual(IntPtr.Zero, Win32.FindWindowExW(Win32.HWND_MESSAGE, IntPtr.Zero, WindowsTray.WindowClass, null));
        tray.PostCommand(WindowsTray.CmdOpen);
        Assert.True(opened.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "Open did not arrive");
        tray.PostCommand(WindowsTray.CmdQuit);
        Assert.True(quit.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "Quit did not arrive");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Start_At_Login_Is_Off_Until_Ticked_And_Ticking_Again_Removes_It()
    {
        if (!OperatingSystem.IsWindows()) return;
        var key = @"Software\Kuestenlogik\BowireTests\Run-" + Guid.NewGuid().ToString("N");
        try
        {
            var login = new StartAtLogin("Bowire", "\"C:\\Program Files\\Bowire\\bowirew.exe\"", key);
            Assert.False(login.IsEnabled);

            login.Set(true);
            Assert.True(login.IsEnabled);
            using (var k = Registry.CurrentUser.OpenSubKey(key))
                Assert.Equal("\"C:\\Program Files\\Bowire\\bowirew.exe\"", k!.GetValue("Bowire"));

            login.Set(false);
            Assert.False(login.IsEnabled);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
    }
}
