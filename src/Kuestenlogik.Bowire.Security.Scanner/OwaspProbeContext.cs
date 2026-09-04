// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Everything one protocol probe is given for one attempt against one URL.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a second <c>RunAsync</c> overload that existed only to carry
/// <c>--auth-header-b</c>. A third overload would have been due the moment a
/// probe needed anything else — and it was, immediately: a gRPC scan against a
/// server with reflection switched off needs the descriptor set the operator
/// supplied, or it cannot enumerate the methods it is meant to test. Growing
/// the parameter list once per flag makes every probe re-declare parameters it
/// has no opinion about.
/// </para>
/// <para>
/// The record is the seam that stops that: a new scan flag reaching probes is
/// a property here, and the probes that don't care never mention it.
/// </para>
/// </remarks>
internal sealed record OwaspProbeContext
{
    /// <summary>The URL this attempt is against.</summary>
    /// <remarks>
    /// Not necessarily the scan's <c>--target</c>: the suite retries
    /// well-known sub-paths, so a probe sees one candidate at a time.
    /// </remarks>
    public required string Target { get; init; }

    /// <summary>The resolved plugin for <see cref="IOwaspProtocolProbe.ProtocolId"/>.</summary>
    public required IBowireProtocol Protocol { get; init; }

    /// <summary>The scan's <c>--auth-header</c> values, as <c>Name: Value</c> strings.</summary>
    public IList<string> AuthHeaders { get; init; } = [];

    /// <summary>
    /// The scan's <c>--auth-header-b</c> values — a second identity, for
    /// probes that check one identity cannot reach another's objects.
    /// </summary>
    public IList<string> AuthHeadersB { get; init; } = [];

    /// <summary>
    /// Plugin configuration for this scan, in the metadata bag
    /// <see cref="IBowireProtocol.InvokeAsync"/> already takes.
    /// </summary>
    /// <remarks>
    /// Keys are the well-known <see cref="BowireMetadataKeys"/> markers, which
    /// plugins strip before anything reaches the wire — so this is where a
    /// gRPC descriptor set travels, and where mTLS material would. Empty for
    /// every scan that passed no such flag, which is most of them.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? ProtocolMetadata { get; init; }
}
