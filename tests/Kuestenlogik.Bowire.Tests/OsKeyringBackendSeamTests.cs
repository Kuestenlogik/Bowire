// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Text;
using Kuestenlogik.Bowire.Keyring;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Coverage for <see cref="OsKeyringBackend"/>'s OS-store dispatch and
/// result-mapping via its injectable seams — the CLI shell-out
/// (<c>ProcessRunner</c>, used by keychain / secret-tool) and the Windows
/// credential read (<c>WindowsCredReader</c>). Fakes stand in for the real OS
/// integration so every hit / miss / error branch is exercised on any host.
/// </summary>
public sealed class OsKeyringBackendSeamTests
{
    private static readonly OsKeyringBackend.WindowsCredReader NoWindows =
        _ => new OsKeyringBackend.WinCredResult(OsKeyringBackend.WinCredOutcome.Unavailable, null, 0);

    private static readonly OsKeyringBackend.ProcessRunner NoProcess =
        (_, _) => throw new InvalidOperationException("process runner should not be called");

    private static OsKeyringBackend Cli(string backend, OsKeyringBackend.ProcessRunner runner)
        => new(backend, runner, NoWindows);

    private static OsKeyringBackend Win(OsKeyringBackend.WindowsCredReader reader)
        => new("wincred", NoProcess, reader);

    // ---- macOS keychain ----

    [Fact]
    public void Keychain_Hit_TrimsTrailingNewline()
    {
        var backend = Cli("keychain", (_, _) => new OsKeyringBackend.ProcessOutput(0, "s3cret\n"));
        var r = backend.Read(new KeyringReference("svc", null));
        Assert.Equal(KeyringReadStatus.Found, r.Status);
        Assert.Equal("s3cret", r.Value);
    }

    [Fact]
    public void Keychain_Miss_ExitCode44_IsNotFound()
    {
        var backend = Cli("keychain", (_, _) => new OsKeyringBackend.ProcessOutput(44, ""));
        Assert.Equal(KeyringReadStatus.NotFound, backend.Read(new KeyringReference("svc", null)).Status);
    }

    [Fact]
    public void Keychain_OtherNonZeroExit_IsError()
    {
        var backend = Cli("keychain", (_, _) => new OsKeyringBackend.ProcessOutput(2, ""));
        var r = backend.Read(new KeyringReference("svc", null));
        Assert.Equal(KeyringReadStatus.Error, r.Status);
        Assert.Contains("exited 2", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Keychain_PassesAccountArgs()
    {
        List<string>? captured = null;
        var backend = Cli("keychain", (_, args) => { captured = args.ToList(); return new OsKeyringBackend.ProcessOutput(0, "x"); });
        backend.Read(new KeyringReference("svc", "alice"));
        Assert.NotNull(captured);
        Assert.Equal(["find-generic-password", "-s", "svc", "-a", "alice", "-w"], captured);
    }

    [Fact]
    public void Cli_ToolNotInstalled_IsError()
    {
        var backend = Cli("secret-tool", (_, _) => throw new Win32Exception("No such file or directory"));
        var r = backend.Read(new KeyringReference("svc", null));
        Assert.Equal(KeyringReadStatus.Error, r.Status);
        Assert.Contains("not available", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Timeout_IsError()
    {
        var backend = Cli("keychain", (_, _) => throw new TimeoutException());
        Assert.Contains("timed out", backend.Read(new KeyringReference("svc", null)).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_StartFailure_IsError()
    {
        var backend = Cli("keychain", (_, _) => throw new InvalidOperationException());
        Assert.Contains("failed to start", backend.Read(new KeyringReference("svc", null)).Error, StringComparison.Ordinal);
    }

    // ---- Linux secret-tool ----

    [Fact]
    public void SecretTool_Hit_KeepsRawValue()
    {
        var backend = Cli("secret-tool", (_, _) => new OsKeyringBackend.ProcessOutput(0, "no-newline-secret"));
        var r = backend.Read(new KeyringReference("svc", "acct"));
        Assert.Equal(KeyringReadStatus.Found, r.Status);
        Assert.Equal("no-newline-secret", r.Value);
    }

    [Fact]
    public void SecretTool_Miss_ExitCode1_IsNotFound()
    {
        var backend = Cli("secret-tool", (_, _) => new OsKeyringBackend.ProcessOutput(1, ""));
        Assert.Equal(KeyringReadStatus.NotFound, backend.Read(new KeyringReference("svc", null)).Status);
    }

    // ---- Windows credential store ----

    [Fact]
    public void Windows_Utf16Blob_Decoded()
    {
        var blob = Encoding.Unicode.GetBytes("p@ss");
        var backend = Win(_ => new OsKeyringBackend.WinCredResult(OsKeyringBackend.WinCredOutcome.Found, blob, 0));
        var r = backend.Read(new KeyringReference("svc", null));
        Assert.Equal(KeyringReadStatus.Found, r.Status);
        Assert.Equal("p@ss", r.Value);
    }

    [Fact]
    public void Windows_OddLengthBlob_DecodedAsUtf8()
    {
        // An odd byte count can't be UTF-16LE → the decoder falls back to UTF-8.
        var blob = Encoding.UTF8.GetBytes("abc"); // 3 bytes
        var backend = Win(_ => new OsKeyringBackend.WinCredResult(OsKeyringBackend.WinCredOutcome.Found, blob, 0));
        Assert.Equal("abc", backend.Read(new KeyringReference("svc", null)).Value);
    }

    [Fact]
    public void Windows_EmptyBlob_IsFoundEmpty()
    {
        var backend = Win(_ => new OsKeyringBackend.WinCredResult(OsKeyringBackend.WinCredOutcome.Found, null, 0));
        var r = backend.Read(new KeyringReference("svc", null));
        Assert.Equal(KeyringReadStatus.Found, r.Status);
        Assert.Equal(string.Empty, r.Value);
    }

    [Fact]
    public void Windows_NotFound_IsNotFound()
    {
        var backend = Win(_ => new OsKeyringBackend.WinCredResult(OsKeyringBackend.WinCredOutcome.NotFound, null, 1168));
        Assert.Equal(KeyringReadStatus.NotFound, backend.Read(new KeyringReference("svc", null)).Status);
    }

    [Fact]
    public void Windows_ReadError_SurfacesWin32Code()
    {
        var backend = Win(_ => new OsKeyringBackend.WinCredResult(OsKeyringBackend.WinCredOutcome.Error, null, 5));
        var r = backend.Read(new KeyringReference("svc", null));
        Assert.Equal(KeyringReadStatus.Error, r.Status);
        Assert.Contains("Win32 error 5", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_Unavailable_ReportsNoStore()
    {
        var backend = Win(NoWindows);
        var r = backend.Read(new KeyringReference("svc", null));
        Assert.Equal(KeyringReadStatus.Error, r.Status);
        Assert.Contains("no OS credential store", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_AccountCompositeTargetTriedFirst_ThenFallsBack()
    {
        var tried = new List<string>();
        var backend = Win(target =>
        {
            tried.Add(target);
            // First (composite) target misses; the bare service holds the value.
            return target.Contains('/', StringComparison.Ordinal)
                ? new OsKeyringBackend.WinCredResult(OsKeyringBackend.WinCredOutcome.NotFound, null, 1168)
                : new OsKeyringBackend.WinCredResult(OsKeyringBackend.WinCredOutcome.Found, Encoding.Unicode.GetBytes("v"), 0);
        });

        var r = backend.Read(new KeyringReference("svc", "alice"));
        Assert.Equal("v", r.Value);
        Assert.Equal(["svc/alice", "svc"], tried);
    }

    // ---- dispatch ----

    [Fact]
    public void NoneBackend_AlwaysFails()
    {
        var backend = new OsKeyringBackend("none", NoProcess, NoWindows);
        var r = backend.Read(new KeyringReference("svc", null));
        Assert.Equal(KeyringReadStatus.Error, r.Status);
    }

    // ---- default OS implementations ----

    [Fact]
    public void DefaultRunProcess_RealCommand_ReturnsExitCodeAndStdout()
    {
        // `dotnet --version` is on PATH wherever these tests run and exits 0.
        var output = OsKeyringBackend.DefaultRunProcess("dotnet", ["--version"]);
        Assert.Equal(0, output.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(output.StdOut));
    }

    [Fact]
    public void DefaultRunProcess_MissingCommand_ThrowsWin32()
    {
        Assert.Throws<Win32Exception>(
            () => OsKeyringBackend.DefaultRunProcess("bowire-no-such-tool-x9z", []));
    }

    [Fact]
    public void DefaultReadWindowsCredential_MatchesHostPlatform()
    {
        var result = OsKeyringBackend.DefaultReadWindowsCredential("bowire-no-such-target-x9z");
        if (OperatingSystem.IsWindows())
        {
            // A target that (almost certainly) doesn't exist → a clean miss.
            Assert.NotEqual(OsKeyringBackend.WinCredOutcome.Found, result.Outcome);
        }
        else
        {
            Assert.Equal(OsKeyringBackend.WinCredOutcome.Unavailable, result.Outcome);
        }
    }
}
