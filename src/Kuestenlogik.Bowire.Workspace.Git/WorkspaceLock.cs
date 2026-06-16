// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Workspace.Git;

/// <summary>
/// Cross-process workspace lockfile — guards concurrent writers
/// against corrupting the per-entity layout. One holder per
/// <c>storageRoot</c>; second writer hitting an active lock surfaces
/// the friendly "another process is editing this workspace" hint
/// instead of overwriting state.
/// </summary>
/// <remarks>
/// <para>
/// On <see cref="AcquireAsync"/> the lock file lives at
/// <c>&lt;storageRoot&gt;/.bowire.lock</c> as a small JSON blob with
/// <c>pid</c>, <c>writer</c>, <c>host</c>, <c>startedAtUtc</c>. The
/// <see cref="FileStream"/> stays open for the lifetime of the lock
/// (share = Read) so a second process can READ the metadata but not
/// delete or replace the file.
/// </para>
/// <para>
/// Stale-lock recovery: if an existing lock's pid no longer maps to a
/// live process (the holder crashed) the acquirer takes over —
/// safety-net for "I rebooted in the middle of a save". Within the
/// same process, two writers serialize on the OS file-handle: the
/// second hits <see cref="IOException"/> immediately because
/// <see cref="FileMode.CreateNew"/> refuses to clobber.
/// </para>
/// </remarks>
public sealed class WorkspaceLock : IAsyncDisposable, IDisposable
{
    /// <summary>File name of the per-workspace lock at the root.</summary>
    public const string FileName = ".bowire.lock";

    private readonly string _path;
    private readonly FileStream _handle;
    private int _disposed;

    private WorkspaceLock(string path, FileStream handle)
    {
        _path = path;
        _handle = handle;
    }

    /// <summary>
    /// Metadata for the writer currently holding the lock. Populated
    /// in <see cref="LockHeldException"/> so the caller can surface a
    /// friendly diagnostic without re-reading the file.
    /// </summary>
    /// <summary>
    /// Acquire the workspace lock or throw
    /// <see cref="LockHeldException"/> when another live process owns
    /// it. A stale lock (pid no longer maps to a process) is taken
    /// over silently.
    /// </summary>
    /// <param name="storageRoot">Absolute path to the workspace root.</param>
    /// <param name="writerName">Holder tag — short identifier of the calling subsystem.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="storageRoot"/> is null / whitespace / missing.
    /// </exception>
    /// <exception cref="LockHeldException">
    /// Another live process already holds the lock.
    /// </exception>
    public static async Task<WorkspaceLock> AcquireAsync(string storageRoot, string writerName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(writerName);

        var root = Path.GetFullPath(storageRoot);
        if (!Directory.Exists(root))
        {
            throw new ArgumentException(
                $"Workspace storageRoot '{storageRoot}' does not exist or is not a directory.",
                nameof(storageRoot));
        }
        var path = Path.Combine(root, FileName);

        // Loop once for stale-lock recovery: if the existing lock's
        // holder is gone we delete + retry. A genuinely live holder
        // surfaces LockHeldException without retry.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                // CreateNew + FileShare.Read: atomically claim the
                // lock vs concurrent acquirers, and let other
                // processes READ the metadata (but not overwrite).
                var handle = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read);
                try
                {
                    var info = new WorkspaceLockInfo(
                        Environment.ProcessId,
                        writerName,
                        Environment.MachineName,
                        DateTimeOffset.UtcNow);
                    var json = JsonSerializer.SerializeToUtf8Bytes(info, s_jsonOpts);
                    await handle.WriteAsync(json, ct).ConfigureAwait(false);
                    await handle.FlushAsync(ct).ConfigureAwait(false);
                    return new WorkspaceLock(path, handle);
                }
                catch
                {
                    await handle.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (IOException) when (attempt == 0 && IsHolderStale(path, out _))
            {
                // Stale lock — holder crashed. Take it over.
                try { File.Delete(path); }
                catch (IOException) { /* race with another stale-recover; just retry */ }
                continue;
            }
            catch (IOException)
            {
                // Active holder OR a transient I/O failure. Read the
                // metadata to surface a meaningful diagnostic.
                throw new LockHeldException(
                    "Workspace lock is held by another process — refusing to write.",
                    TryReadInfo(path));
            }
        }
        // Loop budget exhausted (shouldn't happen — both branches
        // either return or throw).
        throw new LockHeldException(
            "Workspace lock acquisition failed.",
            TryReadInfo(path));
    }

    /// <summary>
    /// Read the current holder's metadata without acquiring the lock.
    /// Returns <c>null</c> when no lock is present, the file is
    /// unreadable, or the JSON is malformed. Useful for diagnostics
    /// in the CLI (e.g. <c>bowire workspace status</c>).
    /// </summary>
    public static WorkspaceLockInfo? Inspect(string storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        var path = Path.Combine(Path.GetFullPath(storageRoot), FileName);
        return TryReadInfo(path);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _handle.Dispose(); } catch { /* ignore */ }
        try { File.Delete(_path); } catch { /* best-effort — the holder is gone, stale-recovery will clean up */ }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // -------------------------------------------------------------------

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static bool IsHolderStale(string path, out WorkspaceLockInfo? info)
    {
        info = TryReadInfo(path);
        if (info is null) return false;
        try
        {
            using var _ = Process.GetProcessById(info.Value.Pid);
            return false;
        }
        catch (ArgumentException)
        {
            // No such pid — holder is gone.
            return true;
        }
        catch (InvalidOperationException)
        {
            // Process exited between GetProcessById's lookup + handle.
            return true;
        }
    }

    private static WorkspaceLockInfo? TryReadInfo(string path)
    {
        try
        {
            // FileShare.ReadWrite so we don't block a holder still
            // writing the body.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            return JsonSerializer.Deserialize<WorkspaceLockInfo>(stream, s_jsonOpts);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Snapshot of a workspace lock holder's metadata — populated when
/// <see cref="WorkspaceLock.AcquireAsync"/> hits an active lock and
/// surfaced on <see cref="LockHeldException.Holder"/>, plus returned
/// from <see cref="WorkspaceLock.Inspect"/> for diagnostic CLIs.
/// </summary>
/// <param name="Pid">OS process id of the holder.</param>
/// <param name="Writer">Free-form holder tag (e.g. <c>workbench</c>, <c>cli</c>).</param>
/// <param name="Host">Machine name the holder reported via <see cref="Environment.MachineName"/>.</param>
/// <param name="StartedAtUtc">When the holder acquired the lock.</param>
public readonly record struct WorkspaceLockInfo(int Pid, string Writer, string Host, DateTimeOffset StartedAtUtc);

/// <summary>
/// Thrown by <see cref="WorkspaceLock.AcquireAsync"/> when another
/// live writer holds the workspace lock. Carries the holder's
/// metadata so the caller can surface a friendly "process &lt;pid&gt;
/// on &lt;host&gt; started at &lt;timestamp&gt;" diagnostic without
/// re-reading the file.
/// </summary>
[System.Serializable]
public sealed class LockHeldException : InvalidOperationException
{
    /// <summary>Holder metadata, when readable; <c>null</c> when the file was unreadable.</summary>
    public WorkspaceLockInfo? Holder { get; }

    /// <summary>Create the exception with a message + (optional) holder metadata.</summary>
    public LockHeldException(string message, WorkspaceLockInfo? holder)
        : base(message)
    {
        Holder = holder;
    }

    /// <summary>Parameterless constructor required by the analyzer for serializable exceptions.</summary>
    public LockHeldException() { }

    /// <summary>Create with just a message.</summary>
    public LockHeldException(string message) : base(message) { }

    /// <summary>Create with a message and an inner exception.</summary>
    public LockHeldException(string message, Exception inner) : base(message, inner) { }
}
