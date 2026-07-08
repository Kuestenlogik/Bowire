// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Management;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Unit tests for the #409 verify / findAll API — asserting how many
/// journalled requests match a predicate, plus the near-miss listing.
/// </summary>
public sealed class MockRequestVerifierTests
{
    private static MockRequestEntry Entry(
        string method = "GET", string path = "/api/test", string outcome = "matched",
        string? query = null, Dictionary<string, string>? headers = null) =>
        new(
            Sequence: 0,
            Timestamp: DateTimeOffset.UnixEpoch,
            Method: method,
            Path: path,
            StatusCode: outcome == "404" ? 404 : 200,
            MatchedStepId: outcome == "matched" ? "step" : null,
            Outcome: outcome,
            DurationMs: 1.0,
            Query: query,
            Headers: headers);

    [Fact]
    public void Default_NoCount_SatisfiedWhenAtLeastOneMatch()
    {
        var entries = new[] { Entry(method: "POST", path: "/orders"), Entry(method: "GET", path: "/x") };
        var r = MockRequestVerifier.Verify(entries, new MockVerification { Method = "POST", Path = "/orders" });
        Assert.True(r.Satisfied);
        Assert.Equal(1, r.Count);

        var none = MockRequestVerifier.Verify(entries, new MockVerification { Path = "/nope" });
        Assert.False(none.Satisfied);
        Assert.Equal(0, none.Count);
    }

    [Fact]
    public void MethodIsCaseInsensitive_PathIsExact()
    {
        var entries = new[] { Entry(method: "post", path: "/orders") };
        Assert.True(MockRequestVerifier.Verify(entries, new MockVerification { Method = "POST", Path = "/orders" }).Satisfied);
        Assert.False(MockRequestVerifier.Verify(entries, new MockVerification { Path = "/Orders" }).Satisfied);
    }

    [Fact]
    public void CountExpectations_ExactlyAtLeastAtMost()
    {
        var entries = Enumerable.Range(0, 3).Select(_ => Entry(method: "GET", path: "/hit")).ToArray();

        Assert.True(MockRequestVerifier.Verify(entries, new MockVerification { Path = "/hit", Exactly = 3 }).Satisfied);
        Assert.False(MockRequestVerifier.Verify(entries, new MockVerification { Path = "/hit", Exactly = 2 }).Satisfied);
        Assert.True(MockRequestVerifier.Verify(entries, new MockVerification { Path = "/hit", AtLeast = 2 }).Satisfied);
        Assert.True(MockRequestVerifier.Verify(entries, new MockVerification { Path = "/hit", AtMost = 3 }).Satisfied);
        Assert.False(MockRequestVerifier.Verify(entries, new MockVerification { Path = "/hit", AtMost = 2 }).Satisfied);
    }

    [Fact]
    public void QueryAndHeaderPredicates_ViaSharedEngine()
    {
        var entries = new[]
        {
            Entry(method: "GET", path: "/search", query: "?q=cats&page=2",
                headers: new(StringComparer.OrdinalIgnoreCase) { ["X-Env"] = "prod" }),
            Entry(method: "GET", path: "/search", query: "?q=dogs"),
        };

        var byQuery = MockRequestVerifier.Verify(entries, new MockVerification
        {
            Path = "/search",
            Match = new BowireStepMatch { Query = [new() { Name = "q", EqualTo = "cats" }] },
        });
        Assert.True(byQuery.Satisfied);
        Assert.Equal(1, byQuery.Count);

        var byHeader = MockRequestVerifier.Verify(entries, new MockVerification
        {
            Match = new BowireStepMatch { Headers = [new() { Name = "X-Env", EqualTo = "prod", CaseInsensitive = true }] },
        });
        Assert.Equal(1, byHeader.Count);
    }

    [Fact]
    public void PathRegexAndGlob()
    {
        var entries = new[] { Entry(path: "/orders/123"), Entry(path: "/orders/abc"), Entry(path: "/users/1") };

        var rx = MockRequestVerifier.Verify(entries, new MockVerification
        {
            Match = new BowireStepMatch { PathRegex = "/orders/[0-9]+" },
        });
        Assert.Equal(1, rx.Count);

        var glob = MockRequestVerifier.Verify(entries, new MockVerification
        {
            Match = new BowireStepMatch { PathGlob = "/orders/*" },
        });
        Assert.Equal(2, glob.Count);
    }

    [Fact]
    public void Log_Verify_And_Unmatched()
    {
        var log = new MockRequestLog();
        log.OnRequest(Entry(method: "GET", path: "/a", outcome: "matched"));
        log.OnRequest(Entry(method: "GET", path: "/b", outcome: "miss"));
        log.OnRequest(Entry(method: "GET", path: "/c", outcome: "404"));

        Assert.True(log.Verify(new MockVerification { Path = "/a" }).Satisfied);

        var unmatched = log.Unmatched();
        Assert.Equal(2, unmatched.Count);
        Assert.All(unmatched, e => Assert.True(e.Outcome is "miss" or "404"));
    }
}
