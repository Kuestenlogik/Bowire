// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Replay;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Which recorded response headers a REST mock replays, and which it drops.
/// </summary>
/// <remarks>
/// A recording captures the headers the real server sent, including the ones
/// that describe <em>that</em> connection rather than the payload. Replaying
/// a captured <c>Content-Length</c> against a body the mock re-serialised, or
/// a captured <c>Transfer-Encoding: chunked</c> onto a response Kestrel is
/// framing itself, breaks the response in ways that look like a client bug.
/// </remarks>
public sealed class RestResponseHeaderTests
{
    private static DefaultHttpContext Apply(IDictionary<string, string>? headers)
    {
        var ctx = new DefaultHttpContext();
        UnaryReplayer.ApplyRestResponseHeaders(ctx, headers);
        return ctx;
    }

    [Fact]
    public void A_Recorded_Header_Is_Replayed()
    {
        var ctx = Apply(new Dictionary<string, string> { ["X-Request-Id"] = "abc-123" });

        Assert.Equal("abc-123", ctx.Response.Headers["X-Request-Id"].ToString());
    }

    [Theory]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Connection")]
    [InlineData("Keep-Alive")]
    [InlineData("Upgrade")]
    [InlineData("Proxy-Connection")]
    [InlineData("TE")]
    [InlineData("Trailer")]
    public void A_Header_Describing_The_Recorded_Connection_Is_Dropped(string name)
    {
        // These belong to the capture's transport, not to the payload. The
        // mock's own server frames the response it is actually sending.
        var ctx = Apply(new Dictionary<string, string> { [name] = "whatever" });

        Assert.False(ctx.Response.Headers.ContainsKey(name), $"{name} should not be replayed");
    }

    [Fact]
    public void The_Deny_List_Ignores_Case_As_Http_Does()
    {
        // A recording may carry "content-length" exactly as the server sent
        // it; header names are case-insensitive and the guard has to be too.
        var ctx = Apply(new Dictionary<string, string> { ["content-length"] = "42" });

        Assert.False(ctx.Response.Headers.ContainsKey("Content-Length"));
    }

    [Fact]
    public void Content_Type_Goes_Through_The_Typed_Property()
    {
        // Setting it through the header dictionary and through the typed
        // property are not the same thing in ASP.NET; the typed one is what
        // the response actually negotiates on.
        var ctx = Apply(new Dictionary<string, string> { ["Content-Type"] = "application/xml" });

        Assert.Equal("application/xml", ctx.Response.ContentType);
    }

    [Fact]
    public void A_Recording_Without_A_Content_Type_Still_Answers_As_Json()
    {
        // The common case: the mock re-serialises a JSON body, so a missing
        // captured content type must not leave the response untyped.
        var ctx = Apply(new Dictionary<string, string> { ["X-Anything"] = "1" });

        Assert.Equal("application/json; charset=utf-8", ctx.Response.ContentType);
    }

    [Theory]
    [InlineData(null)]
    public void No_Recorded_Headers_At_All_Still_Sets_A_Content_Type(IDictionary<string, string>? headers)
        => Assert.Equal("application/json; charset=utf-8", Apply(headers).Response.ContentType);

    [Fact]
    public void An_Empty_Header_Set_Behaves_Like_None()
        => Assert.Equal("application/json; charset=utf-8",
            Apply(new Dictionary<string, string>()).Response.ContentType);

    [Fact]
    public void A_Blank_Content_Type_Falls_Back_Rather_Than_Clearing_It()
    {
        // An empty captured value would otherwise produce a response with no
        // content type at all, which clients handle far worse than a wrong one.
        var ctx = Apply(new Dictionary<string, string> { ["Content-Type"] = "" });

        Assert.Equal("application/json; charset=utf-8", ctx.Response.ContentType);
    }

    [Fact]
    public void A_Nameless_Header_Is_Skipped_Rather_Than_Throwing()
    {
        // Recordings are files; a hand-edited one can carry an empty key, and
        // ASP.NET throws on an empty header name.
        var ctx = Apply(new Dictionary<string, string> { [""] = "value", ["X-Ok"] = "yes" });

        Assert.Equal("yes", ctx.Response.Headers["X-Ok"].ToString());
    }

    [Fact]
    public void Several_Headers_All_Survive_Alongside_A_Dropped_One()
    {
        var ctx = Apply(new Dictionary<string, string>
        {
            ["X-One"] = "1",
            ["Content-Length"] = "999",
            ["X-Two"] = "2",
        });

        Assert.Equal("1", ctx.Response.Headers["X-One"].ToString());
        Assert.Equal("2", ctx.Response.Headers["X-Two"].ToString());
        Assert.False(ctx.Response.Headers.ContainsKey("Content-Length"));
    }
}
