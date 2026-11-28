// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Kuestenlogik.Bowire.Desktop;

/// <summary>What the tray item shows and does.</summary>
public sealed class BowireTrayOptions
{
    /// <summary>The item's tooltip and the menu's first, informational line, e.g. <c>Bowire on port 5080</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Opens the workbench in the browser (menu item and a click on the icon).</summary>
    public required Action Open { get; init; }

    /// <summary>Shuts the host down cleanly.</summary>
    public required Action Quit { get; init; }

    /// <summary>The login entry the menu toggles; null leaves the item out.</summary>
    public StartAtLogin? StartAtLogin { get; init; }

    /// <summary>The icon: a file whose first icon is taken; null takes the running executable's.</summary>
    public string? IconPath { get; init; }

    /// <summary>Menu labels, English unless the caller passes its own.</summary>
    public string OpenLabel { get; init; } = "Open Bowire";

    /// <inheritdoc cref="OpenLabel"/>
    public string StartAtLoginLabel { get; init; } = "Start at login";

    /// <inheritdoc cref="OpenLabel"/>
    public string QuitLabel { get; init; } = "Quit";
}

/// <summary>
/// #687 — the running workbench's handle on the desktop: a notification-area
/// icon with Open, Start at login and Quit. It is what makes a launch without
/// a console window safe, since it is the way to see that Bowire runs and to
/// stop it.
/// </summary>
public static class BowireTray
{
    /// <summary>
    /// Show the item. Null on a platform without one (today: everything but
    /// Windows), or when the shell refuses the icon; the caller runs on as
    /// before. Dispose to remove it.
    /// </summary>
    public static IDisposable? Start(BowireTrayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows()) return null;
        var tray = new WindowsTray(options);
        return tray.WaitShown() ? tray : DisposeAndNull(tray);
    }

    [SupportedOSPlatform("windows")]
    private static IDisposable? DisposeAndNull(WindowsTray tray)
    {
        tray.Dispose();
        return null;
    }
}

/// <summary>
/// Shell_NotifyIcon on a thread of its own: a message-only window receives
/// the icon's clicks and the menu's commands, and its message loop is the
/// thread's whole life.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsTray : IDisposable
{
    internal const int CmdOpen = 1;
    internal const int CmdStartAtLogin = 2;
    internal const int CmdQuit = 3;

    /// <summary>The message-only window's class: how a test, or a second tool, finds a running tray.</summary>
    internal const string WindowClass = ClassName;
    private const int CmdStatus = 4;
    private const uint CallbackMessage = Win32.WM_APP + 1;
    private const string ClassName = "Kuestenlogik.Bowire.Tray";

    // The window procedure is a static unmanaged entry point; one tray per
    // process is all there is, and this is how the procedure finds it.
    private static WindowsTray? s_current;
    private static uint s_taskbarCreated;

    private readonly BowireTrayOptions _options;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _shown;
    private int _disposed;

    public WindowsTray(BowireTrayOptions options)
    {
        _options = options;
        _thread = new Thread(Run) { IsBackground = true, Name = "Bowire tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Whether the icon is in the notification area.</summary>
    internal bool IsShown => _shown;

    /// <summary>Wait for the thread to report: the icon added, or refused.</summary>
    public bool WaitShown()
    {
        _ready.Wait();
        return _shown;
    }

    /// <summary>What a menu click does; tests post the command directly.</summary>
    internal void PostCommand(int command) => Win32.PostMessageW(_hwnd, Win32.WM_COMMAND, command, IntPtr.Zero);

    private void Run()
    {
        try
        {
            s_current = this;
            s_taskbarCreated = Win32.RegisterWindowMessageW("TaskbarCreated");
            var hInstance = Win32.GetModuleHandleW(null);
            fixed (char* className = ClassName)
            {
                var wc = new Win32.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = hInstance,
                    lpszClassName = className,
                };
                // A second tray in the same process finds the class registered; that is fine.
                Win32.RegisterClassExW(&wc);
            }
            _hwnd = Win32.CreateWindowExW(0, ClassName, "Bowire", 0, 0, 0, 0, 0, Win32.HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return;
            _icon = LoadIcon(_options.IconPath ?? Environment.ProcessPath);
            _shown = AddIcon();
        }
        finally
        {
            _ready.Set();
        }
        if (!_shown) return;

        Win32.MSG msg;
        while (Win32.GetMessageW(&msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(&msg);
            Win32.DispatchMessageW(&msg);
        }
    }

    private static IntPtr LoadIcon(string? path)
    {
        if (string.IsNullOrEmpty(path)) return IntPtr.Zero;
        IntPtr small = IntPtr.Zero;
        return Win32.ExtractIconExW(path, 0, null, &small, 1) > 0 ? small : IntPtr.Zero;
    }

    private bool AddIcon()
    {
        var data = NotifyData(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        return Win32.Shell_NotifyIconW(Win32.NIM_ADD, &data);
    }

    private Win32.NOTIFYICONDATAW NotifyData(uint flags)
    {
        var data = new Win32.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(Win32.NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        Win32.Copy(_options.Status, data.szTip, 128);
        return data;
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var tray = s_current;
        if (tray is null) return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
        try
        {
            switch (msg)
            {
                case CallbackMessage:
                    var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
                    if (mouse == Win32.WM_LBUTTONUP) tray._options.Open();
                    else if (mouse is Win32.WM_RBUTTONUP or Win32.WM_CONTEXTMENU) tray.ShowMenu();
                    return IntPtr.Zero;
                case Win32.WM_COMMAND:
                    tray.OnCommand((int)(wParam.ToInt64() & 0xFFFF));
                    return IntPtr.Zero;
                case Win32.WM_CLOSE:
                    tray.RemoveIcon();
                    Win32.DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case Win32.WM_DESTROY:
                    Win32.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            // Explorer restarted: the notification area is new and empty.
            if (msg == s_taskbarCreated && s_taskbarCreated != 0)
            {
                tray._shown = tray.AddIcon();
                return IntPtr.Zero;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An exception must not unwind into user32; the menu stays usable.
            System.Diagnostics.Trace.TraceWarning($"Bowire tray: {ex.Message}");
        }
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = Win32.CreatePopupMenu();
        try
        {
            Win32.AppendMenuW(menu, Win32.MF_STRING | Win32.MF_GRAYED, CmdStatus, _options.Status);
            Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, null);
            Win32.AppendMenuW(menu, Win32.MF_STRING, CmdOpen, _options.OpenLabel);
            if (_options.StartAtLogin is { } login)
                Win32.AppendMenuW(menu, Win32.MF_STRING | (login.IsEnabled ? Win32.MF_CHECKED : 0), CmdStartAtLogin, _options.StartAtLoginLabel);
            Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, null);
            Win32.AppendMenuW(menu, Win32.MF_STRING, CmdQuit, _options.QuitLabel);

            Win32.POINT pt;
            Win32.GetCursorPos(&pt);
            // Without the foreground the menu does not close when clicked away from.
            Win32.SetForegroundWindow(_hwnd);
            // Zero when the menu was dismissed; the choice arrives as WM_COMMAND.
            _ = Win32.TrackPopupMenuEx(menu, Win32.TPM_RIGHTBUTTON | Win32.TPM_BOTTOMALIGN, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    private void OnCommand(int command)
    {
        switch (command)
        {
            case CmdOpen:
                _options.Open();
                break;
            case CmdStartAtLogin when _options.StartAtLogin is { } login:
                login.Set(!login.IsEnabled);
                break;
            case CmdQuit:
                _options.Quit();
                break;
        }
    }

    private void RemoveIcon()
    {
        if (!_shown) return;
        var data = NotifyData(0);
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, &data);
        _shown = false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_hwnd != IntPtr.Zero && _thread.IsAlive)
        {
            Win32.PostMessageW(_hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _thread.Join();
        }
        if (_icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
        if (ReferenceEquals(s_current, this)) s_current = null;
        _ready.Dispose();
    }
}
