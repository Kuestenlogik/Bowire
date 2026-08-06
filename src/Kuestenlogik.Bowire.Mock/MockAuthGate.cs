// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.Mock;

/// <summary>
/// #562: the mock's optional auth gate. Holds a mutable auth-requirement — the
/// whole reference is swapped to toggle auth live (mirroring the
/// <c>Faults</c> hot-swap) so the gate always reads a consistent snapshot — and
/// evaluates each request: when auth is required, a request that presents no
/// credential (or the wrong one) is rejected with 401 before replay.
/// </summary>
public sealed class MockAuthGate
{
    private volatile MockAuthRequirement? _current;

    /// <summary>
    /// The active requirement, or null when auth isn't required. Assign the
    /// whole reference to change it live — a partial field mutation would let a
    /// request see an inconsistent requirement.
    /// </summary>
    public MockAuthRequirement? Current
    {
        get => _current;
        set => _current = value;
    }

    /// <summary>
    /// #562: the mapping behind <c>bowire mock --require-auth &lt;token&gt;</c> — an
    /// empty/absent token yields an open gate, otherwise one requiring a bearer
    /// token equal to <paramref name="credential"/>. The single canonical place
    /// the flag becomes a requirement, so the wiring is guarded by one test.
    /// </summary>
    public static MockAuthGate RequireBearer(string? credential) =>
        string.IsNullOrEmpty(credential)
            ? new MockAuthGate()
            : new MockAuthGate
            {
                Current = new MockAuthRequirement { Required = true, Scheme = "bearer", Credential = credential },
            };

    /// <summary>True when the request is authorized (or auth isn't required).</summary>
    public bool IsAuthorized(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var req = _current;
        if (req is null || !req.Required) return true;

        var presented = ExtractCredential(ctx, req);
        if (string.IsNullOrEmpty(presented)) return false;

        // An exact expected credential must match; when none is configured, any
        // non-empty credential of the scheme is accepted (presence-only).
        return string.IsNullOrEmpty(req.Credential)
            || string.Equals(presented, req.Credential, StringComparison.Ordinal);
    }

    private static string? ExtractCredential(HttpContext ctx, MockAuthRequirement req)
    {
        var headerName = string.IsNullOrWhiteSpace(req.Header) ? "Authorization" : req.Header;
        var raw = ctx.Request.Headers[headerName].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // bearer / basic carry a `<scheme> <token>` value — strip the prefix.
        // apikey (or any custom scheme in a custom header) is the raw value.
        var prefixed = string.IsNullOrWhiteSpace(req.Scheme)
            || string.Equals(req.Scheme, "bearer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(req.Scheme, "basic", StringComparison.OrdinalIgnoreCase);
        if (prefixed)
        {
            // A prefixed scheme MUST carry `<scheme> <token>`. No delimiter means
            // no token — a bare `Authorization: Bearer` is not a presented
            // credential, so it must not satisfy presence-only mode.
            var space = raw.IndexOf(' ');
            return space >= 0 ? raw[(space + 1)..].Trim() : string.Empty;
        }
        return raw.Trim();
    }
}
