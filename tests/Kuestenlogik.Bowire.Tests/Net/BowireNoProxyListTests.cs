// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Net;

namespace Kuestenlogik.Bowire.Tests.Net;

/// <summary>The bypass list reads the forms people paste from NO_PROXY and browser settings (#680).</summary>
public sealed class BowireNoProxyListTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/x", true)]
    [InlineData("example.com", "https://api.example.com/x", true)]
    [InlineData("example.com", "https://notexample.com/x", false)]
    [InlineData(".corp.local", "http://svc.corp.local/", true)]
    [InlineData(".corp.local", "http://corp.local/", true)]
    [InlineData("*.corp.local", "http://a.b.corp.local/", true)]
    [InlineData("api-*.test", "http://api-orders.test/", true)]
    [InlineData("api-*.test", "http://web-orders.test/", false)]
    [InlineData("EXAMPLE.com", "https://Example.COM/", true)]
    [InlineData("10.0.0.0/8", "http://10.20.30.40/", true)]
    [InlineData("10.0.0.0/8", "http://11.0.0.1/", false)]
    [InlineData("192.168.1.7", "http://192.168.1.7:8080/", true)]
    [InlineData("host.test:8443", "https://host.test:8443/", true)]
    [InlineData("host.test:8443", "https://host.test/", false)]
    [InlineData("<local>", "http://intranet/", true)]
    [InlineData("<local>", "http://intranet.corp/", false)]
    [InlineData("*", "https://anything.example/", true)]
    public void Matches_The_Usual_Forms(string list, string url, bool expected) =>
        Assert.Equal(expected, BowireNoProxyList.Parse(list).Matches(new Uri(url)));

    [Fact]
    public void Parse_Splits_On_Commas_Semicolons_And_Whitespace_And_Drops_Duplicates()
    {
        var list = BowireNoProxyList.Parse("a.test, b.test;c.test\n a.test", null, "  ");
        Assert.Equal(["a.test", "b.test", "c.test"], list.Entries);
    }

    [Fact]
    public void An_Empty_List_Bypasses_Nothing() =>
        Assert.False(BowireNoProxyList.Parse("").Matches(new Uri("https://example.com/")));
}
