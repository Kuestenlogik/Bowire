// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
///
/// <para>The two OS-integration surfaces — the CLI shell-out and the Windows
/// P/Invoke — sit behind injectable seams (<c>ProcessRunner</c> and
/// <c>WindowsCredReader</c>) so the dispatch + result-mapping logic is
/// unit-testable on any host with a fake; the public constructor wires the
/// real OS implementations.</para>
/// </summary>
public sealed class OsKeyringBackend : IKeyringBackend
{
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(5);

    private readonly ProcessRunner _runProcess;
    private readonly WindowsCredReader _readWindowsCred;

    /// <inheritdoc />
    public string BackendId { get; }

    /// <summary>
    /// Build a backend, honouring an explicit <see cref="KeyringOptions.Backend"/>
    /// override and otherwise auto-selecting by OS.
    /// </summary>
    public OsKeyringBackend(string backendOverride = "auto")
        : this(backendOverride, DefaultRunProcess, DefaultReadWindowsCredential)
    {
    }

    /// <summary>
    /// Test seam: inject fakes for the CLI shell-out and the Windows credential
    /// read so the per-backend dispatch, exit-code / error mapping, and blob
    /// decode can be exercised on any OS.
    /// </summary>
    internal OsKeyringBackend(string backendOverride, ProcessRunner runProcess, WindowsCredReader readWindowsCred)
    {
        BackendId = ResolveBackendId(backendOverride);
        _runProcess = runProcess;
        _readWindowsCred = readWindowsCred;
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
    public KeyringReadResult Read(KeyringReference reference) => BackendId switch
    {
        "wincred" => ReadWindows(reference),
        "keychain" => ReadMacKeychain(reference),
        "secret-tool" => ReadSecretTool(reference),
        _ => KeyringReadResult.Failed("no OS credential store available on this platform"),
    };

    // ---- macOS Keychain -------------------------------------------------

    private KeyringReadResult ReadMacKeychain(KeyringReference reference)
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

    private KeyringReadResult ReadSecretTool(KeyringReference reference)
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

    private KeyringReadResult RunCli(
        string fileName, IReadOnlyList<string> args, int missExitCode, bool trimTrailingNewline)
    {
        ProcessOutput output;
        try
        {
            output = _runProcess(fileName, args);
        }
        catch (Win32Exception ex)
        {
            // Tool not installed / not on PATH.
            return KeyringReadResult.Failed($"{fileName} not available: {ex.Message}");
        }
        catch (TimeoutException)
        {
            return KeyringReadResult.Failed($"{fileName} timed out");
        }
        catch (InvalidOperationException)
        {
            return KeyringReadResult.Failed($"{fileName} failed to start");
        }

        if (output.ExitCode == 0)
        {
            var value = trimTrailingNewline ? output.StdOut.TrimEnd('\r', '\n') : output.StdOut;
            return KeyringReadResult.Found(value);
        }
        if (output.ExitCode == missExitCode) return KeyringReadResult.NotFound();
        return KeyringReadResult.Failed($"{fileName} exited {output.ExitCode}");
    }

    /// <summary>The result of a CLI shell-out: the exit code and captured stdout.</summary>
    internal readonly record struct ProcessOutput(int ExitCode, string StdOut);

    /// <summary>
    /// Run a process and return its exit code + stdout. Throws
    /// <see cref="Win32Exception"/> when the tool isn't installed,
    /// <see cref="TimeoutException"/> on timeout, and
    /// <see cref="InvalidOperationException"/> when the process can't start.
    /// </summary>
    internal delegate ProcessOutput ProcessRunner(string fileName, IReadOnlyList<string> args);

    internal static ProcessOutput DefaultRunProcess(string fileName, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = Process.Start(psi) ?? throw new InvalidOperationException($"{fileName} failed to start");
        using (proc)
        {
            var stdout = proc.StandardOutput.ReadToEnd();
            _ = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(CliTimeout))
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
                throw new TimeoutException($"{fileName} timed out");
            }
            return new ProcessOutput(proc.ExitCode, stdout);
        }
    }

    // ---- Windows Credential Manager ------------------------------------

    private KeyringReadResult ReadWindows(KeyringReference reference)
    {
        // Generic credentials are keyed by target name only. We use the
        // service as the target; when an account is supplied we also accept
        // the "service/account" composite so a user can store per-account
        // entries under distinct target names.
        var targets = reference.Account is null
            ? new[] { reference.Service }
            : new[] { $"{reference.Service}/{reference.Account}", reference.Service };

        WinCredResult last = default;
        foreach (var target in targets)
        {
            last = _readWindowsCred(target);
            switch (last.Outcome)
            {
                case WinCredOutcome.Found:
                    return KeyringReadResult.Found(
                        last.Blob is { Length: > 0 } b ? DecodeWindowsBlob(b) : string.Empty);
                case WinCredOutcome.Unavailable:
                    return KeyringReadResult.Failed("no OS credential store available on this platform");
            }
        }

        // ERROR_NOT_FOUND (1168) is the expected miss; anything else is a
        // real failure (access denied, etc.).
        return last.Outcome == WinCredOutcome.Error
            ? KeyringReadResult.Failed($"CredRead failed (Win32 error {last.ErrorCode})")
            : KeyringReadResult.NotFound();
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

    /// <summary>Per-target outcome of a Windows credential read.</summary>
    internal enum WinCredOutcome
    {
        /// <summary>The target credential was read (blob may be empty).</summary>
        Found,
        /// <summary>The target doesn't exist (ERROR_NOT_FOUND).</summary>
        NotFound,
        /// <summary>A real read failure (access denied, etc.) — see ErrorCode.</summary>
        Error,
        /// <summary>The Windows store isn't available on this OS (wincred forced elsewhere).</summary>
        Unavailable,
    }

    /// <summary>Result of reading one target from the Windows credential store.</summary>
    internal readonly record struct WinCredResult(WinCredOutcome Outcome, byte[]? Blob, int ErrorCode);

    /// <summary>Read one target name from the Windows credential store.</summary>
    internal delegate WinCredResult WindowsCredReader(string target);

    internal static WinCredResult DefaultReadWindowsCredential(string target)
        => OperatingSystem.IsWindows()
            ? ReadWindowsNative(target)
            : new WinCredResult(WinCredOutcome.Unavailable, null, 0);

    // Windows-native credential read (advapi32 CredReadW). Excluded from
    // coverage: the P/Invoke can only execute on Windows, but the coverage
    // CI runs on Linux, so these lines are structurally unreachable there.
    // The target iteration, blob decode, and result mapping around it live
    // in ReadWindows / DecodeWindowsBlob and are unit-tested via the seam.
    [ExcludeFromCodeCoverage]
    [SupportedOSPlatform("windows")]
    private static WinCredResult ReadWindowsNative(string target)
    {
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var handle))
        {
            var err = Marshal.GetLastPInvokeError();
            return new WinCredResult(
                err is 0 or ERROR_NOT_FOUND ? WinCredOutcome.NotFound : WinCredOutcome.Error, null, err);
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
            byte[]? blob = null;
            if (cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero)
            {
                blob = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, blob, 0, cred.CredentialBlobSize);
            }
            return new WinCredResult(WinCredOutcome.Found, blob, 0);
        }
        finally
        {
            CredFree(handle);
        }
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

    [ExcludeFromCodeCoverage] // Windows-native interop struct — see ReadWindowsNative.
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
