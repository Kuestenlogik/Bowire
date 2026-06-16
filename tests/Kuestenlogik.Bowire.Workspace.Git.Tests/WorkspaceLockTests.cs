// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Workspace.Git;

namespace Kuestenlogik.Bowire.Workspace.Git.Tests;

/// <summary>
/// Behavioural coverage for <see cref="WorkspaceLock"/> — the cross-
/// process workspace lockfile shipped in #196 Phase 2.5. Covers the
/// happy-path acquire/release, the active-holder collision, the
/// metadata round-trip, and the stale-lock recovery path.
/// </summary>
public sealed class WorkspaceLockTests : IDisposable
{
    private readonly string _root;

    public WorkspaceLockTests()
    {
        _root = Directory.CreateTempSubdirectory("bowire-workspace-lock-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task AcquireAsync_OnEmptyRoot_CreatesLockFile()
    {
        await using var heldLock = await WorkspaceLock.AcquireAsync(_root, "test",
            TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(_root, WorkspaceLock.FileName)));
    }

    [Fact]
    public async Task Dispose_RemovesLockFile_SoSecondAcquireSucceeds()
    {
        await using (await WorkspaceLock.AcquireAsync(_root, "first",
            TestContext.Current.CancellationToken))
        {
            // Holder lives here.
        }
        Assert.False(File.Exists(Path.Combine(_root, WorkspaceLock.FileName)));

        // Second acquire after release succeeds.
        await using var second = await WorkspaceLock.AcquireAsync(_root, "second",
            TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(_root, WorkspaceLock.FileName)));
    }

    [Fact]
    public async Task AcquireAsync_While_Held_Throws_LockHeldException_With_Holder_Metadata()
    {
        await using var first = await WorkspaceLock.AcquireAsync(_root, "workbench",
            TestContext.Current.CancellationToken);

        var thrown = await Assert.ThrowsAsync<LockHeldException>(() =>
            WorkspaceLock.AcquireAsync(_root, "cli",
                TestContext.Current.CancellationToken));
        Assert.NotNull(thrown.Holder);
        Assert.Equal("workbench", thrown.Holder!.Value.Writer);
        Assert.Equal(Environment.ProcessId, thrown.Holder.Value.Pid);
        Assert.Equal(Environment.MachineName, thrown.Holder.Value.Host);
    }

    [Fact]
    public async Task Inspect_Returns_Holder_While_Held_And_Null_After_Release()
    {
        var lockHandle = await WorkspaceLock.AcquireAsync(_root, "inspector-test",
            TestContext.Current.CancellationToken);
        try
        {
            var snap = WorkspaceLock.Inspect(_root);
            Assert.NotNull(snap);
            Assert.Equal("inspector-test", snap!.Value.Writer);
        }
        finally
        {
            await lockHandle.DisposeAsync();
        }

        Assert.Null(WorkspaceLock.Inspect(_root));
    }

    [Fact]
    public async Task AcquireAsync_With_Missing_Root_Throws_ArgumentException()
    {
        var bogus = Path.Combine(_root, "subdir-that-does-not-exist");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            WorkspaceLock.AcquireAsync(bogus, "test",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcquireAsync_StaleLock_From_Dead_Pid_Is_Recovered()
    {
        // Hand-craft a lock file that names a pid we know to be dead
        // — int.MaxValue is past the OS pid space on every platform.
        var lockPath = Path.Combine(_root, WorkspaceLock.FileName);
        await File.WriteAllTextAsync(lockPath,
            """{"pid":2147483647,"writer":"ghost","host":"phantom","startedAtUtc":"2020-01-01T00:00:00Z"}""",
            TestContext.Current.CancellationToken);

        // Acquire should take it over via the stale-recover path
        // rather than throw LockHeldException.
        await using var recovered = await WorkspaceLock.AcquireAsync(_root, "recovery",
            TestContext.Current.CancellationToken);
        var snap = WorkspaceLock.Inspect(_root);
        Assert.NotNull(snap);
        Assert.Equal("recovery", snap!.Value.Writer);
        Assert.Equal(Environment.ProcessId, snap.Value.Pid);
    }
}
