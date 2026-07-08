// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Mock.Matchers;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Mock.Management;

/// <summary>
/// A verification query against a mock's request journal (#409) — the mock
/// analog of WireMock's <c>verify(...)</c> / <c>findAll(...)</c>. Reuses the
/// same predicate vocabulary as the request matcher (<see cref="BowireStepMatch"/>):
/// path regex/glob + query / header / cookie predicates, plus an optional
/// method and path, and a count expectation.
/// </summary>
/// <remarks>
/// Body predicates are not evaluated here — the journal doesn't retain request
/// bodies — so a verification carrying <c>match.body</c> won't match anything.
/// </remarks>
public sealed class MockVerification
{
    /// <summary>HTTP method to require (case-insensitive). Null = any.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    /// <summary>Exact request path to require. Ignored when <c>match.pathRegex</c>/<c>pathGlob</c> is set.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Path pattern + query/header/cookie predicates (same shape as a stub's <c>match</c>).</summary>
    [JsonPropertyName("match")]
    public BowireStepMatch? Match { get; init; }

    /// <summary>Require exactly this many matching requests.</summary>
    [JsonPropertyName("exactly")]
    public int? Exactly { get; init; }

    /// <summary>Require at least this many matching requests.</summary>
    [JsonPropertyName("atLeast")]
    public int? AtLeast { get; init; }

    /// <summary>Require at most this many matching requests.</summary>
    [JsonPropertyName("atMost")]
    public int? AtMost { get; init; }
}

/// <summary>Outcome of a <see cref="MockVerification"/> against the journal.</summary>
public sealed record MockVerificationResult(
    bool Satisfied,
    int Count,
    IReadOnlyList<MockRequestEntry> Matches);

/// <summary>
/// Evaluates <see cref="MockVerification"/> queries against journal entries.
/// Stateless — the entries come from a <see cref="MockRequestLog"/> snapshot.
/// </summary>
public static class MockRequestVerifier
{
    /// <summary>Run a verification over <paramref name="entries"/>.</summary>
    public static MockVerificationResult Verify(
        IReadOnlyList<MockRequestEntry> entries, MockVerification verification)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(verification);

        var matches = new List<MockRequestEntry>();
        foreach (var entry in entries)
            if (Matches(entry, verification)) matches.Add(entry);

        var count = matches.Count;
        bool satisfied;
        if (verification.Exactly is null && verification.AtLeast is null && verification.AtMost is null)
        {
            // No count expectation → WireMock's default: at least one.
            satisfied = count >= 1;
        }
        else
        {
            satisfied = (verification.Exactly is null || count == verification.Exactly)
                && (verification.AtLeast is null || count >= verification.AtLeast)
                && (verification.AtMost is null || count <= verification.AtMost);
        }

        return new MockVerificationResult(satisfied, count, matches);
    }

    /// <summary>Whether one journal entry satisfies the verification's predicates.</summary>
    public static bool Matches(MockRequestEntry entry, MockVerification verification)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(verification);

        if (verification.Method is not null
            && !string.Equals(entry.Method, verification.Method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = verification.Match;

        // Path: regex / glob on the match win over an exact `path`.
        if (!string.IsNullOrEmpty(match?.PathRegex))
        {
            if (!MockMatchPredicates.PathRegexMatches(match.PathRegex, entry.Path)) return false;
        }
        else if (!string.IsNullOrEmpty(match?.PathGlob))
        {
            if (!MockMatchPredicates.PathGlobMatches(match.PathGlob, entry.Path)) return false;
        }
        else if (verification.Path is not null
            && !string.Equals(entry.Path, verification.Path, StringComparison.Ordinal))
        {
            return false;
        }

        // Query / header / cookie predicates via the shared matcher engine.
        if (match is not null)
        {
            var request = new MockRequest
            {
                Protocol = "rest",
                HttpMethod = entry.Method,
                Path = entry.Path,
                Query = entry.Query,
                Headers = entry.Headers is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(entry.Headers, StringComparer.OrdinalIgnoreCase),
            };
            if (!MockMatchPredicates.AllPredicatesPass(match, request)) return false;
        }

        return true;
    }
}
