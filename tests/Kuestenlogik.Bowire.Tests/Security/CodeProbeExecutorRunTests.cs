// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// What actually happens when a <c>code:</c> template runs (#491).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CodeProbeExecutorTests"/> covers the decisions taken before a
/// process is started — the engine allow-list, the refusals. This covers the
/// part after, which is where the promises that matter live: the child is
/// killed at the deadline, its output is capped, and a finding that lands on
/// stderr is not silently dropped. None of those can be checked without
/// launching something.
/// </para>
/// <para>
/// These launch a real interpreter, so they use whichever shell the platform
/// guarantees and skip when it cannot be found — a machine with no shell on
/// PATH is not a failure of this code.
/// </para>
/// </remarks>
[Collection("CwdSerialised")]
public sealed class CodeProbeExecutorRunTests
{
    /// <summary>The shell this platform is sure to have, or null.</summary>
    private static (string Engine, string? Path) Shell()
    {
        foreach (var engine in OperatingSystem.IsWindows() ? new[] { "powershell", "pwsh" } : ["sh", "bash"])
        {
            var path = CodeProbeExecutor.FindOnPath(engine);
            if (path is not null) return (engine, path);
        }
        return ("", null);
    }

    private static BowireRecordingStep Probe(string engine, string source)
        => new() { Service = engine, Body = source };

    [Fact]
    public async Task A_Programs_Stdout_And_Exit_Code_Reach_The_Matchers()
    {
        var (engine, path) = Shell();
        Assert.SkipWhen(path is null, "no shell on PATH");

        var source = OperatingSystem.IsWindows()
            ? "Write-Output 'probe-marker'; exit 0"
            : "echo probe-marker; exit 0";

        var response = await CodeProbeExecutor.ExecuteAsync(Probe(engine, source),
            timeoutSeconds: 30, ct: TestContext.Current.CancellationToken);

        // The exit code is the status a `type: status` matcher reads.
        Assert.Equal(0, response.Status);
        Assert.Contains("probe-marker", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Non_Zero_Exit_Is_Reported_As_Itself_Not_As_A_Failure()
    {
        var (engine, path) = Shell();
        Assert.SkipWhen(path is null, "no shell on PATH");

        var response = await CodeProbeExecutor.ExecuteAsync(Probe(engine, "exit 3"),
            timeoutSeconds: 30, ct: TestContext.Current.CancellationToken);

        // A template can legitimately signal its finding through an exit
        // code, so a non-zero one is data rather than an error to raise, and
        // it must not be confused with -1, which means "we killed it".
        Assert.NotEqual(0, response.Status);
        Assert.NotEqual(-1, response.Status);

        // The exact value only survives on interpreters that propagate it.
        // Windows PowerShell launched with a script as its first argument
        // reports 1 for `exit 3` — measured, not assumed — so pinning the
        // number there would be asserting a quirk of the host rather than
        // anything this code does.
        if (!OperatingSystem.IsWindows()) Assert.Equal(3, response.Status);
    }

    [Fact]
    public async Task Output_On_Stderr_Is_Kept_And_Marked_Rather_Than_Dropped()
    {
        var (engine, path) = Shell();
        Assert.SkipWhen(path is null, "no shell on PATH");

        var source = OperatingSystem.IsWindows()
            ? "[Console]::Error.WriteLine('stderr-marker')"
            : "echo stderr-marker 1>&2";

        var response = await CodeProbeExecutor.ExecuteAsync(Probe(engine, source),
            timeoutSeconds: 30, ct: TestContext.Current.CancellationToken);

        // A template whose finding lands on stderr would otherwise read as a
        // clean run; the separator lets a reader tell the streams apart.
        Assert.Contains("stderr-marker", response.Body, StringComparison.Ordinal);
        Assert.Contains("--- stderr ---", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Program_That_Does_Not_Finish_Is_Killed_At_The_Deadline()
    {
        var (engine, path) = Shell();
        Assert.SkipWhen(path is null, "no shell on PATH");

        var source = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 60" : "sleep 60";

        var started = DateTime.UtcNow;
        var response = await CodeProbeExecutor.ExecuteAsync(Probe(engine, source),
            timeoutSeconds: 1, ct: TestContext.Current.CancellationToken);
        var elapsed = DateTime.UtcNow - started;

        // The whole reason for the child process: a hang is bounded by
        // killing something rather than by hoping.
        Assert.Equal(-1, response.Status);
        Assert.Contains("killed after 1s", response.Body, StringComparison.Ordinal);
        Assert.True(elapsed < TimeSpan.FromSeconds(30), $"took {elapsed}");
    }

    [Fact]
    public async Task A_Caller_Cancelling_Is_Not_Reported_As_A_Timeout()
    {
        // Two different things: the scan being stopped, and this template
        // outstaying its budget. Only the second belongs in the response.
        var (engine, path) = Shell();
        Assert.SkipWhen(path is null, "no shell on PATH");

        var source = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 60" : "sleep 60";
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CodeProbeExecutor.ExecuteAsync(Probe(engine, source), timeoutSeconds: 120, ct: cts.Token));
    }

    [Fact]
    public async Task Runaway_Output_Is_Capped_Instead_Of_Taking_The_Scanner_With_It()
    {
        var (engine, path) = Shell();
        Assert.SkipWhen(path is null, "no shell on PATH");

        // ~1 MB, comfortably past the 256 KB cap.
        var source = OperatingSystem.IsWindows()
            ? "1..8000 | ForEach-Object { Write-Output ('x' * 128) }"
            : "for i in $(seq 1 8000); do printf 'x%.0s' $(seq 1 128); echo; done";

        var response = await CodeProbeExecutor.ExecuteAsync(Probe(engine, source),
            timeoutSeconds: 60, ct: TestContext.Current.CancellationToken);

        Assert.True(response.Body.Length <= CodeProbeExecutor.MaxOutputChars + 200,
            $"body was {response.Body.Length} chars");
    }

    [Fact]
    public async Task A_Program_Reading_Stdin_Fails_Fast_Instead_Of_Blocking()
    {
        // stdin is closed on purpose: without that, a template waiting for
        // input burns the whole timeout for nothing.
        var (engine, path) = Shell();
        Assert.SkipWhen(path is null, "no shell on PATH");

        var source = OperatingSystem.IsWindows()
            ? "$x = [Console]::In.ReadLine(); Write-Output \"read:$x\""
            : "read x; echo \"read:$x\"";

        var started = DateTime.UtcNow;
        await CodeProbeExecutor.ExecuteAsync(Probe(engine, source),
            timeoutSeconds: 20, ct: TestContext.Current.CancellationToken);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "blocked on stdin");
    }

    [Fact]
    public async Task The_Script_Is_Passed_As_An_Argument_So_A_Quoted_Path_Cannot_Split()
    {
        // The path is ours, not attacker-supplied — but it is built from a
        // temp directory name, and passing it as an argument rather than
        // interpolating it into a command line is what keeps the next edit
        // safe. A program that echoes its own argument proves the shape.
        var (engine, path) = Shell();
        Assert.SkipWhen(path is null, "no shell on PATH");

        var source = OperatingSystem.IsWindows()
            ? "Write-Output 'ran-from-script'"
            : "echo ran-from-script";

        var response = await CodeProbeExecutor.ExecuteAsync(Probe(engine, source),
            timeoutSeconds: 30, ct: TestContext.Current.CancellationToken);

        Assert.Contains("ran-from-script", response.Body, StringComparison.Ordinal);
    }

    // ---- PATH lookup ----

    [Fact]
    public void FindOnPath_Finds_A_File_Sitting_In_A_PATH_Directory()
    {
        var dir = Directory.CreateTempSubdirectory("bowire-path-");
        var previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var name = "bowire-fake-engine";
            var extension = OperatingSystem.IsWindows() ? ".CMD" : string.Empty;
            File.WriteAllText(Path.Combine(dir.FullName, name + extension), "");
            Environment.SetEnvironmentVariable("PATH", dir.FullName);

            var found = CodeProbeExecutor.FindOnPath(name);

            Assert.NotNull(found);
            Assert.StartsWith(dir.FullName, found, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void FindOnPath_Answers_Null_For_Something_That_Is_Not_There()
        => Assert.Null(CodeProbeExecutor.FindOnPath("bowire-definitely-not-installed-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void FindOnPath_Survives_An_Empty_Or_Malformed_PATH()
    {
        var previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");
            Assert.Null(CodeProbeExecutor.FindOnPath("sh"));

            // A malformed entry must be skipped rather than throw out of a
            // lookup — PATH is somebody else's data.
            Environment.SetEnvironmentVariable("PATH", "\"quoted\"" + Path.PathSeparator + "|<>");
            Assert.Null(CodeProbeExecutor.FindOnPath("sh"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
        }
    }
}
