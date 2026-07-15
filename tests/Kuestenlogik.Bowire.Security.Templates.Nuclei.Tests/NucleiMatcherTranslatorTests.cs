// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security;
using Kuestenlogik.Bowire.Security.Templates.Nuclei;

namespace Kuestenlogik.Bowire.Security.Templates.Nuclei.Tests;

/// <summary>
/// Unit coverage for the matcher → predicate translator. Phase 2b
/// covers <c>status</c>, <c>word</c>, <c>regex</c> matcher types,
/// <c>matchers-condition: and/or</c> composition, value-level
/// <c>condition: and/or</c> composition, and the <c>negative: true</c>
/// inversion. Header / response-line matchers are deferred — the
/// translator returns <c>null</c> for those, which the suite pins
/// explicitly.
/// </summary>
public sealed class NucleiMatcherTranslatorTests
{
    private static NucleiHttpRequest WithMatchers(string mcCondition, params NucleiMatcher[] matchers)
    {
        var req = new NucleiHttpRequest { Method = "GET", MatchersCondition = mcCondition };
        req.Path.Add("{{BaseURL}}/");
        foreach (var m in matchers) req.Matchers.Add(m);
        return req;
    }

    [Fact]
    public void Returns_null_when_no_matchers()
    {
        var req = new NucleiHttpRequest();
        Assert.Null(NucleiMatcherTranslator.Translate(req));
    }

    [Fact]
    public void Status_single_value_becomes_Status_leaf()
    {
        var req = WithMatchers("or", new NucleiMatcher { Type = "status", Status = { 200 } });
        var p = NucleiMatcherTranslator.Translate(req);
        Assert.NotNull(p);
        Assert.Equal(200, p!.Status);
        Assert.Null(p.StatusIn);
    }

    [Fact]
    public void Status_multi_value_becomes_StatusIn_leaf()
    {
        var req = WithMatchers("or", new NucleiMatcher { Type = "status", Status = { 200, 201, 204 } });
        var p = NucleiMatcherTranslator.Translate(req);
        Assert.NotNull(p);
        Assert.Null(p!.Status);
        Assert.Equal(3, p.StatusIn!.Count);
        Assert.Equal(200, p.StatusIn[0]);
        Assert.Equal(201, p.StatusIn[1]);
        Assert.Equal(204, p.StatusIn[2]);
    }

    [Fact]
    public void Single_word_becomes_BodyContains_leaf()
    {
        var req = WithMatchers("or", new NucleiMatcher { Type = "word", Words = { "found-it" }, Part = "body" });
        var p = NucleiMatcherTranslator.Translate(req);
        Assert.NotNull(p);
        Assert.Equal("found-it", p!.BodyContains);
    }

    [Fact]
    public void Multi_word_with_condition_and_becomes_AllOf()
    {
        var req = WithMatchers("or",
            new NucleiMatcher { Type = "word", Words = { "a", "b" }, Condition = "and", Part = "body" });
        var p = NucleiMatcherTranslator.Translate(req);
        Assert.NotNull(p);
        Assert.NotNull(p!.AllOf);
        Assert.Equal(2, p.AllOf!.Count);
        Assert.Contains(p.AllOf, x => x.BodyContains == "a");
        Assert.Contains(p.AllOf, x => x.BodyContains == "b");
    }

    [Fact]
    public void Multi_word_with_condition_or_becomes_AnyOf()
    {
        var req = WithMatchers("or",
            new NucleiMatcher { Type = "word", Words = { "a", "b" }, Condition = "or", Part = "body" });
        var p = NucleiMatcherTranslator.Translate(req);
        Assert.NotNull(p);
        Assert.NotNull(p!.AnyOf);
        Assert.Equal(2, p.AnyOf!.Count);
    }

    [Fact]
    public void Regex_matcher_translates_to_BodyMatches()
    {
        var req = WithMatchers("or",
            new NucleiMatcher { Type = "regex", Regex = { @"v\d+\.\d+\.\d+" }, Part = "body" });
        var p = NucleiMatcherTranslator.Translate(req);
        Assert.Equal(@"v\d+\.\d+\.\d+", p!.BodyMatches);
    }

    [Fact]
    public void Negative_flag_wraps_in_Not()
    {
        var req = WithMatchers("or",
            new NucleiMatcher { Type = "word", Words = { "should-not-appear" }, Negative = true, Part = "body" });
        var p = NucleiMatcherTranslator.Translate(req);
        Assert.NotNull(p!.Not);
        Assert.Equal("should-not-appear", p.Not!.BodyContains);
    }

    [Fact]
    public void MatchersCondition_and_composes_AllOf_at_top()
    {
        var req = WithMatchers("and",
            new NucleiMatcher { Type = "status", Status = { 200 } },
            new NucleiMatcher { Type = "word", Words = { "marker" }, Part = "body" });
        var p = NucleiMatcherTranslator.Translate(req);
        Assert.NotNull(p!.AllOf);
        Assert.Equal(2, p.AllOf!.Count);
    }

    [Fact]
    public void MatchersCondition_or_composes_AnyOf_at_top()
    {
        var req = WithMatchers("or",
            new NucleiMatcher { Type = "status", Status = { 200 } },
            new NucleiMatcher { Type = "word", Words = { "marker" }, Part = "body" });
        var p = NucleiMatcherTranslator.Translate(req);
        Assert.NotNull(p!.AnyOf);
        Assert.Equal(2, p.AnyOf!.Count);
    }

    [Fact]
    public void Single_matcher_returns_predicate_without_top_level_composite()
    {
        // When only ONE matcher exists, no AllOf/AnyOf wrapper —
        // the leaf surfaces directly. Keeps the predicate tree
        // shallow for the common "status: 200" template shape.
        var req = WithMatchers("and", new NucleiMatcher { Type = "status", Status = { 401 } });
        var p = NucleiMatcherTranslator.Translate(req);
        Assert.Null(p!.AllOf);
        Assert.Null(p.AnyOf);
        Assert.Equal(401, p.Status);
    }

    [Fact]
    public void Header_part_matchers_are_skipped_until_later_iteration()
    {
        // Nuclei's part: header would need to land on HeaderEquals /
        // HeaderExists in AttackPredicate — distinct enough that
        // it gets its own translation pass. For now we drop them
        // and the surrounding template runs with whatever
        // body-part matchers it also has.
        var req = WithMatchers("and",
            new NucleiMatcher { Type = "word", Words = { "X-Powered-By: PHP" }, Part = "header" });
        var p = NucleiMatcherTranslator.Translate(req);
        Assert.Null(p);
    }

    [Fact]
    public void Unknown_matcher_type_is_silently_dropped()
    {
        // dsl / binary / size etc. matcher types lack a Bowire
        // analogue today. Drop, don't crash.
        var req = WithMatchers("or",
            new NucleiMatcher { Type = "dsl", Words = { "len(body) > 100" } });
        var p = NucleiMatcherTranslator.Translate(req);
        Assert.Null(p);
    }

    // ---- untranslatable conjuncts (OAST / #35 Phase 2f) ----

    [Fact]
    public void And_condition_with_untranslatable_matcher_returns_null_not_a_widened_predicate()
    {
        // `part: header` has no translation yet, so this AND loses a conjunct.
        // Dropping it would leave `status == 200` alone as the whole
        // predicate — firing on any healthy response. Refusing to translate
        // costs a detection; widening invents one.
        //
        // NB: this used to assert on `part: interactsh_protocol`. That part is
        // now translatable (#35 Phase 2f) and moved to its own tests below.
        var req = WithMatchers("and",
            new NucleiMatcher { Type = "word", Part = "header", Words = { "X-Powered-By" } },
            new NucleiMatcher { Type = "status", Status = { 200 } });

        Assert.Null(NucleiMatcherTranslator.Translate(req));
    }

    [Fact]
    public void Or_condition_with_untranslatable_matcher_keeps_the_translatable_branches()
    {
        // OR is the safe direction: dropping a branch only narrows what fires
        // (a missed detection), it can never invent one.
        var req = WithMatchers("or",
            new NucleiMatcher { Type = "word", Part = "header", Words = { "X-Powered-By" } },
            new NucleiMatcher { Type = "status", Status = { 500 } });

        var p = NucleiMatcherTranslator.Translate(req);
        Assert.NotNull(p);
        Assert.Equal(500, p!.Status);
    }

    // ---- OAST parts (#35 Phase 2f) ----

    [Fact]
    public void Interactsh_protocol_word_translates_onto_the_interaction_axis()
    {
        var req = WithMatchers("or",
            new NucleiMatcher { Type = "word", Part = "interactsh_protocol", Words = { "dns" } });

        var p = NucleiMatcherTranslator.Translate(req);
        Assert.NotNull(p);
        Assert.Equal("dns", p!.OastInteraction!.Protocol);
        // It asserts on the callback, never on the body.
        Assert.Null(p.BodyContains);
    }

    [Fact]
    public void Interactsh_request_word_translates_to_a_raw_callback_check()
    {
        var req = WithMatchers("or",
            new NucleiMatcher { Type = "word", Part = "interactsh_request", Words = { "root:x:" } });

        var p = NucleiMatcherTranslator.Translate(req);
        Assert.NotNull(p);
        Assert.Equal("root:x:", p!.OastInteraction!.RequestContains);
    }

    [Fact]
    public void Interactsh_protocol_multi_word_composes_on_condition()
    {
        var req = WithMatchers("or", new NucleiMatcher
        {
            Type = "word",
            Part = "interactsh_protocol",
            Words = { "dns", "http" },
        });

        var p = NucleiMatcherTranslator.Translate(req);
        Assert.NotNull(p);
        // Default condition is `or`: either transport proves the callback.
        Assert.Equal(2, p!.AnyOf!.Count);
        Assert.Equal("dns", p.AnyOf[0].OastInteraction!.Protocol);
        Assert.Equal("http", p.AnyOf[1].OastInteraction!.Protocol);
    }

    [Fact]
    public void Oast_template_and_condition_now_translates_whole()
    {
        // The exact SSRF shape that previously had to be refused: the OAST
        // conjunct is expressible now, so the AND composes with the proof
        // intact instead of being dropped.
        var req = WithMatchers("and",
            new NucleiMatcher { Type = "word", Part = "interactsh_protocol", Words = { "dns" } },
            new NucleiMatcher { Type = "status", Status = { 200 } });

        var p = NucleiMatcherTranslator.Translate(req);
        Assert.NotNull(p);
        Assert.Equal(2, p!.AllOf!.Count);
        Assert.Equal("dns", p.AllOf[0].OastInteraction!.Protocol);
        Assert.Equal(200, p.AllOf[1].Status);
    }

    [Fact]
    public void And_condition_with_all_matchers_translatable_still_composes()
    {
        // Guard the fix against over-reach: a fully-translatable AND is
        // unaffected.
        var req = WithMatchers("and",
            new NucleiMatcher { Type = "status", Status = { 200 } },
            new NucleiMatcher { Type = "word", Words = { "root:x:" } });

        var p = NucleiMatcherTranslator.Translate(req);
        Assert.NotNull(p);
        Assert.NotNull(p!.AllOf);
        Assert.Equal(2, p.AllOf!.Count);
    }
}
