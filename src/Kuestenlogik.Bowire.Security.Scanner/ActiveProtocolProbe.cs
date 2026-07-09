// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Knobs for the active (mutating / aggressive) scan mode — the opt-in tier
/// the passive scanner deliberately avoids (#395–#400). Every active probe is
/// gated behind <c>--active</c>; these carry the operator-set budgets those
/// probes need to stay bounded.
/// </summary>
public sealed record ActiveScanOptions
{
    /// <summary>
    /// Wall-clock budget (seconds) for time-based probes that hold a connection
    /// open (slow-loris, slow-consumption). Bounds how long the aggressive
    /// checks run. Default 15.
    /// </summary>
    public int DurationSeconds { get; init; } = 15;

    /// <summary>
    /// Concurrency budget for fan-out probes (gRPC concurrent-stream fork-bomb).
    /// The operator sets N explicitly — the verdict is honest about the number
    /// reached rather than assuming a universal limit. Default 100.
    /// </summary>
    public int Concurrency { get; init; } = 100;

    /// <summary>
    /// Operator-supplied allow-list of topics an authenticated client is meant
    /// to reach — the baseline the MQTT wildcard-subscribe probe (#396) judges
    /// delivered traffic against. Empty = no scope supplied (that probe reports
    /// what it observed without a pass/fail verdict).
    /// </summary>
    public IReadOnlyList<string> ExpectedTopics { get; init; } = [];
}

/// <summary>
/// An active (mutating / aggressive) protocol probe. Unlike
/// <see cref="IOwaspProtocolProbe"/> — black-box and side-effect-free — an
/// active probe may <c>PUBLISH</c> a message, hold a connection open, or open
/// many streams against the target. Runs ONLY when the operator opts in with
/// <c>--active</c>; each is expected to namespace + clean up any side effect it
/// leaves (throwaway topics, closed streams) and to stay within the
/// <see cref="ActiveScanOptions"/> budgets.
/// </summary>
internal interface IActiveProtocolProbe
{
    /// <summary>The OWASP Top-10 entry this probe's findings roll up to.</summary>
    OwaspApiEntry Entry { get; }

    /// <summary>Id of the protocol plugin this probe drives (matched against <see cref="IBowireProtocol.Id"/>).</summary>
    string ProtocolId { get; }

    /// <summary>
    /// Run the active probe against <paramref name="target"/> using the resolved
    /// <paramref name="protocol"/> plugin, within the <paramref name="active"/>
    /// budgets. A probe that finds the target isn't addressed as its protocol
    /// (wrong scheme, missing credential) returns an empty list rather than a
    /// false finding.
    /// </summary>
    Task<IReadOnlyList<ScanFinding>> RunAsync(
        string target, IBowireProtocol protocol, IList<string> authHeaders, ActiveScanOptions active, CancellationToken ct);
}
