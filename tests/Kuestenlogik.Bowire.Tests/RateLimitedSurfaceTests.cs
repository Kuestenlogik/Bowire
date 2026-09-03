// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// What the rate limiter is allowed to refuse (#625, corrected).
/// </summary>
/// <remarks>
/// <para>
/// The limiter was global: every request from one address counted, including
/// the workbench document and its five-megabyte bundle. A session that loads
/// the page a few times and then polls reaches six hundred requests inside a
/// minute without trying, and the next page load came back 429 with an empty
/// body.
/// </para>
/// <para>
/// That failure mode is the reason these exist. A 429 on an API call is an
/// answer a client can read and retry. A 429 on the document is a blank
/// window with nothing on it — indistinguishable from a crashed server, and
/// it hid behind an intermittent e2e failure until the harness was made to
/// report the response status (#637).
/// </para>
/// </remarks>
public class RateLimitedSurfaceTests
{
    [Theory]
    [InlineData("/api/plugins")]
    [InlineData("/api/flows")]
    [InlineData("/mcp")]
    [InlineData("/scim/v2/Users")]
    public void TheMachineReadableSurfacesAreLimited(string path)
        => Assert.True(BrowserUiHost.IsRateLimitedSurface(new PathString(path)));

    [Theory]
    [InlineData("/")]
    [InlineData("/bowire")]
    [InlineData("/favicon.ico")]
    [InlineData("/bowire.js")]
    public void TheWorkbenchAndItsAssetsAreNot(string path)
        => Assert.False(BrowserUiHost.IsRateLimitedSurface(new PathString(path)));

    [Fact]
    public void MatchingIsBySegment_SoALookalikePathIsNotCaughtByAccident()
    {
        // "/apiary" is not "/api". Prefix matching without the segment rule
        // would limit somebody's unrelated route and be very hard to explain.
        Assert.False(BrowserUiHost.IsRateLimitedSurface(new PathString("/apiary")));
        Assert.True(BrowserUiHost.IsRateLimitedSurface(new PathString("/api")));
    }
}
