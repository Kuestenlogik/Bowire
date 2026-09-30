// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Net;

/// <summary>
/// Refuses every outgoing request whose URL <paramref name="allow"/> rejects,
/// before it leaves the process. For callers whose request URLs are only known
/// while they run — an auth flow substitutes <c>{{var}}</c> values captured
/// from earlier responses into later step URLs — so the check has to sit on the
/// wire, not on the definition. Pair it with an inner handler that does not
/// follow redirects: a redirect is resolved below this handler and would
/// otherwise never be seen here.
/// </summary>
internal sealed class BowireTargetGuardHandler(Func<Uri, bool> allow) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } uri || !allow(uri))
            throw new BowireTargetRefusedException(request.RequestUri?.ToString());
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// An outgoing request was refused because its target is outside the host's
/// allowed targets (<see cref="BowireTargetPolicy"/>). Derives from
/// <see cref="HttpRequestException"/> so callers that already treat transport
/// failures as "the call did not go through" handle it without a new branch.
/// </summary>
public sealed class BowireTargetRefusedException : HttpRequestException
{
    /// <summary>Creates the exception without naming a target.</summary>
    public BowireTargetRefusedException() : base("Target not allowed on this server.") { }

    /// <summary>Creates the exception for the refused <paramref name="target"/>.</summary>
    public BowireTargetRefusedException(string? target)
        : base("Target not allowed on this server: " + (target ?? "(none)"))
    {
        Target = target;
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public BowireTargetRefusedException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>The URL that was refused, when known.</summary>
    public string? Target { get; }
}
