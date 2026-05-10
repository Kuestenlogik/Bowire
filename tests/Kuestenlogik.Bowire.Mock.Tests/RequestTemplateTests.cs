// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Replay;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Direct tests for <see cref="RequestTemplate"/>'s token resolution.
/// The substitutor wires this in for <c>${request.*}</c> placeholders;
/// each token shape needs explicit coverage so the substitutor's
/// "leave literal on null" fallback doesn't silently mask real bugs.
/// </summary>
public sealed class RequestTemplateTests
{
    private static (DefaultHttpContext ctx, RequestTemplate tpl) Build(
        string method = "GET",
        string path = "/x",
        string? query = null,
        string? body = null,
        IReadOnlyDictionary<string, string>? bindings = null,
        IDictionary<string, string>? headers = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (query is not null) ctx.Request.QueryString = new QueryString(query);
        if (headers is not null)
        {
            foreach (var (k, v) in headers) ctx.Request.Headers[k] = v;
        }
        return (ctx, new RequestTemplate(ctx, body, bindings));
    }

    [Fact]
    public void Resolve_Method_ReturnsRequestVerb()
    {
        var (_, tpl) = Build(method: "POST");
        Assert.Equal("POST", tpl.Resolve("method"));
    }

    [Fact]
    public void Resolve_Path_ReturnsFullRequestPath()
    {
        var (_, tpl) = Build(path: "/users/123");
        Assert.Equal("/users/123", tpl.Resolve("path"));
    }

    [Fact]
    public void Resolve_Body_ReturnsBufferedBody()
    {
        var (_, tpl) = Build(body: """{"a":1}""");
        Assert.Equal("""{"a":1}""", tpl.Resolve("body"));
    }

    [Theory]
    [InlineData("path.0", "/a/b/c", "a")]
    [InlineData("path.1", "/a/b/c", "b")]
    [InlineData("path.2", "/a/b/c", "c")]
    public void Resolve_PathSegmentByIndex_PicksZeroBasedSegment(string token, string path, string expected)
    {
        var (_, tpl) = Build(path: path);
        Assert.Equal(expected, tpl.Resolve(token));
    }

    [Fact]
    public void Resolve_PathSegmentOutOfRange_ReturnsNull()
    {
        var (_, tpl) = Build(path: "/a");
        Assert.Null(tpl.Resolve("path.99"));
    }

    [Fact]
    public void Resolve_PathBindingByName_HitsTemplateCapture()
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = "alice" };
        var (_, tpl) = Build(path: "/users/alice", bindings: bindings);
        Assert.Equal("alice", tpl.Resolve("path.id"));
    }

    [Fact]
    public void Resolve_PathBindingMissing_ReturnsNull()
    {
        var (_, tpl) = Build(path: "/x");
        Assert.Null(tpl.Resolve("path.missing"));
    }

    [Fact]
    public void Resolve_QueryDirectKey_ReturnsValue()
    {
        var (_, tpl) = Build(query: "?city=Berlin");
        Assert.Equal("Berlin", tpl.Resolve("query.city"));
    }

    [Fact]
    public void Resolve_QueryCaseInsensitiveFallback_StillReturnsValue()
    {
        // Direct lookup is case-sensitive in IQueryCollection, so the
        // helper falls through to the case-insensitive scan.
        var (_, tpl) = Build(query: "?City=Berlin");
        Assert.Equal("Berlin", tpl.Resolve("query.city"));
    }

    [Fact]
    public void Resolve_QueryMissing_ReturnsNull()
    {
        var (_, tpl) = Build(query: "?other=1");
        Assert.Null(tpl.Resolve("query.missing"));
    }

    [Fact]
    public void Resolve_HeaderPresent_ReturnsValue()
    {
        var (_, tpl) = Build(headers: new Dictionary<string, string> { ["X-Trace"] = "abc" });
        Assert.Equal("abc", tpl.Resolve("header.x-trace")); // case-insensitive lookup
    }

    [Fact]
    public void Resolve_HeaderMissing_ReturnsNull()
    {
        var (_, tpl) = Build();
        Assert.Null(tpl.Resolve("header.X-Missing"));
    }

    [Fact]
    public void Resolve_UnknownToken_ReturnsNull()
    {
        var (_, tpl) = Build();
        Assert.Null(tpl.Resolve("totally-unknown"));
    }

    [Fact]
    public void Resolve_BodyPath_NoBody_ReturnsNull()
    {
        var (_, tpl) = Build(body: null);
        Assert.Null(tpl.Resolve("body.foo"));
    }

    [Fact]
    public void Resolve_BodyPath_NonJsonBody_ReturnsNull()
    {
        var (_, tpl) = Build(body: "not json {");
        Assert.Null(tpl.Resolve("body.foo"));
    }

    [Fact]
    public void Resolve_BodyPath_NestedObject_NavigatesByDot()
    {
        var (_, tpl) = Build(body: """{"a":{"b":{"c":"deep"}}}""");
        Assert.Equal("deep", tpl.Resolve("body.a.b.c"));
    }

    [Fact]
    public void Resolve_BodyPath_ArrayIndex_PicksElement()
    {
        var (_, tpl) = Build(body: """{"items":[{"id":"first"},{"id":"second"}]}""");
        Assert.Equal("second", tpl.Resolve("body.items.1.id"));
    }

    [Fact]
    public void Resolve_BodyPath_ArrayOutOfBounds_ReturnsNull()
    {
        var (_, tpl) = Build(body: """{"items":[{"id":"a"}]}""");
        Assert.Null(tpl.Resolve("body.items.5.id"));
    }

    [Fact]
    public void Resolve_BodyPath_StringSegmentOnArray_ReturnsNull()
    {
        var (_, tpl) = Build(body: """{"items":["a","b"]}""");
        // arrays only accept numeric indexes — "a" forces a fail.
        Assert.Null(tpl.Resolve("body.items.foo"));
    }

    [Fact]
    public void Resolve_BodyPath_NavigateThroughScalar_ReturnsNull()
    {
        var (_, tpl) = Build(body: """{"a":42}""");
        Assert.Null(tpl.Resolve("body.a.b"));
    }

    [Fact]
    public void Resolve_BodyPath_LeafBool_ReturnsLowercaseString()
    {
        var (_, tpl) = Build(body: """{"flag":true,"none":null}""");
        Assert.Equal("true", tpl.Resolve("body.flag"));
        Assert.Equal("", tpl.Resolve("body.none"));
    }

    [Fact]
    public void Resolve_BodyPath_LeafFalse_ReturnsLowercaseString()
    {
        var (_, tpl) = Build(body: """{"flag":false}""");
        Assert.Equal("false", tpl.Resolve("body.flag"));
    }

    [Fact]
    public void Resolve_BodyPath_LeafNumber_ReturnsRawText()
    {
        var (_, tpl) = Build(body: """{"n":42.5}""");
        Assert.Equal("42.5", tpl.Resolve("body.n"));
    }

    [Fact]
    public void Resolve_BodyPath_LeafObject_ReturnsCompactJson()
    {
        var (_, tpl) = Build(body: """{"obj":{"k":1}}""");
        var result = tpl.Resolve("body.obj");
        Assert.Contains("\"k\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_BodyPath_MissingSegment_ReturnsNull()
    {
        var (_, tpl) = Build(body: """{"a":1}""");
        Assert.Null(tpl.Resolve("body.missing.deep"));
    }

    [Fact]
    public void Resolve_PathSegment_NegativeIndex_ReturnsNull()
    {
        // path.{N} only accepts non-negative integers. Negative values
        // fall through to the named-binding path which then misses too.
        var (_, tpl) = Build(path: "/a/b");
        Assert.Null(tpl.Resolve("path.-1"));
    }
}
