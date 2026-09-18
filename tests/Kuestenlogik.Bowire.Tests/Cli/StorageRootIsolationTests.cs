// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// <c>BOWIRE_DATA_DIR</c> moves what a CLI command reads and writes.
/// </summary>
/// <remarks>
/// <para>
/// Driven through the real executable rather than a handler, because what
/// broke was neither: <c>BowireStorageRoot.Apply</c> ran only when a host
/// was being built, so a bare subcommand resolved through the default store
/// and read <c>~/.bowire</c> whatever the variable said. Every test that
/// called a handler directly set the store itself and so could not have
/// caught it — the gap was in the process start-up, and only a process
/// shows it.
/// </para>
/// <para>
/// This is not a hypothetical: verifying the fix by hand turned up a file
/// in the developer's own <c>~/.bowire</c> that a test run had put there.
/// A run that sets the variable to isolate itself has to actually be
/// isolated.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class StorageRootIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-rootiso-" + Guid.NewGuid().ToString("N"));

    public StorageRootIsolationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "workspaces", "harbor"));
        File.WriteAllText(
            Path.Combine(_root, "workspaces.json"),
            """{"workspaces":[{"id":"harbor","name":"Harbour ops"}]}""");
        File.WriteAllText(
            Path.Combine(_root, "workspaces", "harbor", "flows.json"),
            """
            { "flows": [ { "id": "f1", "name": "Alpha", "nodes": [
              { "id": "n1", "type": "request", "service": "S", "method": "M", "body": "{}" } ] } ] }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_Command_Lists_The_Workspaces_Of_The_Store_It_Was_Pointed_At()
    {
        var (exit, stdout, stderr) = await RunAsync("workspace", "list");

        Assert.True(exit == 0, $"exit {exit}: {stderr}");
        Assert.Contains("harbor", stdout, StringComparison.Ordinal);
        Assert.Contains("Harbour ops", stdout, StringComparison.Ordinal);
        // The paths it prints have to be under the store it was given. A
        // single line naming the real profile directory is the whole bug.
        Assert.Contains(_root, stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_Run_Reaches_The_Flows_Of_That_Store()
    {
        // Past listing and into the file: the inventory and the artifacts
        // behind it resolve through different calls, and only one of them
        // used to be moved by the variable.
        var (exit, stdout, stderr) = await RunAsync("test", "--workspace-id", "harbor");

        Assert.True(exit == 0, $"exit {exit}: {stderr}");
        Assert.Contains("Alpha", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_Of_The_Real_Profile_Leaks_Into_The_Answer()
    {
        // The failure mode was not an error — it was the right shape of
        // answer about the wrong machine.
        var realProfile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bowire");

        var (_, stdout, _) = await RunAsync("workspace", "list");

        Assert.DoesNotContain(realProfile, stdout, StringComparison.OrdinalIgnoreCase);
    }

    // ---- harness ----

    /// <summary>
    /// Run the built <c>bowire</c> with this test's store, and hand back what
    /// it said.
    /// </summary>
    private async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var exe = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "bowire.exe" : "bowire");
        Assert.SkipUnless(File.Exists(exe), $"no bowire executable beside the tests at {exe}");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Not the repo: a project manifest discovered from the working
            // directory outranks nothing here, but it is one more input the
            // test has no reason to take.
            WorkingDirectory = _root,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["BOWIRE_DATA_DIR"] = _root;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for bowire.");

        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return (process.ExitCode, await stdout, await stderr);
    }

}
