// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mock.Tests;

public sealed class ExactMatcherTests
{
    private static BowireRecording MakeRecording(params BowireRecordingStep[] steps)
    {
        var rec = new BowireRecording { RecordingFormatVersion = 1 };
        foreach (var s in steps) rec.Steps.Add(s);
        return rec;
    }

    private static BowireRecordingStep RestStep(string verb, string path, string? response = null) => new()
    {
        Id = "step_" + Guid.NewGuid().ToString("N")[..8],
        Protocol = "rest",
        Service = "WeatherService",
        Method = "GetForecast",
        MethodType = "Unary",
        HttpPath = path,
        HttpVerb = verb,
        Status = "OK",
        Response = response ?? "{}"
    };

    private static MockRequest Req(string method, string path) => new()
    {
        Protocol = "rest",
        HttpMethod = method,
        Path = path
    };

    [Fact]
    public void TryMatch_OnExactPair_ReturnsStep()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(RestStep("GET", "/weather", """{"temp":21}"""));

        Assert.True(matcher.TryMatch(Req("GET", "/weather"), rec, out var step));
        Assert.NotNull(step);
        Assert.Equal("""{"temp":21}""", step.Response);
    }

    [Fact]
    public void TryMatch_VerbIsCaseInsensitive()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(RestStep("GET", "/weather"));

        Assert.True(matcher.TryMatch(Req("get", "/weather"), rec, out _));
    }

    [Fact]
    public void TryMatch_PathIsCaseSensitive()
    {
        // Path case-sensitivity matches the HTTP spec; a GET /Weather call
        // should NOT match a recorded /weather step.
        var matcher = new ExactMatcher();
        var rec = MakeRecording(RestStep("GET", "/weather"));

        Assert.False(matcher.TryMatch(Req("GET", "/Weather"), rec, out _));
    }

    [Fact]
    public void TryMatch_OnWrongVerb_ReturnsFalse()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(RestStep("GET", "/weather"));

        Assert.False(matcher.TryMatch(Req("POST", "/weather"), rec, out _));
    }

    [Fact]
    public void TryMatch_OnNonRestStep_IsSkipped()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(new BowireRecordingStep
        {
            Protocol = "grpc",
            MethodType = "Unary",
            Service = "Svc",
            Method = "M",
            // No httpPath / httpVerb.
            Status = "OK"
        });

        Assert.False(matcher.TryMatch(Req("GET", "/weather"), rec, out _));
    }

    [Fact]
    public void TryMatch_OnStreamingRestStep_StillMatches_ReplayerHandlesMethodType()
    {
        // Phase 2c: streaming steps are no longer filtered by the matcher —
        // the replayer decides what to do based on (protocol, methodType).
        // A ServerStreaming REST step with matching verb+path matches; the
        // UnaryReplayer's SSE branch takes it from there.
        var matcher = new ExactMatcher();
        var rec = MakeRecording(new BowireRecordingStep
        {
            Protocol = "rest",
            MethodType = "ServerStreaming",
            Service = "Svc",
            Method = "M",
            HttpPath = "/weather",
            HttpVerb = "GET",
            Status = "OK"
        });

        Assert.True(matcher.TryMatch(Req("GET", "/weather"), rec, out var step));
        Assert.Equal("ServerStreaming", step.MethodType);
    }

    [Fact]
    public void TryMatch_PicksFirstMatchInOrder()
    {
        var matcher = new ExactMatcher();
        var first = RestStep("GET", "/weather", """{"temp":1}""");
        var second = RestStep("GET", "/weather", """{"temp":2}""");
        var rec = MakeRecording(first, second);

        Assert.True(matcher.TryMatch(Req("GET", "/weather"), rec, out var matched));
        Assert.Equal("""{"temp":1}""", matched.Response);
    }

    // ---- gRPC matching ----

    private static BowireRecordingStep GrpcStep(string service, string method, string? responseBinary = "CCo=") => new()
    {
        Id = "step_" + Guid.NewGuid().ToString("N")[..8],
        Protocol = "grpc",
        Service = service,
        Method = method,
        MethodType = "Unary",
        Status = "OK",
        ResponseBinary = responseBinary
    };

    private static MockRequest GrpcReq(string path) => new()
    {
        Protocol = "grpc",
        HttpMethod = "POST",
        Path = path,
        ContentType = "application/grpc"
    };

    [Fact]
    public void TryMatch_OnGrpcRequest_MatchesByFullServiceAndMethodPath()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(GrpcStep("calc.Calculator", "Add"));

        Assert.True(matcher.TryMatch(GrpcReq("/calc.Calculator/Add"), rec, out var step));
        Assert.Equal("calc.Calculator", step.Service);
        Assert.Equal("Add", step.Method);
    }

    [Fact]
    public void TryMatch_OnGrpcRequest_IgnoresRestSteps()
    {
        var matcher = new ExactMatcher();
        // REST step happens to share the service-path-shape — the gRPC
        // matcher should NOT pick it up.
        var rec = MakeRecording(RestStep("POST", "/calc.Calculator/Add"));

        Assert.False(matcher.TryMatch(GrpcReq("/calc.Calculator/Add"), rec, out _));
    }

    [Fact]
    public void TryMatch_OnGrpcRequest_WithWrongMethodName_ReturnsFalse()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(GrpcStep("calc.Calculator", "Add"));

        Assert.False(matcher.TryMatch(GrpcReq("/calc.Calculator/Subtract"), rec, out _));
    }

    [Fact]
    public void TryMatch_OnRestRequest_IgnoresGrpcSteps()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(GrpcStep("calc.Calculator", "Add"));

        Assert.False(matcher.TryMatch(Req("POST", "/calc.Calculator/Add"), rec, out _));
    }

    [Fact]
    public void TryMatch_OnGrpcRequest_ServicePathWithDots_IsTreatedAsFullName()
    {
        // gRPC uses dotted package names routinely (e.g. "google.protobuf.Empty").
        // The matcher should concatenate service + "/" + method verbatim, not
        // try to parse the dots as path segments.
        var matcher = new ExactMatcher();
        var rec = MakeRecording(GrpcStep("google.pubsub.v1.Publisher", "CreateTopic"));

        Assert.True(matcher.TryMatch(
            GrpcReq("/google.pubsub.v1.Publisher/CreateTopic"), rec, out _));
    }

    // ---- Path-template matching (Phase 2a) ----

    [Fact]
    public void TryMatch_OnTemplatePath_BindsSingleSegment()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(RestStep("GET", "/users/{id}", """{"id":"any"}"""));

        // Different concrete IDs all match the same recorded step.
        Assert.True(matcher.TryMatch(Req("GET", "/users/42"), rec, out _));
        Assert.True(matcher.TryMatch(Req("GET", "/users/abc"), rec, out _));
        Assert.True(matcher.TryMatch(Req("GET", "/users/00000000-0000-0000-0000-000000000000"), rec, out _));
    }

    [Fact]
    public void TryMatch_OnTemplatePath_RejectsExtraSegments()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(RestStep("GET", "/users/{id}"));

        Assert.False(matcher.TryMatch(Req("GET", "/users/42/extra"), rec, out _));
        Assert.False(matcher.TryMatch(Req("GET", "/users"), rec, out _));
        Assert.False(matcher.TryMatch(Req("GET", "/users/"), rec, out _));
    }

    [Fact]
    public void TryMatch_OnTemplatePath_WithMultipleParameters_MatchesAll()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(RestStep("GET", "/users/{uid}/posts/{pid}"));

        Assert.True(matcher.TryMatch(Req("GET", "/users/42/posts/7"), rec, out _));
        Assert.False(matcher.TryMatch(Req("GET", "/users/42/posts"), rec, out _));
        Assert.False(matcher.TryMatch(Req("GET", "/users/42/posts/7/comments"), rec, out _));
    }

    [Fact]
    public void TryMatch_OnTemplatePath_WithSpecialRegexChars_EscapesLiterals()
    {
        // Literal '.' in a path segment between templates should be treated as
        // a literal dot, not as the regex 'any character' meta.
        var matcher = new ExactMatcher();
        var rec = MakeRecording(RestStep("GET", "/files/{name}.txt"));

        Assert.True(matcher.TryMatch(Req("GET", "/files/report.txt"), rec, out _));
        // 'xtxt' would match if '.' were treated as regex dot; it shouldn't.
        Assert.False(matcher.TryMatch(Req("GET", "/files/reportxtxt"), rec, out _));
    }

    [Fact]
    public void TryMatch_LiteralPath_StillMatchesExactly()
    {
        // Paths without '{...}' placeholders stay on the fast literal path —
        // template handling doesn't regress existing Phase-1a matching.
        var matcher = new ExactMatcher();
        var rec = MakeRecording(RestStep("GET", "/weather"));

        Assert.True(matcher.TryMatch(Req("GET", "/weather"), rec, out _));
        Assert.False(matcher.TryMatch(Req("GET", "/WEATHER"), rec, out _));  // path case-sensitive
        Assert.False(matcher.TryMatch(Req("GET", "/weather/extra"), rec, out _));
    }

    // ---- Body-bound disambiguation across siblings sharing a template ----

    private static BowireRecordingStep RestStepWithBody(
        string verb, string path, string body, string response) => new()
    {
        Id = "step_" + Guid.NewGuid().ToString("N")[..8],
        Protocol = "rest",
        Service = "PetService",
        Method = "GetPetById",
        MethodType = "Unary",
        HttpPath = path,
        HttpVerb = verb,
        Body = body,
        Status = "OK",
        Response = response
    };

    [Fact]
    public void TryMatch_OnTemplatePath_PrefersStepWhoseBodyBindingMatchesRequestPath()
    {
        // Three captures of GET /pet/{petId} with petId = 3, 5, 10. A mock
        // call against /pet/5 must return the response captured for petId=5,
        // not the first matching template hit.
        var matcher = new ExactMatcher();
        var rec = MakeRecording(
            RestStepWithBody("GET", "/pet/{petId}", """{"petId":3}""", """{"id":3}"""),
            RestStepWithBody("GET", "/pet/{petId}", """{"petId":5}""", """{"id":5}"""),
            RestStepWithBody("GET", "/pet/{petId}", """{"petId":10}""", """{"id":10}"""));

        Assert.True(matcher.TryMatch(Req("GET", "/pet/5"), rec, out var step));
        Assert.Equal("""{"id":5}""", step.Response);
    }

    [Fact]
    public void TryMatch_OnTemplatePath_BodyBindingHandlesStringValues()
    {
        // Path bindings arrive as URL segments (strings); the recorded body
        // may carry the value as a string literal. Compare on the canonical
        // text form so quoted-string captures still match.
        var matcher = new ExactMatcher();
        var rec = MakeRecording(
            RestStepWithBody("GET", "/users/{id}", """{"id":"alice"}""", """{"name":"A"}"""),
            RestStepWithBody("GET", "/users/{id}", """{"id":"bob"}""", """{"name":"B"}"""));

        Assert.True(matcher.TryMatch(Req("GET", "/users/bob"), rec, out var step));
        Assert.Equal("""{"name":"B"}""", step.Response);
    }

    [Fact]
    public void TryMatch_OnTemplatePath_NoBodyBindingMatch_FallsBackToFirstHit()
    {
        // None of the recorded steps carry a body field that maps to the
        // request's path binding. The matcher has no signal to pick between
        // them — the historical "first match in capture order wins" tie-break
        // takes over so legacy recordings keep working.
        var matcher = new ExactMatcher();
        var rec = MakeRecording(
            RestStepWithBody("GET", "/pet/{petId}", "{}", """{"id":1}"""),
            RestStepWithBody("GET", "/pet/{petId}", "{}", """{"id":2}"""));

        Assert.True(matcher.TryMatch(Req("GET", "/pet/42"), rec, out var step));
        Assert.Equal("""{"id":1}""", step.Response);
    }

    [Fact]
    public void TryMatch_OnTemplatePath_MalformedBody_FallsBackToFirstHit()
    {
        // A recording whose body isn't parseable JSON (e.g. legacy free-text
        // capture) shouldn't blow up the matcher — it just scores zero and
        // the first template hit still wins.
        var matcher = new ExactMatcher();
        var rec = MakeRecording(
            RestStepWithBody("GET", "/pet/{petId}", "not-json", """{"id":1}"""),
            RestStepWithBody("GET", "/pet/{petId}", """{"petId":7}""", """{"id":7}"""));

        // Request for pet 7 still finds the right body-bound step.
        Assert.True(matcher.TryMatch(Req("GET", "/pet/7"), rec, out var step7));
        Assert.Equal("""{"id":7}""", step7.Response);

        // Request for an id that nobody recorded falls back to the first
        // template hit (the malformed-body one).
        Assert.True(matcher.TryMatch(Req("GET", "/pet/99"), rec, out var stepFallback));
        Assert.Equal("""{"id":1}""", stepFallback.Response);
    }

    [Fact]
    public void TryMatch_OnTemplatePath_MultipleBindings_PrefersBestBodyOverlap()
    {
        // Two-parameter template /users/{uid}/posts/{pid}: the step that
        // matches BOTH bindings outranks the one that matches only one.
        var matcher = new ExactMatcher();
        var rec = MakeRecording(
            RestStepWithBody("GET", "/users/{uid}/posts/{pid}",
                """{"uid":42,"pid":1}""", """{"post":"first"}"""),
            RestStepWithBody("GET", "/users/{uid}/posts/{pid}",
                """{"uid":42,"pid":7}""", """{"post":"seventh"}"""));

        Assert.True(matcher.TryMatch(Req("GET", "/users/42/posts/7"), rec, out var step));
        Assert.Equal("""{"post":"seventh"}""", step.Response);
    }

    // ---------------- #402: predicates, regex/glob paths, priority ----------------

    private static MockRequest ReqEx(
        string method, string path, string? query = null,
        Dictionary<string, string>? headers = null) => new()
    {
        Protocol = "rest",
        HttpMethod = method,
        Path = path,
        Query = query,
        Headers = headers ?? new(StringComparer.OrdinalIgnoreCase),
    };

    private static BowireRecordingStep RestStepMatched(
        string verb, string path, BowireStepMatch match, string response) => new()
    {
        Id = "step_" + Guid.NewGuid().ToString("N")[..8],
        Protocol = "rest",
        Service = "S",
        Method = "M",
        MethodType = "Unary",
        HttpPath = path,
        HttpVerb = verb,
        Status = "OK",
        Response = response,
        Match = match,
    };

    [Fact]
    public void QueryPredicate_Equals_GatesTheMatch()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(RestStepMatched("GET", "/search",
            new BowireStepMatch { Query = [new() { Name = "q", EqualTo = "cats" }] }, """{"hit":true}"""));

        Assert.True(matcher.TryMatch(ReqEx("GET", "/search", "?q=cats"), rec, out var ok));
        Assert.Equal("""{"hit":true}""", ok.Response);
        Assert.False(matcher.TryMatch(ReqEx("GET", "/search", "?q=dogs"), rec, out _));
        Assert.False(matcher.TryMatch(Req("GET", "/search"), rec, out _)); // absent
    }

    [Fact]
    public void HeaderPredicate_IsCaseInsensitiveByName_AndSupportsAbsent()
    {
        var matcher = new ExactMatcher();
        var needsAuth = MakeRecording(RestStepMatched("GET", "/secure",
            new BowireStepMatch { Headers = [new() { Name = "Authorization", Present = true }] }, """{"ok":1}"""));

        Assert.True(matcher.TryMatch(
            ReqEx("GET", "/secure", headers: new(StringComparer.OrdinalIgnoreCase) { ["authorization"] = "Bearer x" }),
            needsAuth, out _));
        Assert.False(matcher.TryMatch(Req("GET", "/secure"), needsAuth, out _));

        var anon = MakeRecording(RestStepMatched("GET", "/secure",
            new BowireStepMatch { Headers = [new() { Name = "Authorization", Present = false }] }, """{"anon":1}"""));
        Assert.True(matcher.TryMatch(Req("GET", "/secure"), anon, out var a));
        Assert.Equal("""{"anon":1}""", a.Response);
    }

    [Fact]
    public void CookiePredicate_MatchesFromCookieHeader()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(RestStepMatched("GET", "/me",
            new BowireStepMatch { Cookies = [new() { Name = "session", Contains = "abc" }] }, """{"user":"x"}"""));

        Assert.True(matcher.TryMatch(
            ReqEx("GET", "/me", headers: new(StringComparer.OrdinalIgnoreCase) { ["Cookie"] = "theme=dark; session=abc123" }),
            rec, out _));
        Assert.False(matcher.TryMatch(
            ReqEx("GET", "/me", headers: new(StringComparer.OrdinalIgnoreCase) { ["Cookie"] = "theme=dark" }),
            rec, out _));
    }

    [Fact]
    public void PathRegex_MatchesFamilyOfPaths()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(RestStepMatched("GET", null!,
            new BowireStepMatch { PathRegex = "/orders/[0-9]+" }, """{"order":1}"""));

        Assert.True(matcher.TryMatch(Req("GET", "/orders/123"), rec, out _));
        Assert.False(matcher.TryMatch(Req("GET", "/orders/abc"), rec, out _));
        Assert.False(matcher.TryMatch(Req("GET", "/orders/123/items"), rec, out _)); // anchored
    }

    [Fact]
    public void PathGlob_SingleAndDoubleStar()
    {
        var matcher = new ExactMatcher();
        var single = MakeRecording(RestStepMatched("GET", null!,
            new BowireStepMatch { PathGlob = "/api/*/health" }, """{"g":"single"}"""));
        Assert.True(matcher.TryMatch(Req("GET", "/api/v1/health"), single, out _));
        Assert.False(matcher.TryMatch(Req("GET", "/api/v1/x/health"), single, out _)); // * is one segment

        var deep = MakeRecording(RestStepMatched("GET", null!,
            new BowireStepMatch { PathGlob = "/static/**" }, """{"g":"deep"}"""));
        Assert.True(matcher.TryMatch(Req("GET", "/static/css/app.css"), deep, out _));
    }

    [Fact]
    public void Priority_OverridesLiteralBeatsTemplateAndPicksHigher()
    {
        var matcher = new ExactMatcher();
        // A template step with priority 10 outranks a literal step (priority 0)
        // for the same path — explicit priority dominates the heuristic.
        var rec = MakeRecording(
            RestStep("GET", "/thing", """{"who":"literal"}"""),
            RestStepMatched("GET", "/{x}", new BowireStepMatch { Priority = 10 }, """{"who":"priority"}"""));

        Assert.True(matcher.TryMatch(Req("GET", "/thing"), rec, out var step));
        Assert.Equal("""{"who":"priority"}""", step.Response);
    }

    [Fact]
    public void NegativePriority_ActsAsFallback()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(
            RestStepMatched("GET", "/{x}", new BowireStepMatch { PathGlob = null, Priority = -1 }, """{"who":"fallback"}"""),
            RestStep("GET", "/specific", """{"who":"specific"}"""));

        Assert.True(matcher.TryMatch(Req("GET", "/specific"), rec, out var hit));
        Assert.Equal("""{"who":"specific"}""", hit.Response);
        // A path only the fallback template matches still resolves to it.
        Assert.True(matcher.TryMatch(Req("GET", "/anything"), rec, out var fb));
        Assert.Equal("""{"who":"fallback"}""", fb.Response);
    }

    [Fact]
    public void AllPredicatesMustPass()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(RestStepMatched("GET", "/x", new BowireStepMatch
        {
            Query = [new() { Name = "a", EqualTo = "1" }],
            Headers = [new() { Name = "X-Env", EqualTo = "prod", CaseInsensitive = true }],
        }, """{"ok":1}"""));

        Assert.True(matcher.TryMatch(
            ReqEx("GET", "/x", "?a=1", new(StringComparer.OrdinalIgnoreCase) { ["X-Env"] = "PROD" }), rec, out _));
        // header wrong → no match even though query passes
        Assert.False(matcher.TryMatch(
            ReqEx("GET", "/x", "?a=1", new(StringComparer.OrdinalIgnoreCase) { ["X-Env"] = "dev" }), rec, out _));
    }

    // ---- MockMatchPredicates helper units ----

    [Theory]
    [InlineData("/api/*/health", "/api/v1/health", true)]
    [InlineData("/api/*/health", "/api/v1/v2/health", false)]
    [InlineData("/static/**", "/static/a/b/c.js", true)]
    [InlineData("/f?o", "/f/o", false)] // '?' matches one non-'/' char, not a slash
    [InlineData("/f?o", "/fao", true)]
    [InlineData("/a.b", "/aXb", false)] // '.' stays literal
    public void GlobToRegex_Matches(string glob, string path, bool expected)
        => Assert.Equal(expected, Kuestenlogik.Bowire.Mock.Matchers.MockMatchPredicates.GlobToRegex(glob).IsMatch(path));

    [Fact]
    public void ParseQuery_DecodesAndAccumulates()
    {
        var q = Kuestenlogik.Bowire.Mock.Matchers.MockMatchPredicates.ParseQuery("?a=1&a=2&b=hello%20world&c");
        Assert.Equal(2, q["a"].Count);
        Assert.Equal("1", q["a"][0]);
        Assert.Equal("2", q["a"][1]);
        Assert.Equal("hello world", q["b"][0]);
        Assert.Equal("", q["c"][0]);
    }

    [Fact]
    public void EvaluatePredicate_PresenceAndOperators()
    {
        var mp = typeof(Kuestenlogik.Bowire.Mock.Matchers.MockMatchPredicates);
        // present-only passes when a value exists
        Assert.True(Kuestenlogik.Bowire.Mock.Matchers.MockMatchPredicates.EvaluatePredicate(
            new BowireMatchPredicate { Name = "x" }, ["v"]));
        // absent required
        Assert.True(Kuestenlogik.Bowire.Mock.Matchers.MockMatchPredicates.EvaluatePredicate(
            new BowireMatchPredicate { Name = "x", Present = false }, null));
        // matches regex
        Assert.True(Kuestenlogik.Bowire.Mock.Matchers.MockMatchPredicates.EvaluatePredicate(
            new BowireMatchPredicate { Name = "x", Matches = "^ab.$" }, ["abc"]));
        Assert.False(Kuestenlogik.Bowire.Mock.Matchers.MockMatchPredicates.EvaluatePredicate(
            new BowireMatchPredicate { Name = "x", Matches = "^ab.$" }, ["zzz"]));
        _ = mp;
    }
}
