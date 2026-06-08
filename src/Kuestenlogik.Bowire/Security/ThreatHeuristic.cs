// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Security;

/// <summary>
/// Deterministic rule-based threat-model ranking (#112). Mirrors the
/// shape <c>/api/ai/threat-model</c> emits so the workbench can render
/// heuristic + AI results with the same code path; adds a
/// <c>ruleTrace</c> array per row explaining which rules fired.
/// <para>
/// The engine is intentionally simple: a fixed catalogue of additive
/// scoring rules over endpoint metadata. No I/O, no AI, sub-millisecond
/// per call. The AI path (<c>BowireAiEndpoints</c> threat-model) stays
/// available for users who want semantic ranking on top; the heuristic
/// is the default so security tooling works without configuring AI.
/// </para>
/// </summary>
internal static class ThreatHeuristic
{
    /// <summary>Endpoint shape — matches the workbench-side ThreatModelEndpoint payload.</summary>
    public sealed record Endpoint(
        string EndpointId,
        string Path,
        string? Verb,
        string? Protocol,
        string? Service,
        string? InputShape,
        string? AuthState);

    /// <summary>One ranked row + the rule-trace explaining its score.</summary>
    public sealed record RankedRow(
        string EndpointId,
        int Risk,
        string Why,
        string[] SuggestedTemplates,
        string[] RuleTrace);

    /// <summary>Top-level ranking result the endpoint returns.</summary>
    public sealed record Ranking(RankedRow[] Ranked);

    // --- Rule catalogue ---
    // Each rule is a (predicate, score, reason, suggestedTemplate?). Scores
    // are additive; a row that trips three rules sums their scores then
    // clamps to 0-10. Same-template hits dedupe in the output.

    private static readonly Regex IdInPath = new(@"\{[^}]+\}|/:\w+|/\d+(/|$)", RegexOptions.Compiled);
    private static readonly Regex AdminPath = new(@"/(admin|internal|debug|sudo|root|management)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AuthPath = new(@"/(auth|login|token|oauth|sso|saml)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UserDataPath = new(@"/(users?|accounts?|profile|members?|customers?|tenants?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PiiHint = new(@"\b(ssn|credit[_-]?card|password|secret|token|api[_-]?key)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Ranking Rank(Endpoint[] endpoints, int topN)
    {
        if (endpoints.Length == 0) return new Ranking([]);

        var ranked = new List<RankedRow>(endpoints.Length);
        foreach (var ep in endpoints)
        {
            var score = 0;
            var trace = new List<string>();
            var templates = new List<string>();

            var path = ep.Path ?? string.Empty;
            var verb = (ep.Verb ?? string.Empty).ToUpperInvariant();
            // CA1308: comparing case-insensitively is the point — using
            // OrdinalIgnoreCase equality below + a ToLowerInvariant
            // here keeps the AuthState case-folded for the trace text.
#pragma warning disable CA1308
            var auth = (ep.AuthState ?? string.Empty).ToLowerInvariant();
#pragma warning restore CA1308
            var input = ep.InputShape ?? string.Empty;

            // Verb-based mutation risk.
            switch (verb)
            {
                case "DELETE":
                    score += 3;
                    trace.Add("verb DELETE → +3 (destructive)");
                    templates.Add("auth-bypass");
                    break;
                case "PATCH":
                case "PUT":
                    score += 2;
                    trace.Add($"verb {verb} → +2 (mutation)");
                    templates.Add("mass-assignment");
                    break;
                case "POST":
                    score += 1;
                    trace.Add("verb POST → +1 (write)");
                    break;
                // GET / HEAD / OPTIONS: no baseline bump.
            }

            // Object-id-in-path → BOLA candidate.
            if (IdInPath.IsMatch(path))
            {
                score += 2;
                trace.Add("path carries an id segment → +2 (BOLA candidate)");
                templates.Add("idor");
            }

            // Admin / internal / debug paths.
            if (AdminPath.IsMatch(path))
            {
                score += 3;
                trace.Add("path under /admin|/internal|/debug → +3");
                templates.Add("auth-bypass");
            }

            // Auth paths get special attention.
            if (AuthPath.IsMatch(path))
            {
                score += 2;
                trace.Add("path under /auth|/login|/token → +2 (auth surface)");
                templates.Add("jwt-flaws");
            }

            // User-data paths.
            if (UserDataPath.IsMatch(path))
            {
                score += 1;
                trace.Add("path under /users|/accounts|/profile → +1 (PII surface)");
            }

            // Anonymous / unknown auth state.
            if (auth == "anonymous")
            {
                score += 2;
                trace.Add("auth state: anonymous → +2");
            }
            else if (string.IsNullOrEmpty(auth) || auth == "unknown")
            {
                score += 1;
                trace.Add("auth state unknown → +1");
            }

            // Sensitive field-name hints in the input shape.
            if (PiiHint.IsMatch(input))
            {
                score += 2;
                trace.Add("input shape contains sensitive field name → +2");
                templates.Add("sensitive-data-exposure");
            }

            // Clamp to 0..10.
            if (score < 0) score = 0;
            if (score > 10) score = 10;

            // Build the headline reason from the highest-scoring trace
            // line or fall back to a generic phrase. Concise — the full
            // explanation lives in RuleTrace.
            var why = trace.Count > 0
                ? string.Join("; ", trace)
                : "no rule fired — baseline low risk";

            ranked.Add(new RankedRow(
                EndpointId: ep.EndpointId,
                Risk: score,
                Why: why,
                SuggestedTemplates: templates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                RuleTrace: trace.ToArray()));
        }

        // Sort by risk desc; take topN.
        ranked.Sort((a, b) => b.Risk.CompareTo(a.Risk));
        if (topN > 0 && ranked.Count > topN) ranked = ranked.GetRange(0, topN);
        return new Ranking(ranked.ToArray());
    }
}
