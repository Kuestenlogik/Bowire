// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Kuestenlogik.Bowire.Keyring;

/// <summary>
/// The one shipped <see cref="IKeyringBackend"/> — reads the running OS's
/// native credential store with zero NuGet dependencies:
/// <list type="bullet">
///   <item><b>Windows</b> — <c>CredReadW</c> P/Invoke against
///     <c>advapi32.dll</c>, target name <c>service</c> (generic
///     credential). The password blob is decoded UTF-16LE, matching the
///     convention used by PowerShell's <c>CredentialManager</c> module,
///     node-keytar, and Python's <c>keyring</c>.</item>
///   <item><b>macOS</b> — <c>security find-generic-password -s service
///     [-a account] -w</c>, the value printed on stdout.</item>
///   <item><b>Linux</b> — <c>secret-tool lookup service &lt;service&gt;
///     [account &lt;account&gt;]</c> (libsecret), the value printed on
///     stdout with no trailing newline.</item>
/// </list>
/// The <see cref="KeyringOptions.Backend"/> override forces one of these
/// ids; the default <c>auto</c> selects by <see cref="OperatingSystem"/>.
/// </summary>
public sealed class OsKeyringBackend : IKeyringBackend
{
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public string BackendId { get; }

    /// <summary>
    /// Build a backend, honouring an explicit <see cref="KeyringOptions.Backend"/>
    /// override and otherwise auto-selecting by OS.
    /// </summary>
    public OsKeyringBackend(string backendOverride = "auto")
    {
        BackendId = ResolveBackendId(backendOverride);
    }

    private static string ResolveBackendId(string backendOverride)
    {
        var forced = (backendOverride ?? "auto").Trim();
        // Explicit override wins; comparison is case-insensitive but the
        // canonical id we return is always the lowercase literal.
        if (forced.Equals("wincred", StringComparison.OrdinalIgnoreCase)) return "wincred";
        if (forced.Equals("keychain", StringComparison.OrdinalIgnoreCase)) return "keychain";
        if (forced.Equals("secret-tool", StringComparison.OrdinalIgnoreCase)) return "secret-tool";
        if (forced.Equals("none", StringComparison.OrdinalIgnoreCase)) return "none";
        // Anything else (including "auto" and unknown values) falls through
        // to the OS pick.
        if (OperatingSystem.IsWindows()) return "wincred";
        if (OperatingSystem.IsMacOS()) return "keychain";
        if (OperatingSystem.IsLinux()) return "secret-tool";
        return "none";
    }

    /// <inheritdoc />
    public KeyringReadResult Read(KeyringReference reference)
    {
        // The IsWindows() guard is what makes the [SupportedOSPlatform]
        // ReadWindows call site provably reachable only on Windows — a
        // "wincred" backend forced onto another OS falls through to the
        // "no store" result instead of P/Invoking advapi32.
        if (BackendId == "wincred" && OperatingSystem.IsWindows()) return ReadWindows(reference);
        return BackendId switch
        {
            "keychain" => ReadMacKeychain(reference),
            "secret-tool" => ReadSecretTool(reference),
            _ => KeyringReadResult.Failed("no OS credential store available on this platform"),
        };
    }

    // ---- macOS Keychain -------------------------------------------------

    private static KeyringReadResult ReadMacKeychain(KeyringReference reference)
    {
        var args = new List<string> { "find-generic-password", "-s", reference.Service };
        if (reference.Account is not null)
        {
            args.Add("-a");
            args.Add(reference.Account);
        }
        args.Add("-w");
        // `security` exits 44 (errSecItemNotFound) on a clean miss; any
        // other non-zero is a genuine failure worth surfacing.
        return RunCli("security", args, missExitCode: 44,
            // -w prints the password followed by a newline the shell adds.
            trimTrailingNewline: true);
    }

    // ---- Linux libsecret ------------------------------------------------

    private static KeyringReadResult ReadSecretTool(KeyringReference reference)
    {
        var args = new List<string> { "lookup", "service", reference.Service };
        if (reference.Account is not null)
        {
            args.Add("account");
            args.Add(reference.Account);
        }
        // secret-tool exits 1 with empty stdout on a miss and prints the
        // secret with NO trailing newline on a hit.
        return RunCli("secret-tool", args, missExitCode: 1, trimTrailingNewline: false);
    }

    private static KeyringReadResult RunCli(
        string fileName, IReadOnlyList<string> args, int missExitCode, bool trimTrailingNewline)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            // Tool not installed / not on PATH.
            return KeyringReadResult.Failed($"{fileName} not available: {ex.Message}");
        }
        if (proc is null) return KeyringReadResult.Failed($"{fileName} failed to start");

        using (proc)
        {
            var stdout = proc.StandardOutput.ReadToEnd();
            _ = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(CliTimeout))
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
                return KeyringReadResult.Failed($"{fileName} timed out");
            }

            if (proc.ExitCode == 0)
            {
                var value = trimTrailingNewline ? stdout.TrimEnd('\r', '\n') : stdout;
                return KeyringReadResult.Found(value);
            }
            if (proc.ExitCode == missExitCode) return KeyringReadResult.NotFound();
            return KeyringReadResult.Failed($"{fileName} exited {proc.ExitCode}");
        }
    }

    // ---- Windows Credential Manager ------------------------------------

    [SupportedOSPlatform("windows")]
    private static KeyringReadResult ReadWindows(KeyringReference reference)
    {
        // Generic credentials are keyed by target name only. We use the
        // service as the target; when an account is supplied we also accept
        // the "service/account" composite so a user can store per-account
        // entries under distinct target names.
        var targets = reference.Account is null
            ? new[] { reference.Service }
            : new[] { $"{reference.Service}/{reference.Account}", reference.Service };

        foreach (var target in targets)
        {
            if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var handle)) continue;
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
                if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
                {
                    return KeyringReadResult.Found(string.Empty);
                }
                var blob = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, blob, 0, cred.CredentialBlobSize);
                return KeyringReadResult.Found(DecodeWindowsBlob(blob));
            }
            finally
            {
                CredFree(handle);
            }
        }

        // ERROR_NOT_FOUND (1168) is the expected miss; anything else is a
        // real failure (access denied, etc.).
        var err = Marshal.GetLastPInvokeError();
        return err is 0 or ERROR_NOT_FOUND
            ? KeyringReadResult.NotFound()
            : KeyringReadResult.Failed($"CredRead failed (Win32 error {err})");
    }

    private static string DecodeWindowsBlob(byte[] blob)
    {
        // Credential Manager stores generic blobs as UTF-16LE by
        // convention (PowerShell CredentialManager, node-keytar, Python
        // keyring). Odd length can't be UTF-16, so fall back to UTF-8.
        var text = blob.Length % 2 == 0
            ? Encoding.Unicode.GetString(blob)
            : Encoding.UTF8.GetString(blob);
        return text.TrimEnd('\0');
    }

    private const int CRED_TYPE_GENERIC = 1;
    private const int ERROR_NOT_FOUND = 1168;

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
