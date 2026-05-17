// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Edge-case coverage for <see cref="AttackPredicateEvaluator"/>. The
/// sibling <see cref="AttackPredicateEvaluatorTests"/> covers the
/// "happy" behaviour of each operator; this file fills in the
/// failure-mode + JSONPath-walker branches the v1 file didn't reach.
/// </summary>
public sealed class AttackPredicateEvaluatorEdgeTests
{
    private static AttackProbeResponse R(string body = "{}", int status = 200,
        IReadOnlyDictionary<string, string>? headers = null) => new()
    {
        Status = status,
        Body = body,
        LatencyMs = 0,
        Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void Evaluate_NullPredicate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AttackPredicateEvaluator.Evaluate(null!, R()));
    }

    [Fact]
    public void Evaluate_NullResponse_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AttackPredicateEvaluator.Evaluate(new AttackPredicate(), null!));
    }

    [Fact]
    public void BodyMatches_InvalidRegex_FailsClosed()
    {
        var p = new AttackPredicate { BodyMatches = "(unclosed" };
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R("anything")));
    }

    [Fact]
    public void BodyJsonPath_EmptyBody_AndExistsFalse_Matches()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.foo", Exists = false },
        };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R("")));
    }

    [Fact]
    public void BodyJsonPath_MalformedJsonBody_AndExistsFalse_Matches()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.foo", Exists = false },
        };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R("{not json")));
    }

    [Fact]
    public void BodyJsonPath_NoMatch_NoOperator_ReturnsFalse()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.missing" },
        };
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R("{\"present\":1}")));
    }

    [Fact]
    public void BodyJsonPath_RootPath_ResolvesToRoot()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$", Exists = true },
        };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R("{\"a\":1}")));
    }

    [Fact]
    public void BodyJsonPath_ArrayIndex_OutOfRange_NoMatch()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.items[10]", Exists = true },
        };
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R("{\"items\":[1,2]}")));
    }

    [Fact]
    public void BodyJsonPath_ArrayIndex_OnNonArray_NoMatch()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.x[0]", Exists = true },
        };
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R("{\"x\":\"not-array\"}")));
    }

    [Fact]
    public void BodyJsonPath_Wildcard_OnNonArray_NoMatch()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.x[*]", Exists = true },
        };
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R("{\"x\":\"not-array\"}")));
    }

    [Fact]
    public void BodyJsonPath_Wildcard_OnArray_FlattensValues()
    {
        // Wildcard expansion joins matches with \n; regex must match
        // across that — use a permissive pattern that finds a single
        // digit anywhere.
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.items[*]", Matches = "[0-9]" },
        };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R("{\"items\":[1,2,3]}")));
    }

    [Fact]
    public void BodyJsonPath_UnsupportedBracketToken_NoMatch()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.items[abc]", Exists = true },
        };
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R("{\"items\":[1]}")));
    }

    [Fact]
    public void BodyJsonPath_MalformedBracketPath_NoMatch()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.items[0", Exists = true },
        };
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R("{\"items\":[1]}")));
    }

    [Fact]
    public void BodyJsonPath_PropertyOnNonObject_NoMatch()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.foo", Exists = true },
        };
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R("\"plain-string\"")));
    }

    [Fact]
    public void BodyJsonPath_EqualsValue_MatchOnString()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.role", EqualsValue = "admin" },
        };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R("{\"role\":\"admin\"}")));
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R("{\"role\":\"user\"}")));
    }

    [Fact]
    public void BodyJsonPath_EqualsValue_OnBoolAndNumber_RenderedAsLiteral()
    {
        var pBool = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.flag", EqualsValue = "true" },
        };
        Assert.True(AttackPredicateEvaluator.Evaluate(pBool, R("{\"flag\":true}")));

        var pNum = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.n", EqualsValue = "42" },
        };
        Assert.True(AttackPredicateEvaluator.Evaluate(pNum, R("{\"n\":42}")));
    }

    [Fact]
    public void BodyJsonPath_EqualsValue_OnObject_RendersAsJsonText()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.obj", EqualsValue = "{\"a\":1}" },
        };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R("{\"obj\":{\"a\":1}}")));
    }

    [Fact]
    public void BodyJsonPath_Matches_InvalidRegex_FailsClosed()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.x", Matches = "(unclosed" },
        };
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R("{\"x\":\"a\"}")));
    }

    [Fact]
    public void BodyJsonPath_AnyValueMatches_FindsAcrossArray()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.errors[*]", AnyValueMatches = "SyntaxError" },
        };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R("{\"errors\":[\"oops\",\"SyntaxError: bad\"]}")));
    }

    [Fact]
    public void BodyJsonPath_AnyValueMatches_InvalidRegex_FailsClosed()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.x", AnyValueMatches = "(unclosed" },
        };
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R("{\"x\":\"y\"}")));
    }

    [Fact]
    public void BodyJsonPath_NullValueInPath_StringifiesToNull()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.x", EqualsValue = "null" },
        };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R("{\"x\":null}")));
    }
}
