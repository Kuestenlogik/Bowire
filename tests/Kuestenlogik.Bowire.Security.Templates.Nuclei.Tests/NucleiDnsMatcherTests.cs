// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security;
using Kuestenlogik.Bowire.Security.Templates.Nuclei;

namespace Kuestenlogik.Bowire.Security.Templates.Nuclei.Tests;

/// <summary>
/// #491 (#35 Phase 2g) — which Nuclei DNS matcher parts translate.
///
/// Bowire's predicate model has one body, and <c>DnsProbeExecutor</c> fills it
/// with the answer section alone. So the parts that address the whole response
/// translate (narrower than Nuclei — a missed detection at worst) and the parts
/// that address a different section do not (answering them from the answer
/// section would be a different assertion under the same name).
/// </summary>
public sealed class NucleiDnsMatcherTests
{
    private static AttackPredicate? Dns(string matchersCondition, params NucleiMatcher[] matchers) =>
        NucleiMatcherTranslator.Translate(matchers, matchersCondition, NucleiMatcherSurface.Dns);

    private static NucleiMatcher Word(string part, params string[] words)
    {
        var m = new NucleiMatcher { Type = "word", Part = part };
        foreach (var w in words) m.Words.Add(w);
        return m;
    }

    [Theory]
    [InlineData("")]
    [InlineData("answer")]
    [InlineData("raw")]
    [InlineData("all")]
    [InlineData("ANSWER")]
    public void Parts_Covered_By_The_Answer_Section_Translate(string part)
    {
        var predicate = Dns("or", Word(part, "myshopify.com"));

        Assert.NotNull(predicate);
        Assert.Equal("myshopify.com", predicate.BodyContains);
    }

    [Theory]
    [InlineData("question")]
    [InlineData("authority")]
    [InlineData("additional")]
    public void Parts_Addressing_Another_Section_Do_Not_Translate(string part)
    {
        Assert.Null(Dns("or", Word(part, "example.com")));
    }

    [Fact]
    public void A_Question_Matcher_Would_Have_Been_A_False_Positive()
    {
        // This is the concrete reason for refusing `question`: the question
        // echoes the very name the template asked for, so a word drawn from it
        // matches on every lookup. Translating it onto the answer body would
        // have been quietly wrong rather than loudly missing.
        var asIfTranslated = new AttackPredicate { BodyContains = "example.com" };
        var everyLookup = new AttackProbeResponse
        {
            Status = 0,
            Body = "shop.example.com. 300 IN CNAME shop.example.com.",
        };

        Assert.True(AttackPredicateEvaluator.Evaluate(asIfTranslated, everyLookup));
        Assert.Null(Dns("or", Word("question", "example.com")));
    }

    [Fact]
    public void An_And_Group_Refuses_When_A_Section_Matcher_Drops_Out()
    {
        // Same rule the HTTP path already applies: a dropped conjunct was a
        // REQUIRED condition, so keeping only the survivors widens the
        // predicate and fires where Nuclei would not.
        var predicate = Dns("and",
            Word("answer", "myshopify.com"),
            Word("authority", "ns1.example.com"));

        Assert.Null(predicate);
    }

    [Fact]
    public void An_Or_Group_Keeps_Its_Survivors()
    {
        // Dropping an `or` branch only narrows — a missed detection, never an
        // invented one.
        var predicate = Dns("or",
            Word("answer", "myshopify.com"),
            Word("authority", "ns1.example.com"));

        Assert.NotNull(predicate);
        Assert.Equal("myshopify.com", predicate.BodyContains);
    }

    [Fact]
    public void Regex_Follows_The_Same_Part_Rule()
    {
        var onAnswer = new NucleiMatcher { Type = "regex", Part = "answer" };
        onAnswer.Regex.Add(@"\.myshopify\.com\.$");
        Assert.NotNull(Dns("or", onAnswer));

        var onQuestion = new NucleiMatcher { Type = "regex", Part = "question" };
        onQuestion.Regex.Add(@"\.example\.com\.$");
        Assert.Null(Dns("or", onQuestion));
    }

    [Fact]
    public void Negative_Still_Inverts_On_The_Dns_Surface()
    {
        // "no CNAME to a known parking provider" is how several takeover
        // templates express the safe case.
        var matcher = Word("answer", "myshopify.com");
        matcher.Negative = true;

        var predicate = Dns("or", matcher);

        Assert.NotNull(predicate);
        Assert.NotNull(predicate.Not);
        Assert.Equal("myshopify.com", predicate.Not.BodyContains);
    }

    [Fact]
    public void The_Http_Surface_Does_Not_Learn_The_Dns_Parts()
    {
        // `answer` is not an HTTP part and must stay untranslated there.
        Assert.Null(NucleiMatcherTranslator.Translate(
            [Word("answer", "x")], "or", NucleiMatcherSurface.Http));
    }

    [Fact]
    public void An_Unset_Part_Arrives_As_Body_And_Still_Translates()
    {
        // NucleiMatcher.Part defaults to "body", so a DNS template that says
        // nothing about part reaches the translator carrying "body". Rejecting
        // it as "not a DNS part" would drop every ordinary dns: template —
        // which is exactly what it did until this test existed.
        var untouchedDefault = new NucleiMatcher { Type = "word" };
        untouchedDefault.Words.Add("s3.amazonaws.com");
        Assert.Equal("body", untouchedDefault.Part);

        var predicate = Dns("or", untouchedDefault);

        Assert.NotNull(predicate);
        Assert.Equal("s3.amazonaws.com", predicate.BodyContains);
    }

    [Fact]
    public void A_Status_Matcher_Reads_The_Rcode()
    {
        // NXDOMAIN is 3. The predicate slot is shared with HTTP status; on a
        // dns: template the executor puts the rcode there.
        var matcher = new NucleiMatcher { Type = "status" };
        matcher.Status.Add(3);

        var predicate = Dns("or", matcher);

        Assert.NotNull(predicate);
        Assert.Equal(3, predicate.Status);
    }
}
