// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Contracts;

/// <summary>
/// Result of verifying one consumer contract against a provider (#364).
/// The engine's own report type, deliberately decoupled from the CLI's
/// generic <c>RunReport</c>: it carries the consumer and provider as
/// structured names (not folded into a "C → P" title) plus a start
/// timestamp, which is exactly what the consumer × provider matrix needs
/// for its axes and per-cell "last run". The CLI adapter maps this onto a
/// <c>RunReport</c> for the shared JUnit / SARIF emitters.
/// </summary>
public sealed class ContractVerificationReport
{
    /// <summary>Consumer party name (matrix row).</summary>
    public string Consumer { get; set; } = "";

    /// <summary>Provider party name (matrix column).</summary>
    public string Provider { get; set; } = "";

    /// <summary>When the verification run started (UTC) — the cell's "last run".</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Wall-clock duration of the whole run.</summary>
    public long DurationMs { get; set; }

    // `init` rather than get-only: the store round-trips reports through
    // System.Text.Json, which cannot populate a get-only collection — a
    // get-only list silently deserialised as empty and dropped every stored
    // result from the matrix. CA2227 still holds (no full setter).
    /// <summary>One entry per replayed interaction, in contract order.</summary>
    public List<ContractInteractionResult> Interactions { get; init; } = [];

    /// <summary>Total assertions across all interactions.</summary>
    public int TotalAssertions { get; set; }

    /// <summary>Assertions that passed.</summary>
    public int PassedAssertions { get; set; }

    /// <summary>Interactions with at least one failed assertion or a transport error.</summary>
    public int FailedInteractions { get; set; }

    /// <summary>True when the provider satisfied every interaction.</summary>
    public bool Passed => FailedInteractions == 0;
}

/// <summary>One interaction replayed against the provider.</summary>
public sealed class ContractInteractionResult
{
    /// <summary>The Pact interaction description (drill-in row label).</summary>
    public string Description { get; set; } = "";

    /// <summary>HTTP method of the replayed request.</summary>
    public string Method { get; set; } = "";

    /// <summary>Actual response status, as a string ("200"); null if the request never completed.</summary>
    public string? Status { get; set; }

    /// <summary>Actual response body (may be truncated by the caller for display).</summary>
    public string? Response { get; set; }

    /// <summary>Transport / read error, if the interaction could not be evaluated.</summary>
    public string? Error { get; set; }

    /// <summary>Duration of this single interaction.</summary>
    public long DurationMs { get; set; }

    // init-only for the same round-trip reason as Interactions above.
    /// <summary>Status + body assertions checked against the contract.</summary>
    public List<ContractAssertion> Assertions { get; init; } = [];

    /// <summary>True when there was no error and every assertion passed.</summary>
    public bool Passed => string.IsNullOrEmpty(Error) && Assertions.TrueForAll(a => a.Passed);
}

/// <summary>A single contract assertion (status match or structural body match).</summary>
public sealed class ContractAssertion
{
    /// <summary>What was checked: <c>status</c> or <c>body</c>.</summary>
    public string Path { get; set; } = "";

    /// <summary>The comparison: <c>eq</c> (status) or <c>matches-shape</c> (body).</summary>
    public string Op { get; set; } = "";

    /// <summary>Expected value / shape (may be truncated for display).</summary>
    public string? Expected { get; set; }

    /// <summary>Actual value / body (may be truncated for display).</summary>
    public string? ActualText { get; set; }

    /// <summary>Whether the assertion held.</summary>
    public bool Passed { get; set; }

    /// <summary>The shape diff or mismatch detail when it failed.</summary>
    public string? Error { get; set; }
}
