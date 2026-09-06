// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The "was I explicitly asked for?" marker.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BowireServerUrl.Parse"/> consumes the <c>hint@</c> prefix
/// before any plugin is reached, so a plugin cannot see it. Plugins that
/// must only act when pinned — a bundled-schema discovery, an ad-hoc
/// separate-target fallback — need that bit delivered some other way, and
/// SSE and SignalR each grew a private marker for it.
/// </para>
/// <para>
/// TacticalAPI is why this is now one shared marker rather than three
/// private ones: it gated on the <c>tacticalapi@</c> prefix instead, which
/// can never arrive, so its discovery returned an empty list on every path
/// including its own sample's. Its unit test passed because it called
/// <c>DiscoverAsync</c> with a prefix production never delivers.
/// </para>
/// </remarks>
public sealed class PluginHintMarkerTests
{
    [Fact]
    public void ParseStripsTheHint_WhichIsWhyTheMarkerExists()
    {
        // The premise. If this ever changed, a plugin could read the
        // prefix and none of the rest would be needed.
        var (hint, url) = BowireServerUrl.Parse("tacticalapi@http://localhost:5191");

        Assert.Equal("tacticalapi", hint);
        Assert.Equal("http://localhost:5191", url);
        Assert.DoesNotContain("tacticalapi@", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:5191")]
    [InlineData("http://localhost:5191/hubs/chat")]
    [InlineData("http://localhost:5191?existing=1")]
    [InlineData("http://localhost:5191/stream?a=1&b=2")]
    public void RoundTrips(string url)
    {
        var marked = BowireServerUrl.WithPluginHint(url, "sse");

        Assert.True(BowireServerUrl.HasPluginHint(marked, "sse"));
        Assert.Equal(url, BowireServerUrl.StripPluginHint(marked));
    }

    [Fact]
    public void StrippingKeepsTheQueryWellFormed()
    {
        // Dropping a marker that landed first has to promote the next
        // parameter, or the URL keeps a stray '&' where its query starts.
        var marked = BowireServerUrl.WithPluginHint("http://host/stream", "sse") + "&keep=1";

        Assert.Equal("http://host/stream?keep=1", BowireServerUrl.StripPluginHint(marked));
    }

    [Fact]
    public void AnswersOnlyForThePluginThatWasPinned()
    {
        var marked = BowireServerUrl.WithPluginHint("http://host/stream", "sse");

        Assert.True(BowireServerUrl.HasPluginHint(marked, "sse"));
        Assert.False(BowireServerUrl.HasPluginHint(marked, "signalr"));
    }

    [Fact]
    public void MatchesTheIdCaseInsensitively()
    {
        // Parse leaves the hint opaque and the router matches it
        // case-insensitively, so a plugin asking the question must not be
        // stricter than the router that answered it.
        var marked = BowireServerUrl.WithPluginHint("http://host", "SignalR");

        Assert.True(BowireServerUrl.HasPluginHint(marked, "signalr"));
        Assert.True(BowireServerUrl.HasPluginHint(marked, "SIGNALR"));
    }

    [Fact]
    public void IgnoresTheNameAppearingElsewhereInTheUrl()
    {
        // Only a query parameter counts. A path segment that happens to
        // spell the marker is part of someone's URL, not our routing.
        var url = "http://host/__bowirePluginHint=sse/stream";

        Assert.False(BowireServerUrl.HasPluginHint(url, "sse"));
        Assert.Equal(url, BowireServerUrl.StripPluginHint(url));
    }

    [Fact]
    public void UnmarkedUrlsAreUntouched()
    {
        const string url = "http://host/stream?a=1";

        Assert.False(BowireServerUrl.HasPluginHint(url, "sse"));
        Assert.Equal(url, BowireServerUrl.StripPluginHint(url));
        Assert.Equal(url, BowireServerUrl.WithPluginHint(url, ""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void HandlesAbsentUrls(string? url)
    {
        Assert.False(BowireServerUrl.HasPluginHint(url, "sse"));
        Assert.Equal(string.Empty, BowireServerUrl.StripPluginHint(url));
    }
}
