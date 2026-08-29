// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Auth;

/// <summary>Who is acting, and on whose behalf (#98).</summary>
public sealed class BowireImpersonationScope
{
    /// <summary>The real caller — the administrator, never the target.</summary>
    public required string Actor { get; init; }

    /// <summary>The identity whose slot is being served.</summary>
    public required string ActingAs { get; init; }
}

/// <summary>
/// An administrator looking at somebody else's workbench (#98).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a cookie rather than a signed token.</b> The workbench has some
/// seventy direct <c>fetch</c> call sites and no central wrapper, so a header
/// would mean touching all of them or patching global <c>fetch</c> — and a
/// tool whose purpose is sending requests to other people's services is the
/// last place to add a header to every outbound call by default. A cookie
/// rides along on same-origin requests and needs no client change at all.
/// </para>
/// <para>
/// <b>Why that is not a weaker choice.</b> The cookie is a parameter, never an
/// authority. On every request the server re-derives the real caller from
/// their token and re-asks the directory whether <em>they</em> administer the
/// install; the cookie only says whose slot to open. Forging it therefore buys
/// nothing unless you are already an administrator — a stronger property than
/// a signed token, which is only as good as its key and as the moment it was
/// minted. An administrator who is demoted stops impersonating on their very
/// next request, without anything having to expire.
/// </para>
/// </remarks>
public static class BowireImpersonation
{
    /// <summary>
    /// The cookie that names whose slot to open.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>__Host-</c> prefixed. That prefix requires the
    /// Secure attribute, and an embedded host reachable over plain HTTP on an
    /// internal network would then fail to set the cookie at all — silently,
    /// with impersonation appearing to do nothing. The prefix would buy
    /// protection against a sibling host writing the value, and the value is
    /// re-authorised against the caller's own token on every request anyway,
    /// so there is nothing to gain by writing it.
    /// </remarks>
    public const string CookieName = "bowire.act-as";

    /// <summary>
    /// The route that starts and ends a session, relative to the workbench's
    /// base path.
    /// </summary>
    /// <remarks>
    /// Named here because two places have to agree about it: the endpoint that
    /// maps it, and the middleware that must <em>not</em> record a request to
    /// it. Ending a session is already written to the log as <c>end</c>, and
    /// recording the DELETE as well puts the same event in twice, in two
    /// vocabularies.
    /// </remarks>
    public const string EndpointPath = "/api/impersonation";

    private static readonly AsyncLocal<BowireImpersonationScope?> s_current = new();

    /// <summary>
    /// The impersonation in force on this execution context, or <c>null</c>
    /// when the caller is acting as themselves.
    /// </summary>
    public static BowireImpersonationScope? Current => s_current.Value;

    /// <summary>
    /// Act as <paramref name="actingAs"/> until the returned scope is disposed.
    /// </summary>
    /// <remarks>
    /// Restores the previous value rather than clearing it, for the same
    /// reason <see cref="BowireTenancy.Enter"/> does: a nested scope is
    /// legitimate, and clearing would silently drop the outer one for the rest
    /// of the request.
    /// </remarks>
    public static IDisposable Enter(string actor, string actingAs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(actingAs);

        var previous = s_current.Value;
        s_current.Value = new BowireImpersonationScope
        {
            Actor = actor.Trim(),
            ActingAs = actingAs.Trim(),
        };
        return new Scope(previous);
    }

    private sealed class Scope(BowireImpersonationScope? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            s_current.Value = previous;
        }
    }
}
