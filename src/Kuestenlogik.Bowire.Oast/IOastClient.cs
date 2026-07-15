// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Oast;

/// <summary>
/// A callback host planted in one probe, plus the id the interaction server
/// files its callbacks under.
/// </summary>
/// <param name="CallbackHost">
/// The host to substitute into the probe (e.g. <c>c20id13nonce.oast.example.com</c>).
/// Every allocation returns a distinct host so two probes can never be
/// credited with each other's callback.
/// </param>
/// <param name="CorrelationId">
/// The slice of <paramref name="CallbackHost"/> the server correlates on.
/// </param>
public readonly record struct OastAllocation(string CallbackHost, string CorrelationId);

/// <summary>
/// The out-of-band interaction seam (#35 Phase 2f). A probe plants a callback
/// host in the target; if the target resolves or fetches it, the interaction
/// server records the callback and the scanner polls it back — which is the
/// only way to detect a *blind* vulnerability (SSRF / RCE / XXE) where the
/// response itself carries no evidence.
/// </summary>
/// <remarks>
/// Implementations are opt-in by construction: the scanner has no client
/// unless the operator passed an interaction server, so a scan never reaches
/// a third party by default.
/// </remarks>
public interface IOastClient : IAsyncDisposable
{
    /// <summary>
    /// The interaction-server domain callbacks are addressed under (e.g.
    /// <c>oast.example.com</c>). Surfaced so findings can name where the
    /// evidence was collected.
    /// </summary>
    string ServerDomain { get; }

    /// <summary>
    /// Reserve a fresh callback host for one probe. Cheap + local after the
    /// session is registered — allocation does not round-trip per call.
    /// </summary>
    OastAllocation Allocate();

    /// <summary>
    /// Fetch the interactions the server has recorded for this session since
    /// the last poll. Returns only new ones; correlate them to a probe via
    /// <see cref="OastInteraction.FullId"/> / <see cref="OastInteraction.UniqueId"/>.
    /// </summary>
    Task<IReadOnlyList<OastInteraction>> PollAsync(CancellationToken ct = default);
}
