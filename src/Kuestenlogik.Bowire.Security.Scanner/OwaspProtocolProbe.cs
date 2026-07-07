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
/// and hands the live instance to <see cref="RunAsync"/>. When the plugin
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
}
