// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Interceptor;

namespace Kuestenlogik.Bowire.Tests.Interceptor;

/// <summary>
/// Method-axis + gate coverage for <see cref="InterceptorMockRule.Matches"/>
/// and the internal <see cref="InterceptorMockRule.PathMatches"/> helper
/// (#308, Phase D). The path-grammar theory lives in
/// <see cref="InterceptorMockStoreTests"/>; this fixture pins the method
/// comparison (literal + case-insensitive + wildcard), the
/// <c>Enabled=false</c> short-circuit, and the empty-pattern guard that the
/// store-level tests don't reach.
/// </summary>
public sealed class InterceptorMockRuleMatchesTests
{
    [Fact]
    public void LiteralMethod_IsCaseInsensitive()
    {
        var rule = new InterceptorMockRule { PathPattern = "/api/x", Method = "GET" };
        Assert.True(rule.Matches("get", "/api/x"));
        Assert.True(rule.Matches("GET", "/api/x"));
    }

    [Fact]
    public void LiteralMethod_Mismatch_DoesNotMatch()
    {
        var rule = new InterceptorMockRule { PathPattern = "/api/x", Method = "GET" };
        Assert.False(rule.Matches("POST", "/api/x"));
    }

    [Fact]
    public void WildcardMethod_MatchesAnyVerb()
    {
        var rule = new InterceptorMockRule { PathPattern = "/api/x", Method = "*" };
        Assert.True(rule.Matches("GET", "/api/x"));
        Assert.True(rule.Matches("PATCH", "/api/x"));
    }

    [Fact]
    public void Disabled_NeverMatches_EvenOnAWildcardRule()
    {
        var rule = new InterceptorMockRule { PathPattern = "*", Method = "*", Enabled = false };
        Assert.False(rule.Matches("GET", "/anything"));
    }

    [Fact]
    public void PathMatch_IsCaseInsensitive()
    {
        var rule = new InterceptorMockRule { PathPattern = "/API/Users", Method = "*" };
        Assert.True(rule.Matches("GET", "/api/users"));
    }

    [Fact]
    public void WildcardTail_IgnoresQueryString()
    {
        var rule = new InterceptorMockRule { PathPattern = "/api/users/*", Method = "*" };
        Assert.True(rule.Matches("GET", "/api/users/42?role=admin"));
    }

    [Theory]
    [InlineData("", "/api/x", false)]
    [InlineData(null, "/api/x", false)]
    public void PathMatches_EmptyOrNullPattern_ReturnsFalse(string? pattern, string path, bool expected)
    {
        Assert.Equal(expected, InterceptorMockRule.PathMatches(pattern!, path));
    }

    [Fact]
    public void WildcardTail_PrefixWithoutSlashBoundary_DoesNotMatch()
    {
        // "/api/users/*" must not match "/api/userss" — the char after the
        // prefix has to be a path separator, not just any continuation.
        Assert.False(InterceptorMockRule.PathMatches("/api/users/*", "/api/userss"));
    }
}
