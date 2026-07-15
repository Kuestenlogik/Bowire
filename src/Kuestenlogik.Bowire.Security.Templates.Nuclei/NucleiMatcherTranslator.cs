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
    public static AttackPredicate? Translate(IReadOnlyList<NucleiMatcher> matchers, string matchersCondition)
    {
        ArgumentNullException.ThrowIfNull(matchers);
        if (matchers.Count == 0) return null;

        var subPredicates = new List<AttackPredicate>();
        var dropped = 0;
        foreach (var matcher in matchers)
        {
            var p = TranslateMatcher(matcher);
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

    private static AttackPredicate? TranslateMatcher(NucleiMatcher matcher)
    {
        var predicate = matcher.Type switch
        {
            "status" => TranslateStatus(matcher),
            "word" => TranslateWord(matcher),
            "regex" => TranslateRegex(matcher),
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

    private static AttackPredicate? TranslateWord(NucleiMatcher matcher)
    {
        if (matcher.Words.Count == 0) return null;
        if (!IsBodyPart(matcher.Part)) return null; // Header matchers in a later iteration.

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

    private static AttackPredicate? TranslateRegex(NucleiMatcher matcher)
    {
        if (matcher.Regex.Count == 0) return null;
        if (!IsBodyPart(matcher.Part)) return null;

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
    private static bool IsBodyPart(string part)
    {
        return string.IsNullOrEmpty(part)
            || part.Equals("body", StringComparison.OrdinalIgnoreCase)
            || part.Equals("all", StringComparison.OrdinalIgnoreCase);
    }
}
