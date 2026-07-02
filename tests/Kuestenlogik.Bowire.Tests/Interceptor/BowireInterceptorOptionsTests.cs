// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Interceptor;

namespace Kuestenlogik.Bowire.Tests.Interceptor;

/// <summary>
/// Pins the documented defaults on <see cref="BowireInterceptorOptions"/>
/// (#153 acceptance criteria: 1 MiB body cap, 1000-flow ring buffer, the
/// workbench's own <c>/bowire</c> surface excluded, master + mock gates
/// on). These are load-bearing — the middleware and the flow store both
/// read them, and the issue's "identical responses for non-modified
/// traffic" contract assumes the ignore list already covers Bowire's own
/// endpoints.
/// </summary>
public sealed class BowireInterceptorOptionsTests
{
    [Fact]
    public void Defaults_MatchDocumentedAcceptanceCriteria()
    {
        var options = new BowireInterceptorOptions();

        Assert.Equal(1024 * 1024, options.MaxBodyBytes);
        Assert.Equal(1000, options.MaxRetainedFlows);
        Assert.True(options.Enabled);
        Assert.True(options.MocksEnabled);
    }

    [Fact]
    public void IgnoredPathPrefixes_DefaultExcludesBowireSurface()
    {
        var options = new BowireInterceptorOptions();

        Assert.Contains("/bowire", options.IgnoredPathPrefixes);
    }

    [Fact]
    public void IgnoredPathPrefixes_IsMutable_SoOperatorsCanMuteRoutes()
    {
        var options = new BowireInterceptorOptions();

        options.IgnoredPathPrefixes.Add("/health");

        Assert.Contains("/health", options.IgnoredPathPrefixes);
        Assert.Contains("/bowire", options.IgnoredPathPrefixes);
    }

    [Fact]
    public void Setters_RoundTrip()
    {
        var options = new BowireInterceptorOptions
        {
            MaxBodyBytes = 512,
            MaxRetainedFlows = 5,
            Enabled = false,
            MocksEnabled = false,
        };

        Assert.Equal(512, options.MaxBodyBytes);
        Assert.Equal(5, options.MaxRetainedFlows);
        Assert.False(options.Enabled);
        Assert.False(options.MocksEnabled);
    }
}
