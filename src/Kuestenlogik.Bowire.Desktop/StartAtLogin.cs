// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Kuestenlogik.Bowire.Desktop;

/// <summary>
/// #687 — "Start at login": one value under the user's own Run key, so it
/// needs no elevation and touches nobody else's login. Off unless the user
/// ticks it; ticking it again removes the value.
/// </summary>
/// <param name="valueName">The value's name under the key, e.g. <c>Bowire</c>.</param>
/// <param name="command">What Windows runs at login: the windowed launcher, quoted.</param>
/// <param name="keyPath">The key under HKEY_CURRENT_USER; tests pass a scratch key.</param>
[SupportedOSPlatform("windows")]
public sealed class StartAtLogin(string valueName, string command, string keyPath = StartAtLogin.RunKey)
{
    /// <summary>Where Windows looks for what to start when this user logs in.</summary>
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Whether the value is there, and names this command.</summary>
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName) is string value && string.Equals(value, command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Add or remove the value.</summary>
    public void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        if (enabled) key.SetValue(valueName, command, RegistryValueKind.String);
        else key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
