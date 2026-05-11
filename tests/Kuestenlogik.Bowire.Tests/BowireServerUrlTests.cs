// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

public sealed class BowireServerUrlTests
{
    [Fact]
    public void Parse_Plain_Url_Has_No_Hint()
    {
        var (hint, url) = BowireServerUrl.Parse("https://api.example.com");

        Assert.Null(hint);
        Assert.Equal("https://api.example.com", url);
    }

    [Fact]
    public void Parse_With_Hint_Splits_Cleanly()
    {
        var (hint, url) = BowireServerUrl.Parse("grpc@https://api.example.com:443");

        Assert.Equal("grpc", hint);
        Assert.Equal("https://api.example.com:443", url);
    }

    [Fact]
    public void Parse_Hint_Allows_Hyphens_And_Digits()
    {
        var (hint, url) = BowireServerUrl.Parse("socket-io2@https://api.example.com");

        Assert.Equal("socket-io2", hint);
        Assert.Equal("https://api.example.com", url);
    }

    // ---- URI userinfo must NOT be misread as a plugin hint -----------

    [Fact]
    public void Parse_Userinfo_With_Password_Stays_Intact()
    {
        var (hint, url) = BowireServerUrl.Parse("https://alice:secret@host.com");

        Assert.Null(hint);
        Assert.Equal("https://alice:secret@host.com", url);
    }

    [Fact]
    public void Parse_Userinfo_Without_Password_Stays_Intact()
    {
        var (hint, url) = BowireServerUrl.Parse("https://alice@host.com");

        Assert.Null(hint);
        Assert.Equal("https://alice@host.com", url);
    }

    [Fact]
    public void Parse_Hint_Plus_Userinfo_Keeps_Both()
    {
        var (hint, url) = BowireServerUrl.Parse("grpc@https://alice:pwd@host.com");

        Assert.Equal("grpc", hint);
        Assert.Equal("https://alice:pwd@host.com", url);
    }

    // ---- Bare email-style strings must NOT trigger a hint -------------

    [Fact]
    public void Parse_Email_Style_Without_Scheme_Has_No_Hint()
    {
        var (hint, url) = BowireServerUrl.Parse("alice@example.com");

        Assert.Null(hint);
        Assert.Equal("alice@example.com", url);
    }

    // ---- Plugin schemes (udp://, kafka://, dis://) need no hint -------

    [Fact]
    public void Parse_Plugin_Scheme_Url_Has_No_Hint()
    {
        var (hint, url) = BowireServerUrl.Parse("udp://239.0.13.37:8137");

        Assert.Null(hint);
        Assert.Equal("udp://239.0.13.37:8137", url);
    }

    [Fact]
    public void Parse_Redundant_Hint_With_Plugin_Scheme_Is_Honoured()
    {
        var (hint, url) = BowireServerUrl.Parse("udp@udp://239.0.13.37:8137");

        Assert.Equal("udp", hint);
        Assert.Equal("udp://239.0.13.37:8137", url);
    }

    // ---- Edge cases ---------------------------------------------------

    [Fact]
    public void Parse_Empty_Or_Null_Returns_Empty()
    {
        var (hint1, url1) = BowireServerUrl.Parse(null);
        Assert.Null(hint1);
        Assert.Equal(string.Empty, url1);

        var (hint2, url2) = BowireServerUrl.Parse(string.Empty);
        Assert.Null(hint2);
        Assert.Equal(string.Empty, url2);
    }

    [Fact]
    public void Parse_At_At_Start_Has_No_Hint()
    {
        var (hint, url) = BowireServerUrl.Parse("@https://api.example.com");

        Assert.Null(hint);
        Assert.Equal("@https://api.example.com", url);
    }

    [Fact]
    public void Parse_Hint_With_Special_Chars_Is_Rejected()
    {
        // Underscore is not in [a-zA-Z0-9-]; treat as not-a-hint and pass through.
        var (hint, url) = BowireServerUrl.Parse("my_plugin@https://api.example.com");

        Assert.Null(hint);
        Assert.Equal("my_plugin@https://api.example.com", url);
    }

    [Fact]
    public void StripHint_Returns_Bare_Url()
    {
        Assert.Equal("https://api.example.com:443", BowireServerUrl.StripHint("grpc@https://api.example.com:443"));
        Assert.Equal("https://api.example.com", BowireServerUrl.StripHint("https://api.example.com"));
        Assert.Equal("https://alice:pwd@host.com", BowireServerUrl.StripHint("https://alice:pwd@host.com"));
    }

    [Fact]
    public void Parse_GrpcWeb_Hint_Splits_Like_Any_Other_Plugin_Hint()
    {
        // The grammar is opaque to plugin variant names; grpcweb is no
        // different from grpc here. The mapping to plugin id + transport
        // metadata happens later in BowireEndpointHelpers.ResolveHint.
        var (hint, url) = BowireServerUrl.Parse("grpcweb@http://localhost:4268");

        Assert.Equal("grpcweb", hint);
        Assert.Equal("http://localhost:4268", url);
    }
}
