// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// Per-environment in-memory cookie store. Bowire holds one
/// <see cref="CookieContainer"/> per environment id while the workbench
/// process runs; the REST plugin hands the container to a fresh per-call
/// <see cref="HttpClientHandler"/> when the JS layer marked the request
/// with <see cref="MarkerKey"/>, so cookies set by one call (e.g.
/// <c>POST /login</c>) ride along on every following call to the same
/// origin (<c>GET /me</c>).
/// <para>
/// Memory-only by design: process restart wipes the jar, which mirrors
/// what an actual logged-out browser session looks like. Persistent
/// storage would buy little since secrets in cookies are already short-
/// lived and the user can re-authenticate trivially.
/// </para>
/// </summary>
public static class CookieJar
{
    /// <summary>
    /// Magic metadata key the JS auth layer uses to opt a request into
    /// the persistent cookie jar. The value is the active environment id
    /// — Bowire keeps one container per env so dev/staging/prod don't
    /// share session cookies. Stripped from the request headers before
    /// the actual HTTP call so the marker never reaches the wire.
    /// </summary>
    public const string MarkerKey = "__bowireCookieEnv__";

    private static readonly ConcurrentDictionary<string, CookieContainer> s_jars = new(StringComparer.Ordinal);

    /// <summary>
    /// Look up the container for the supplied environment id, creating
    /// one on the fly the first time it's referenced.
    /// </summary>
    public static CookieContainer GetOrCreate(string envId)
    {
        return s_jars.GetOrAdd(envId, _ => new CookieContainer());
    }

    /// <summary>
    /// Drop every cookie associated with <paramref name="envId"/>. Wired
    /// into the env-auth UI's "Clear cookies" button via the
    /// <c>/api/auth/cookie-jar</c> endpoint.
    /// </summary>
    public static bool Clear(string envId) => s_jars.TryRemove(envId, out _);

    /// <summary>
    /// Snapshot of every (origin, name=value) pair currently stored for
    /// <paramref name="envId"/>. Used by the workbench to render the
    /// cookies the next request will replay; the underlying container
    /// is the source of truth, this is read-only.
    /// </summary>
    public static IReadOnlyList<CookieSnapshot> Snapshot(string envId)
    {
        if (!s_jars.TryGetValue(envId, out var container)) return [];
        var list = new List<CookieSnapshot>();
        foreach (Cookie c in container.GetAllCookies())
        {
            list.Add(new CookieSnapshot(c.Domain, c.Path, c.Name, c.Value, c.Expires, c.Secure, c.HttpOnly));
        }
        return list;
    }
}

/// <summary>Wire shape for the workbench's cookies-list endpoint.</summary>
public sealed record CookieSnapshot(
    string Domain,
    string Path,
    string Name,
    string Value,
    DateTime Expires,
    bool Secure,
    bool HttpOnly);
