// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Everything one OWASP API probe is given for one run against one target.
/// </summary>
/// <remarks>
/// <para>
/// The same seam <see cref="OwaspProbeContext"/> is for the protocol probes,
/// and for the reason its remarks give: a flat parameter list grows once per
/// scan flag, and every probe then re-declares parameters it has no opinion
/// about. Ten probes take this; one of them reads
/// <see cref="Oast"/>.
/// </para>
/// <para>
/// It was due now. SSRF is a server-side effect, and
/// <see cref="Api7SsrfProbe"/>'s own summary says what it settled for without
/// a callback channel: a timing differential. An out-of-band host is the
/// evidence that check approximates.
/// </para>
/// </remarks>
internal sealed record OwaspApiProbeContext
{
    /// <summary>The URL being scanned.</summary>
    public required string Target { get; init; }

    /// <summary>The client to make requests with, configured by the scan.</summary>
    public required HttpClient Http { get; init; }

    /// <summary>The scan's <c>--auth-header</c> values, as <c>Name: Value</c> strings.</summary>
    public IList<string> AuthHeaders { get; init; } = [];

    /// <summary>
    /// A second identity's headers, for the probes that check one identity
    /// cannot reach another's objects. Empty for most scans.
    /// </summary>
    public IList<string> AuthHeadersB { get; init; } = [];

    /// <summary>
    /// The out-of-band callback channel, when the scan has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null is the normal case and is not a failure: a probe that would have
    /// planted a callback falls back to whatever it could prove without one.
    /// What it must not do is report the absence of a callback as evidence of
    /// safety — a target with no outbound network reaches nothing, and that
    /// says nothing about whether it tried.
    /// </para>
    /// </remarks>
    public IOastProbeChannel? Oast { get; init; }

    /// <summary>
    /// How long a probe waits for a planted callback before moving on.
    /// </summary>
    /// <remarks>
    /// A callback lands after the target has made its own request, so there
    /// is nothing to read the moment the probe's own request returns. The
    /// default is a few seconds — long enough for a target that fetches
    /// promptly, short enough that a target which never fetches does not
    /// hold the scan. Anything slower still reaches the workbench live feed,
    /// which polls the same session.
    /// </remarks>
    public TimeSpan OastGrace { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>How often the wait re-checks. Never longer than the grace.</summary>
    public TimeSpan OastPollInterval { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// The out-of-band callback channel a probe may plant into.
/// </summary>
/// <remarks>
/// <para>
/// Narrower than <c>IOastClient</c> on purpose, and not that interface: the
/// workbench session is the thing a probe must go through, because
/// <c>IOastClient.PollAsync</c> returns only what is new. A probe polling the
/// client directly would take those callbacks off the wire and the live feed
/// would never show them -- the operator watching the panel would see a scan
/// find something they cannot see themselves.
/// </para>
/// <para>
/// So the channel returns the accumulated feed rather than a delta, and the
/// session implements it.
/// </para>
/// </remarks>
internal interface IOastProbeChannel
{
    /// <summary>A fresh callback host to plant in one input.</summary>
    Task<string> AllocateAsync(CancellationToken ct = default);

    /// <summary>
    /// Every callback seen in this session so far, including any that have
    /// just arrived.
    /// </summary>
    Task<IReadOnlyList<OastCallback>> PollAsync(CancellationToken ct = default);
}
