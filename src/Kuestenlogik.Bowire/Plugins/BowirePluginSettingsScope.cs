// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// Which workspace the current call is being served for (#640).
/// </summary>
/// <remarks>
/// <para>
/// Ambient state, which is normally worth avoiding — and is the same trade
/// <see cref="Auth.BowireTenancy"/> makes, for the same reason. The workspace
/// arrives as a query parameter on an HTTP request, and the code that needs it
/// is <c>DiscoverAsync</c> on a plugin, which is handed a URL and a
/// cancellation token and nothing else. Threading a workspace through every
/// protocol contract in every plugin repository, to deliver something almost
/// no call site uses, is a worse trade than one <c>AsyncLocal</c> read by one
/// service.
/// </para>
/// <para>
/// Unset means "no workspace named", which is the ordinary case: the CLI, a
/// test, an embedded host that never adopted workspaces. Settings then fall
/// back to their declared defaults rather than erroring — a plugin should work
/// when nobody has configured it.
/// </para>
/// </remarks>
public static class BowirePluginSettingsScope
{
    private static readonly AsyncLocal<Scope?> s_current = new();

    /// <summary>The workspace being served, or <c>null</c>.</summary>
    public static (string WorkspaceId, string? StorageRoot)? Current
        => s_current.Value is { } scope ? (scope.WorkspaceId, scope.StorageRoot) : null;

    /// <summary>
    /// Serve the rest of this call for <paramref name="workspaceId"/>. Dispose
    /// restores whatever was in scope before.
    /// </summary>
    /// <param name="workspaceId">
    /// The workspace. Null or blank clears the scope rather than naming an
    /// empty one, so a request without a workspace looks the same as a CLI
    /// invocation.
    /// </param>
    /// <param name="storageRoot">
    /// A git-backed workspace's checkout, when the operator pointed one there.
    /// </param>
    public static IDisposable Enter(string? workspaceId, string? storageRoot = null)
    {
        var previous = s_current.Value;
        s_current.Value = string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : new Scope(workspaceId.Trim(), storageRoot);
        return new Restore(previous);
    }

    private sealed record Scope(string WorkspaceId, string? StorageRoot);

    private sealed class Restore(Scope? previous) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            s_current.Value = previous;
        }
    }
}
