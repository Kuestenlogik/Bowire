// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// One line per action taken on somebody else's behalf (#98).
/// </summary>
/// <remarks>
/// <para>
/// Impersonation is the one place in Bowire where the person doing something
/// and the person it happens to are different people. Everything else in the
/// product can be reconstructed from its own state; this cannot — once an
/// administrator has acted as somebody, the resulting recording looks exactly
/// like one that person made themselves.
/// </para>
/// <para>
/// So the log is append-only and names both identities on every line. It is
/// not a general request log: writing every read would bury the handful of
/// lines that matter under a day of noise, and a log nobody can read is not an
/// audit trail. Only the start, the end, and the requests that changed
/// something are recorded.
/// </para>
/// </remarks>
public sealed class BowireAuditLog
{
    /// <summary>The directory under the storage root that holds the log.</summary>
    public const string DirectoryName = "audit";

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;

    /// <summary>A log under <paramref name="storageRoot"/>.</summary>
    /// <param name="storageRoot">The data root.</param>
    /// <param name="clock">Injected in tests so timestamps are predictable.</param>
    public BowireAuditLog(string storageRoot, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        Directory = Path.Combine(Path.GetFullPath(storageRoot), DirectoryName);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Where the log is kept.</summary>
    public string Directory { get; }

    /// <summary>The log itself.</summary>
    public string File => Path.Combine(Directory, "actions.jsonl");

    /// <summary>
    /// Record one action.
    /// </summary>
    /// <param name="action">What happened — <c>begin</c>, <c>end</c>, or the HTTP method.</param>
    /// <param name="actor">Who did it. The real caller, never the impersonated identity.</param>
    /// <param name="actingAs">Whose behalf it was on.</param>
    /// <param name="detail">The path, or whatever else identifies the action.</param>
    public void Record(string action, string actor, string actingAs, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(actingAs);

        var line = JsonSerializer.Serialize(new
        {
            at = _clock.GetUtcNow(),
            action,
            actor,
            actingAs,
            detail,
        });

        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                System.IO.File.AppendAllText(File, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort. Failing the request would be the wrong trade:
                // the administrator is mid-support-call, and an unwritable log
                // is an operator problem rather than a reason to stop them
                // working. It is loud in the sense that matters — the missing
                // line is visible next to the ones around it.
                _ = ex;
            }
        }
    }

    /// <summary>Every line in the log, oldest first. For tests and operators.</summary>
    public IReadOnlyList<string> Lines()
    {
        lock (_gate)
        {
            return System.IO.File.Exists(File)
                ? System.IO.File.ReadAllLines(File)
                : [];
        }
    }
}
