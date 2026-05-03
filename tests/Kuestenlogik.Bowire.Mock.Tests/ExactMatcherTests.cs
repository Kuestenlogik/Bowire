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
}
