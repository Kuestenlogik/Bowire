// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Security.Templates.Nuclei;

/// <summary>
/// Translate a Nuclei <see cref="NucleiHttpRequest.Matchers"/> block into
/// Bowire's <see cref="AttackPredicate"/> tree. Composes
/// matcher-condition (and / or) and matcher-internal value-condition
/// (and / or) onto the predicate's <see cref="AttackPredicate.AllOf"/> /
/// <see cref="AttackPredicate.AnyOf"/> composites, and folds
/// <c>negative: true</c> through <see cref="AttackPredicate.Not"/>.
///
/// Phase 2b scope: <c>status</c>, <c>word</c>, <c>regex</c> matcher
/// types — covers most HTTP web-vulnerability templates in the
/// projectdiscovery/nuclei-templates corpus. <c>part: body</c> and the
/// implicit / <c>all</c> are supported; <c>part: header</c> and
/// <c>part: response</c> route through a placeholder that emits
/// <c>null</c> for the matcher (and is logged at the converter level
/// as "skipped — header matching lands in Phase 2b+"). Unknown matcher
/// types likewise emit <c>null</c> — the surrounding predicate-tree
/// just drops the matcher rather than blocking the whole template.
/// </summary>
public enum NucleiMatcherSurface
{
    /// <summary>Matchers over an HTTP response — <c>body</c> / <c>all</c>.</summary>
    Http,

    /// <summary>
    /// Matchers over a DNS response (#491, Phase 2g). Nuclei addresses the
    /// sections separately (<c>answer</c> / <c>question</c> / <c>authority</c> /
    /// <c>additional</c> / <c>raw</c>); Bowire has one body, and
    /// <c>DnsProbeExecutor</c> fills it with the answer section alone.
    /// </summary>
    Dns,

    /// <summary>
    /// Matchers over what came back on a raw socket (#491). One response, one
    /// body — nothing to disambiguate.
    /// </summary>
    Network,

    /// <summary>
    /// Matchers over a TLS certificate (#491). <c>SslProbeExecutor</c> renders
    /// the whole certificate into the body, so parts naming a single field
    /// (<c>issuer</c>, <c>subject</c>, …) cannot be honoured without letting a
    /// word match the wrong field.
    /// </summary>
    Ssl,
}

public static class NucleiMatcherTranslator
{
    /// <summary>
    /// Build the full predicate tree for the matchers on one HTTP
    /// request. Returns <c>null</c> when no matcher translated — the
    /// caller treats that as "this template has no actionable
    /// predicate, will not fire" (visible non-silent outcome).
    /// </summary>
    public static AttackPredicate? Translate(NucleiHttpRequest http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return Translate(http.Matchers, http.MatchersCondition);
    }

    /// <summary>
    /// Build the predicate tree for a bare matcher list + its
    /// composition condition — shared across transports (HTTP + the
    /// Phase-2g <c>dns</c> pass, which reuses the same word / regex /
    /// negative matcher shape over the resolved DNS answer).
    /// </summary>
    public static AttackPredicate? Translate(
        IReadOnlyList<NucleiMatcher> matchers,
        string matchersCondition,
        NucleiMatcherSurface surface = NucleiMatcherSurface.Http)
    {
        ArgumentNullException.ThrowIfNull(matchers);
        if (matchers.Count == 0) return null;

        var subPredicates = new List<AttackPredicate>();
        var dropped = 0;
        foreach (var matcher in matchers)
        {
            var p = TranslateMatcher(matcher, surface);
            if (p is not null) subPredicates.Add(p);
            else dropped++;
        }

        if (subPredicates.Count == 0) return null;

        var isAnd = string.Equals(matchersCondition, "and", StringComparison.OrdinalIgnoreCase);

        // An `and` that lost a conjunct cannot be evaluated honestly. Each
        // dropped matcher was a REQUIRED condition, so composing only the
        // survivors WIDENS the predicate — it fires where Nuclei would not.
        // The OAST templates make this concrete: they pair an out-of-band
        // callback matcher (`part: interactsh_protocol`, untranslatable —
        // #35 Phase 2f) with a status check under `matchers-condition: and`.
        // Dropping the callback conjunct leaves `status == 200` alone, which
        // reports SSRF/RCE on every healthy response. Refusing to translate
        // costs a detection; widening invents one, so we refuse.
        //
        // `or` is the safe direction and keeps its survivors: dropping a
        // branch only narrows what can fire (a missed detection), it can
        // never invent one.
        if (isAnd && dropped > 0) return null;

        if (subPredicates.Count == 1) return subPredicates[0];

        // Compose the matcher-level condition. Nuclei default is "or"
        // when matchers-condition is unset.
        return isAnd
            ? new AttackPredicate { AllOf = subPredicates }
            : new AttackPredicate { AnyOf = subPredicates };
    }

    private static AttackPredicate? TranslateMatcher(NucleiMatcher matcher, NucleiMatcherSurface surface)
    {
        var predicate = matcher.Type switch
        {
            // On the DNS surface a `status` matcher reads the RCODE that
            // DnsProbeExecutor puts in AttackProbeResponse.Status — 0 NOERROR,
            // 3 NXDOMAIN — rather than an HTTP status. Same predicate slot,
            // different meaning, and the template already means the rcode.
            "status" => TranslateStatus(matcher),
            "word" => TranslateWord(matcher, surface),
            "regex" => TranslateRegex(matcher, surface),
            _ => null,
        };

        if (predicate is null) return null;

        // `negative: true` flips matcher polarity — the predicate
        // fires when the values DON'T match. Wrap in Not.
        if (matcher.Negative)
        {
            return new AttackPredicate { Not = predicate };
        }
        return predicate;
    }

    private static AttackPredicate? TranslateStatus(NucleiMatcher matcher)
    {
        if (matcher.Status.Count == 0) return null;
        if (matcher.Status.Count == 1)
        {
            return new AttackPredicate { Status = matcher.Status[0] };
        }
        return new AttackPredicate { StatusIn = matcher.Status.ToList() };
    }

    private static AttackPredicate? TranslateWord(NucleiMatcher matcher, NucleiMatcherSurface surface)
    {
        if (matcher.Words.Count == 0) return null;

        // #35 Phase 2f — the OAST parts assert on the out-of-band callback,
        // not on the response, so they translate onto the interaction axis
        // instead of a body check.
        if (TryTranslateInteractshWord(matcher) is { } oast) return oast;

        if (!IsBodyPart(matcher.Part, surface)) return null; // Header matchers in a later iteration.

        if (matcher.Words.Count == 1)
        {
            return new AttackPredicate { BodyContains = matcher.Words[0] };
        }

        var leaves = matcher.Words
            .Select(w => new AttackPredicate { BodyContains = w })
            .ToList<AttackPredicate>();

        // Within a single multi-value matcher, condition: and|or composes
        // the leaves. Nuclei default is "or" when condition is unset.
        return string.Equals(matcher.Condition, "and", StringComparison.OrdinalIgnoreCase)
            ? new AttackPredicate { AllOf = leaves }
            : new AttackPredicate { AnyOf = leaves };
    }

    /// <summary>
    /// Translate Nuclei's OAST matcher parts onto the interaction axis
    /// (#35 Phase 2f), or null when this matcher isn't one:
    /// <list type="bullet">
    /// <item><c>part: interactsh_protocol</c> — words are transports
    /// (<c>dns</c> / <c>http</c> / …); the callback must have arrived on one.</item>
    /// <item><c>part: interactsh_request</c> — words must appear in the raw
    /// callback, e.g. content the target exfiltrated into it.</item>
    /// </list>
    /// Safe with OAST switched off: with no interaction server no interactions
    /// are collected, so the clause simply never matches.
    /// </summary>
    private static AttackPredicate? TryTranslateInteractshWord(NucleiMatcher matcher)
    {
        var isProtocol = string.Equals(matcher.Part, "interactsh_protocol", StringComparison.OrdinalIgnoreCase);
        var isRequest = string.Equals(matcher.Part, "interactsh_request", StringComparison.OrdinalIgnoreCase);
        if (!isProtocol && !isRequest) return null;

        var leaves = matcher.Words
            .Select(w => new AttackPredicate
            {
                OastInteraction = isProtocol
                    ? new OastInteractionClause { Protocol = w }
                    : new OastInteractionClause { RequestContains = w },
            })
            .ToList<AttackPredicate>();

        if (leaves.Count == 1) return leaves[0];

        // Same composition rule as the body matchers: `condition` defaults to
        // "or" when unset.
        return string.Equals(matcher.Condition, "and", StringComparison.OrdinalIgnoreCase)
            ? new AttackPredicate { AllOf = leaves }
            : new AttackPredicate { AnyOf = leaves };
    }

    private static AttackPredicate? TranslateRegex(NucleiMatcher matcher, NucleiMatcherSurface surface)
    {
        if (matcher.Regex.Count == 0) return null;
        if (!IsBodyPart(matcher.Part, surface)) return null;

        if (matcher.Regex.Count == 1)
        {
            return new AttackPredicate { BodyMatches = matcher.Regex[0] };
        }

        var leaves = matcher.Regex
            .Select(r => new AttackPredicate { BodyMatches = r })
            .ToList<AttackPredicate>();

        return string.Equals(matcher.Condition, "and", StringComparison.OrdinalIgnoreCase)
            ? new AttackPredicate { AllOf = leaves }
            : new AttackPredicate { AnyOf = leaves };
    }

    /// <summary>
    /// Nuclei's <c>part</c> property selects which slice of the response
    /// the matcher inspects. Phase 2b accepts <c>body</c> (the
    /// default), <c>all</c> (whole response — for our predicate model
    /// body-only is close enough), and the empty string. Header /
    /// response-line matching ride a different predicate slot
    /// (<see cref="AttackPredicate.HeaderEquals"/> et al.) and need
    /// their own translation pass; until then they're filtered out so
    /// the matcher contributes nothing.
    /// </summary>
    private static bool IsBodyPart(string part, NucleiMatcherSurface surface)
    {
        return surface switch
        {
            NucleiMatcherSurface.Dns => IsDnsAnswerPart(part),

            // A socket reply is one blob. `data` is Nuclei's name for it.
            NucleiMatcherSurface.Network =>
                IsUnsetOrWholeResponse(part) || part.Equals("data", StringComparison.OrdinalIgnoreCase),

            // The certificate renders into one body, so only whole-response
            // parts translate. A part naming one field (issuer / subject /
            // serial / …) is refused: evaluating it against the whole
            // rendering would let "Let's Encrypt" under `part: issuer` match a
            // subject that happens to contain it. Same rule as the DNS
            // sections — refusing costs a detection, widening invents one.
            NucleiMatcherSurface.Ssl =>
                IsUnsetOrWholeResponse(part) || part.Equals("response", StringComparison.OrdinalIgnoreCase),

            _ => string.IsNullOrEmpty(part)
                || part.Equals("body", StringComparison.OrdinalIgnoreCase)
                || part.Equals("all", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// The parts that mean "whatever came back". <c>body</c> is in here on
    /// every surface because <see cref="NucleiMatcher.Part"/> defaults to it —
    /// an unset part is indistinguishable from a literal one by the time the
    /// translator sees it.
    /// </summary>
    private static bool IsUnsetOrWholeResponse(string part)
    {
        return string.IsNullOrEmpty(part)
            || part.Equals("body", StringComparison.OrdinalIgnoreCase)
            || part.Equals("raw", StringComparison.OrdinalIgnoreCase)
            || part.Equals("all", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Which Nuclei DNS matcher parts can be honoured against the single body
    /// <c>DnsProbeExecutor</c> produces — the <b>answer section only</b>.
    /// <para>
    /// <c>answer</c> is exact. <c>raw</c> / <c>all</c> / the unset default
    /// address the whole response in Nuclei, so evaluating them against the
    /// answer section alone is strictly narrower: a word living in another
    /// section is missed. That direction is a lost detection, which this
    /// translator already accepts elsewhere.
    /// </para>
    /// <para>
    /// <c>question</c> / <c>authority</c> / <c>additional</c> are refused
    /// outright. Answering them from the answer section would be a different
    /// assertion wearing the same name, and for <c>question</c> in particular
    /// it inverts into a false positive: the question echoes the name the
    /// template asked for, so a word drawn from that name matches on every
    /// lookup, vulnerable or not.
    /// </para>
    /// </summary>
    private static bool IsDnsAnswerPart(string part)
    {
        return IsUnsetOrWholeResponse(part)
            || part.Equals("answer", StringComparison.OrdinalIgnoreCase);
    }
}
