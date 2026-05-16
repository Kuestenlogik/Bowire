// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the predicate engine driving <c>bowire scan</c>. Each
/// operator (status / statusIn / bodyContains / bodyMatches /
/// bodyJsonPath / headerEquals / headerExists / headerMissing /
/// latencyMsAtLeast / allOf / anyOf / not) gets a positive and a
/// negative case. The JSONPath walker has its own focused tests
/// alongside since the subset is implemented in-house (not a
/// JSONPath library).
/// </summary>
public sealed class AttackPredicateEvaluatorTests
{
    private static AttackProbeResponse R(int status = 200, string body = "", int latency = 0,
        IReadOnlyDictionary<string, string>? headers = null) => new()
    {
        Status = status,
        Body = body,
        LatencyMs = latency,
        Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void EmptyPredicate_MatchesEverything()
    {
        Assert.True(AttackPredicateEvaluator.Evaluate(new AttackPredicate(), R()));
    }

    // ---- status operators ---------------------------------------

    [Fact]
    public void Status_Equals_MatchAndMiss()
    {
        var p = new AttackPredicate { Status = 200 };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(status: 200)));
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(status: 401)));
    }

    [Fact]
    public void StatusIn_MatchAndMiss()
    {
        var p = new AttackPredicate { StatusIn = new[] { 200, 201, 202 } };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(status: 201)));
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(status: 401)));
    }

    // ---- body operators -----------------------------------------

    [Fact]
    public void BodyContains_MatchAndMiss()
    {
        var p = new AttackPredicate { BodyContains = "Admin" };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(body: "the Admin service is reachable")));
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(body: "the user service is reachable")));
    }

    [Fact]
    public void BodyMatches_RegexMatchAndMiss()
    {
        var p = new AttackPredicate { BodyMatches = @"service\s+\w+\s+is\s+reachable" };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(body: "service Admin is reachable")));
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(body: "no banner here")));
    }

    [Fact]
    public void BodyMatches_InvalidRegex_TreatedAsNoMatch()
    {
        // Templates with malformed regex should fail closed (don't fire
        // the finding) rather than throw — every other template in the
        // corpus still runs.
        var p = new AttackPredicate { BodyMatches = "[unclosed-bracket" };
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(body: "anything")));
    }

    // ---- bodyJsonPath -------------------------------------------

    [Fact]
    public void BodyJsonPath_Exists_True()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.data.services", Exists = true },
        };
        var body = """{ "data": { "services": ["AdminService"] } }""";
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(body: body)));

        var bodyMissing = """{ "data": {} }""";
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(body: bodyMissing)));
    }

    [Fact]
    public void BodyJsonPath_Exists_FalseInverts()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.errors", Exists = false },
        };
        var bodyClean = """{ "data": { "x": 1 } }""";
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(body: bodyClean)));

        var bodyErr = """{ "errors": [{ "msg": "..." }] }""";
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(body: bodyErr)));
    }

    [Fact]
    public void BodyJsonPath_Equals_MatchesByStringifiedValue()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.kind", EqualsValue = "admin" },
        };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(body: """{ "kind": "admin" }""")));
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(body: """{ "kind": "user" }""")));
    }

    [Fact]
    public void BodyJsonPath_AnyValueMatches_WalksArrayWildcard()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause
            {
                Path = "$.services[*].name",
                AnyValueMatches = ".*Admin.*",
            },
        };
        var body = """{ "services": [{ "name": "UserService" }, { "name": "AdminService" }] }""";
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(body: body)));

        var bodyClean = """{ "services": [{ "name": "UserService" }, { "name": "ProfileService" }] }""";
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(body: bodyClean)));
    }

    [Fact]
    public void BodyJsonPath_OnInvalidJson_TreatedAsNotExisting()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.foo", Exists = true },
        };
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(body: "not-json-{{{}}}")));
    }

    [Fact]
    public void BodyJsonPath_ArrayIndex_PicksOneElement()
    {
        var p = new AttackPredicate
        {
            BodyJsonPath = new AttackJsonPathClause { Path = "$.items[1].id", EqualsValue = "second" },
        };
        var body = """{ "items": [{ "id": "first" }, { "id": "second" }, { "id": "third" }] }""";
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(body: body)));
    }

    // ---- header operators ---------------------------------------

    [Fact]
    public void HeaderEquals_CaseInsensitive()
    {
        var p = new AttackPredicate
        {
            HeaderEquals = new Dictionary<string, string> { ["X-Powered-By"] = "ASP.NET" },
        };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-powered-by"] = "ASP.NET",
        };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(headers: headers)));
    }

    [Fact]
    public void HeaderExists_FiresOnPresence()
    {
        var p = new AttackPredicate { HeaderExists = new[] { "Server" } };
        var withServer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Server"] = "kestrel" };
        var withoutServer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(headers: withServer)));
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(headers: withoutServer)));
    }

    [Fact]
    public void HeaderMissing_FiresOnAbsence()
    {
        // The bread-and-butter "missing security headers" check — flag
        // the response when X-Frame-Options ISN'T there.
        var p = new AttackPredicate { HeaderMissing = new[] { "X-Frame-Options" } };
        var safeHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Frame-Options"] = "DENY" };
        var unsafeHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(headers: safeHeaders)));
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(headers: unsafeHeaders)));
    }

    // ---- latency operator ---------------------------------------

    [Fact]
    public void LatencyMsAtLeast_MatchAndMiss()
    {
        var p = new AttackPredicate { LatencyMsAtLeast = 5000 };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(latency: 5200)));
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(latency: 200)));
    }

    // ---- composite operators ------------------------------------

    [Fact]
    public void AllOf_RequiresEveryChild()
    {
        var p = new AttackPredicate
        {
            AllOf = new[]
            {
                new AttackPredicate { Status = 200 },
                new AttackPredicate { BodyContains = "admin" },
            },
        };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(status: 200, body: "admin service exposed")));
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(status: 200, body: "user service exposed")));
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(status: 401, body: "admin service exposed")));
    }

    [Fact]
    public void AnyOf_RequiresOneChild()
    {
        var p = new AttackPredicate
        {
            AnyOf = new[]
            {
                new AttackPredicate { BodyContains = "Admin" },
                new AttackPredicate { BodyContains = "Internal" },
                new AttackPredicate { BodyContains = "Diagnostic" },
            },
        };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(body: "Internal-only endpoint")));
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(body: "User endpoint")));
    }

    [Fact]
    public void Not_InvertsChild()
    {
        var p = new AttackPredicate
        {
            Not = new AttackPredicate { HeaderExists = new[] { "Strict-Transport-Security" } },
        };
        var withHsts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Strict-Transport-Security"] = "max-age=31536000",
        };
        var withoutHsts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(headers: withHsts)));
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(headers: withoutHsts)));
    }

    [Fact]
    public void LeafOperators_OnSameNode_AreImplicitAnd()
    {
        // status + bodyContains on one node both have to match — every
        // operator on a single node combines via implicit AND, mirroring
        // the ADR's predicate semantics.
        var p = new AttackPredicate { Status = 200, BodyContains = "admin" };
        Assert.True(AttackPredicateEvaluator.Evaluate(p, R(status: 200, body: "admin")));
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(status: 200, body: "")));
        Assert.False(AttackPredicateEvaluator.Evaluate(p, R(status: 401, body: "admin")));
    }
}
