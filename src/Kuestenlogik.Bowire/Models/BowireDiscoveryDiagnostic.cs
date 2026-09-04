// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Models;

/// <summary>
/// What a protocol plugin has to say about a probe <em>besides</em> the
/// services it returned (#544).
/// <para>
/// <see cref="IBowireProtocol.DiscoverAsync(string, bool, System.Threading.CancellationToken)"/> is all-or-nothing: a plugin
/// either returns a list or throws. A plugin whose probe half-worked — an
/// MCP server with one malformed tool but perfectly good resources and
/// prompts — therefore had to choose between hiding the fault (return the
/// partial list) and hiding the results (throw). This record is the third
/// option, handed back alongside the services through
/// <see cref="IBowireDiscoveryDiagnostics"/>.
/// </para>
/// </summary>
/// <param name="Severity">
/// How bad it is. The plugin does <em>not</em> pick the wire outcome —
/// <see cref="BowireDiscoveryProbe"/> does, because only the probe knows
/// how many services actually came back. See
/// <see cref="BowireDiscoverySeverity"/> for the mapping.
/// </param>
/// <param name="Message">
/// The one-liner that becomes <see cref="BowireDiscoveryAttempt.Message"/>.
/// Never prefixed with the plugin name — the attempt already carries it.
/// </param>
public sealed record BowireDiscoveryDiagnostic(
    BowireDiscoverySeverity Severity,
    string Message)
{
    /// <summary>
    /// The per-step breakdown behind <see cref="Message"/>: one line per
    /// faulted MCP surface, one line per well-known path a REST sweep
    /// tried, … Reaches the wire as the attempt's optional
    /// <c>details</c> array.
    /// <para>
    /// Leave it <see langword="null"/> (not empty) when there is nothing to
    /// break down — the endpoint's serializer ignores nulls, so the field
    /// simply does not appear.
    /// </para>
    /// </summary>
    public IReadOnlyList<string>? Details { get; init; }
}

/// <summary>
/// How a <see cref="BowireDiscoveryDiagnostic"/> is meant to be read. It is
/// a C#-only axis: <see cref="BowireDiscoveryProbe"/> combines it with the
/// service count to pick the <see cref="BowireDiscoveryAttempt.Outcome"/>
/// string that actually reaches the wire.
/// </summary>
public enum BowireDiscoverySeverity
{
    /// <summary>
    /// Context, not a failure — "no OpenAPI document found at this origin",
    /// "resolved via /openapi/v1.json", "the OpenAPI parser package is not
    /// installed". The outcome stays <c>ok</c> / <c>empty</c>; only the
    /// message gets better than "returned no services".
    /// </summary>
    Note,

    /// <summary>
    /// Something genuinely broke behind an otherwise successful handshake.
    /// With services present this is what produces
    /// <see cref="BowireDiscoveryAttempt.OutcomePartial"/>; with none it is
    /// indistinguishable from a throw and lands on <c>error</c>.
    /// </summary>
    Fault,
}
