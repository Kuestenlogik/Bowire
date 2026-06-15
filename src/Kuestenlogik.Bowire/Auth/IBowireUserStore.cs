// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// Resolves on-disk storage paths for per-user Bowire state
/// (environments, recordings, collections, flows, &amp;c.). The default
/// implementation returns the legacy single-user layout under
/// <c>~/.bowire/</c>; multi-tenant deployments (issue #28) swap in an
/// implementation that scopes paths by the authenticated user's <c>sub</c>
/// claim into <c>~/.bowire-server/users/&lt;sub&gt;/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a seam at all.</b> Bowire stores per-user state through a
/// handful of static stores (<see cref="EnvironmentStore"/>,
/// <see cref="RecordingStore"/>, &amp;c.) that hard-code paths like
/// <c>~/.bowire/environments.json</c>. Single-user mode is correct;
/// multi-tenant installs hit the ceiling of 'everyone shares one
/// <c>~/.bowire/</c>' the moment a second authenticated identity
/// arrives. This seam lets the path resolution become identity-aware
/// without ripping the stores apart -- the stores keep their static
/// shape and call through <see cref="BowireUserContext"/> for the
/// final path.
/// </para>
/// <para>
/// <b>Scope.</b> Phase A of the auth seam (<see cref="IBowireAuthProvider"/>,
/// shipped) handles 'who is the caller?'. This seam handles 'where does
/// their state live?'. SCIM 2.0 (Phase C) handles 'how do operators
/// provision users in the first place?'. Each phase is shippable on its
/// own; this one ships the contract + a default that preserves the
/// current single-user behaviour, so installs that haven't opted into
/// multi-tenant see zero change.
/// </para>
/// </remarks>
public interface IBowireUserStore
{
    /// <summary>
    /// Absolute path to the file <paramref name="filename"/> under the
    /// active user's scope. Implementations are free to colocate users
    /// (single shared dir, the default) or partition by identity
    /// (per-user subdirectory, the multi-tenant case).
    /// </summary>
    /// <param name="filename">
    /// Trailing-component file name without any directory prefix --
    /// e.g. <c>"environments.json"</c>, <c>"recordings.json"</c>.
    /// Implementations decorate it with whatever path makes sense for
    /// their model.
    /// </param>
    string GetUserPath(string filename);
}

/// <summary>
/// Default <see cref="IBowireUserStore"/> implementation -- the legacy
/// single-user layout under <c>~/.bowire/</c>. Installations that
/// don't opt into multi-tenant mode (issue #28) use this transparently
/// and see no behavioural change.
/// </summary>
public sealed class DefaultBowireUserStore : IBowireUserStore
{
    /// <summary>Singleton instance -- the store has no per-instance state.</summary>
    public static readonly DefaultBowireUserStore Instance = new();

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".bowire");

    public string GetUserPath(string filename)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);
        // SafePath.Combine rejects rooted filenames (so a caller can't
        // sneak an absolute path that would silently drop Root via
        // Path.Combine's documented behaviour) and verifies the result
        // still lives under Root after normalisation. Callers pass
        // multi-segment relatives like "workspaces/<id>/recordings"
        // through here too — SafePath handles them the same way.
        return SafePath.Combine(Root, filename);
    }
}

/// <summary>
/// Static facade the per-user stores call through to resolve paths.
/// Defaults to <see cref="DefaultBowireUserStore.Instance"/>; the host
/// can swap in a request-aware implementation by setting
/// <see cref="Current"/> at startup (single-user override) or via an
/// async-local <c>HttpContext</c>-driven middleware (multi-tenant mode,
/// once SCIM lands).
/// </summary>
public static class BowireUserContext
{
    private static IBowireUserStore _current = DefaultBowireUserStore.Instance;

    /// <summary>
    /// Active resolver. Defaults to single-user
    /// <see cref="DefaultBowireUserStore.Instance"/>; the host can
    /// replace at startup. Multi-tenant deployments will route through
    /// an <c>AsyncLocal&lt;IBowireUserStore&gt;</c>-backed wrapper so
    /// each request sees its caller's scope -- that machinery lands
    /// with the SCIM phase, not now.
    /// </summary>
    public static IBowireUserStore Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Convenience: resolve <paramref name="filename"/> under the
    /// active user store. Equivalent to <c>Current.GetUserPath(filename)</c>.
    /// Stores call this to keep their existing static shape while
    /// participating in the user-scoping seam.
    /// </summary>
    public static string GetUserPath(string filename) => _current.GetUserPath(filename);

    /// <summary>
    /// #147 — resolve a per-workspace path. When <paramref name="storageRoot"/>
    /// is set (git-backed workspace, the operator points the workspace
    /// at a checked-out folder), the resolver returns
    /// <c>&lt;storageRoot&gt;/&lt;relativePath&gt;</c>. Otherwise falls
    /// back to the legacy per-user layout under
    /// <c>~/.bowire/workspaces/&lt;workspaceId&gt;/&lt;relativePath&gt;</c>.
    /// Stores call this to participate in the git-workspace seam
    /// without having to thread storageRoot through every method.
    /// </summary>
    /// <param name="workspaceId">
    /// Workspace identifier. Used as a path segment in the default-
    /// folder fallback path; ignored when <paramref name="storageRoot"/>
    /// is set.
    /// </param>
    /// <param name="storageRoot">
    /// Optional absolute path to a git-backed workspace's root.
    /// </param>
    /// <param name="relativePath">
    /// Path relative to the workspace root — e.g. "environments/staging.json",
    /// "recordings/login.json".
    /// </param>
    public static string GetWorkspacePath(string workspaceId, string? storageRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        // Upfront rejection of absolute relativePath / workspaceId so
        // every downstream Path.Combine in this method is safe — the
        // BCL would silently drop earlier segments otherwise (the
        // cs/path-combine footgun). storageRoot itself is the operator's
        // chosen root in both branches; we trust that one.
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException(
                "Expected a relative path; got an absolute path: " + relativePath,
                nameof(relativePath));
        }
        if (!string.IsNullOrEmpty(workspaceId) && Path.IsPathRooted(workspaceId))
        {
            throw new ArgumentException(
                "Expected a relative workspace id; got an absolute path: " + workspaceId,
                nameof(workspaceId));
        }

        if (!string.IsNullOrWhiteSpace(storageRoot))
        {
            // Anchor everything under the operator's checked-out folder
            // so per-entity files land in the working tree the team
            // ships in git. SafePath verifies the combined path stays
            // under storageRoot after normalisation — blocks ../
            // escapes the operator's relativePath could otherwise smuggle.
            return SafePath.Combine(storageRoot, relativePath);
        }
        // Default-folder layout — preserved exactly so installs that
        // haven't opted in see zero behavioural change. The IsPathRooted
        // guards above mean each Path.Combine here just concatenates
        // segments under the (relative) "workspaces" anchor; the
        // ../-escape check happens one level down when the legacy
        // string flows into DefaultBowireUserStore.GetUserPath →
        // SafePath.Combine.
        var legacy = string.IsNullOrEmpty(workspaceId)
            ? Path.Combine("workspaces", relativePath)
            : Path.Combine("workspaces", workspaceId, relativePath);
        return _current.GetUserPath(legacy);
    }
}
