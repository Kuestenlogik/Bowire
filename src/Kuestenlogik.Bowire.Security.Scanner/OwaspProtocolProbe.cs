// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// A protocol-specific OWASP probe. Where <see cref="IOwaspApiProbe"/> speaks
/// raw HTTP against a target, an <see cref="IOwaspProtocolProbe"/> drives one
/// of Bowire's protocol plugins (<see cref="IBowireProtocol"/>) through its
/// own invoke / discovery path — so it can reach the vulnerability classes
/// that only exist at the protocol layer (gRPC server reflection, GraphQL
/// introspection, …) rather than over plain HTTP.
///
/// <para>The probe names the protocol it needs by <see cref="ProtocolId"/>;
/// the suite resolves that plugin from <see cref="BowireProtocolRegistry"/>
/// and hands the live instance to
/// <see cref="RunAsync(string, IBowireProtocol, IList{string}, CancellationToken)"/>. When the plugin
/// isn't deployed next to the host the check is skipped, never falsely
/// passed. Findings carry the same <c>APIn-2023-</c> tag as the HTTP probes,
/// so they roll up into the same OWASP coverage table.</para>
/// </summary>
internal interface IOwaspProtocolProbe
{
    /// <summary>The OWASP Top-10 entry this probe's findings roll up to.</summary>
    OwaspApiEntry Entry { get; }

    /// <summary>
    /// Id of the protocol plugin this probe drives (e.g. <c>graphql</c>,
    /// <c>grpc</c>) — matched against <see cref="IBowireProtocol.Id"/>.
    /// </summary>
    string ProtocolId { get; }

    /// <summary>
    /// Run the probe against <paramref name="target"/> using the resolved
    /// <paramref name="protocol"/> plugin. <paramref name="authHeaders"/>
    /// carries the scan's <c>--auth-header</c> values (as <c>Key: Value</c>
    /// strings) for checks that need an authenticated baseline; discovery-only
    /// checks (reflection / introspection exposure) deliberately probe
    /// anonymously and ignore them.
    /// </summary>
    Task<IReadOnlyList<ScanFinding>> RunAsync(string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct);

    /// <summary>
    /// Same, with the scan's optional <em>second</em> identity
    /// (<c>--auth-header-b</c>) available.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A default implementation that drops <paramref name="authHeadersB"/> and
    /// calls the single-identity overload, so a probe only overrides this when
    /// it actually compares two identities. The alternative — a parameter on
    /// the one method — would have edited fourteen probes that have nothing to
    /// say about a second credential, which is churn that hides the one change
    /// that matters.
    /// </para>
    /// <para>
    /// The second identity already reached the HTTP probe seam
    /// (<see cref="IOwaspApiProbe"/>) and stopped there. Cross-identity
    /// authorization is not an HTTP-only failure — see
    /// Kuestenlogik/Bowire.VulnDb#23, where a WebSocket handler authenticates
    /// the session and never authorizes it.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<ScanFinding>> RunAsync(
        string target, IBowireProtocol protocol,
        IList<string> authHeaders, IList<string> authHeadersB, CancellationToken ct)
        => RunAsync(target, protocol, authHeaders, ct);
}
