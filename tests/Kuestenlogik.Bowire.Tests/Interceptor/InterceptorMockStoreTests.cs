// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Interceptor;

namespace Kuestenlogik.Bowire.Tests.Interceptor;

/// <summary>
/// Unit-level coverage for <see cref="InterceptorMockStore"/> + the
/// <see cref="InterceptorMockRule.Matches"/> grammar (#308, Phase D).
/// The end-to-end middleware integration lives in
/// <see cref="BowireInterceptorMiddlewareTests"/> — these tests pin the
/// matcher edge cases without booting Kestrel.
/// </summary>
public sealed class InterceptorMockStoreTests
{
    [Fact]
    public void Add_AssignsIdAndDefaults()
    {
        var store = new InterceptorMockStore();
        var rule = store.Add(new InterceptorMockRule
        {
            PathPattern = "/api/users",
            Method = "GET",
            ResponseBody = "{\"ok\":true}",
        });

        Assert.False(string.IsNullOrEmpty(rule.Id));
        Assert.StartsWith("mock_", rule.Id, StringComparison.Ordinal);
        Assert.Equal("GET /api/users", rule.Name);
        Assert.Equal(200, rule.ResponseStatus);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void Add_WithExistingId_ReplacesRule()
    {
        var store = new InterceptorMockStore();
        var first = store.Add(new InterceptorMockRule { PathPattern = "/a", Method = "GET", ResponseBody = "1" });
        var replacement = new InterceptorMockRule
        {
            Id = first.Id,
            PathPattern = "/a",
            Method = "GET",
            ResponseBody = "2",
        };

        store.Add(replacement);
        var snapshot = store.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("2", snapshot[0].ResponseBody);
    }

    [Fact]
    public void FindMatch_ReturnsFirstMatch_InInsertionOrder()
    {
        var store = new InterceptorMockStore();
        var specific = store.Add(new InterceptorMockRule { PathPattern = "/api/users/42", Method = "GET" });
        store.Add(new InterceptorMockRule { PathPattern = "/api/users/*", Method = "GET" });

        var match = store.FindMatch("GET", "/api/users/42");
        Assert.NotNull(match);
        Assert.Equal(specific.Id, match!.Id);
    }

    [Fact]
    public void FindMatch_SkipsDisabledRules()
    {
        var store = new InterceptorMockStore();
        store.Add(new InterceptorMockRule { PathPattern = "/api/users", Method = "GET", Enabled = false });
        Assert.Null(store.FindMatch("GET", "/api/users"));
    }

    [Fact]
    public void FindMatch_WildcardMethodMatchesAnyVerb()
    {
        var store = new InterceptorMockStore();
        store.Add(new InterceptorMockRule { PathPattern = "/api/widgets", Method = "*" });
        Assert.NotNull(store.FindMatch("GET", "/api/widgets"));
        Assert.NotNull(store.FindMatch("POST", "/api/widgets"));
        Assert.NotNull(store.FindMatch("DELETE", "/api/widgets"));
    }

    [Theory]
    [InlineData("/api/users/*", "/api/users", true)]
    [InlineData("/api/users/*", "/api/users/42", true)]
    [InlineData("/api/users/*", "/api/users/42/posts", true)]
    [InlineData("/api/users/*", "/api/userss", false)]
    [InlineData("/api/users", "/api/users", true)]
    [InlineData("/api/users", "/api/users?q=1", true)]
    [InlineData("/api/users", "/api/users/42", false)]
    [InlineData("*", "/anything/at/all", true)]
    public void PathMatches_GrammarEdgeCases(string pattern, string path, bool expected)
    {
        var rule = new InterceptorMockRule { PathPattern = pattern, Method = "*" };
        Assert.Equal(expected, rule.Matches("GET", path));
    }

    [Fact]
    public void Remove_ExistingRule_ReturnsTrue_AndRemoves()
    {
        var store = new InterceptorMockStore();
        var rule = store.Add(new InterceptorMockRule { PathPattern = "/a" });
        Assert.True(store.Remove(rule.Id));
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Remove_UnknownRule_ReturnsFalse()
    {
        var store = new InterceptorMockStore();
        Assert.False(store.Remove("missing"));
    }

    [Fact]
    public void Clear_EmptiesStore()
    {
        var store = new InterceptorMockStore();
        store.Add(new InterceptorMockRule { PathPattern = "/a" });
        store.Add(new InterceptorMockRule { PathPattern = "/b" });
        store.Clear();
        Assert.Empty(store.Snapshot());
    }
}
