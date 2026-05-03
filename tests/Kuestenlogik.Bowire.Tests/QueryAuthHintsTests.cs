// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Endpoints;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the apikey-with-location=query path. The frontend marks
/// query-string credentials with a magic prefix in the metadata dict;
/// BowireEndpointHelpers.ApplyQueryAuthHints strips them, URL-encodes the
/// value, picks the right URL separator (question-mark vs ampersand),
/// and returns the remaining metadata as plain HTTP headers.
/// </summary>
public class QueryAuthHintsTests
{
    [Fact]
    public void NoMetadata_LeavesUrlUnchanged()
    {
        var (url, meta) = BowireEndpointHelpers.ApplyQueryAuthHints("https://api.example.com/v1/books", null);
        Assert.Equal("https://api.example.com/v1/books", url);
        Assert.Null(meta);
    }

    [Fact]
    public void MetadataWithoutPrefix_LeavesUrlUnchanged_AndPassesHeadersThrough()
    {
        var input = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer abc",
            ["X-Trace-Id"] = "42"
        };
        var (url, meta) = BowireEndpointHelpers.ApplyQueryAuthHints("https://api.example.com/", input);

        Assert.Equal("https://api.example.com/", url);
        Assert.NotNull(meta);
        Assert.Equal(2, meta!.Count);
        Assert.Equal("Bearer abc", meta["Authorization"]);
    }

    [Fact]
    public void QueryHint_AppendsParameterAndStripsFromMetadata()
    {
        var input = new Dictionary<string, string>
        {
            ["__bowireQuery__api_key"] = "secret123",
            ["X-Trace-Id"] = "42"
        };
        var (url, meta) = BowireEndpointHelpers.ApplyQueryAuthHints("https://api.example.com/v1/books", input);

        Assert.Equal("https://api.example.com/v1/books?api_key=secret123", url);
        Assert.NotNull(meta);
        Assert.Single(meta!);
        Assert.Equal("42", meta["X-Trace-Id"]);
        Assert.False(meta.ContainsKey("__bowireQuery__api_key"));
    }

    [Fact]
    public void QueryHint_UsesAmpersandWhenUrlAlreadyHasQueryString()
    {
        var input = new Dictionary<string, string>
        {
            ["__bowireQuery__api_key"] = "secret123"
        };
        var (url, _) = BowireEndpointHelpers.ApplyQueryAuthHints("https://api.example.com/v1/books?lang=en", input);

        Assert.Equal("https://api.example.com/v1/books?lang=en&api_key=secret123", url);
    }

    [Fact]
    public void QueryHint_UrlEncodesNameAndValue()
    {
        var input = new Dictionary<string, string>
        {
            ["__bowireQuery__has space"] = "with/slash and=equals"
        };
        var (url, _) = BowireEndpointHelpers.ApplyQueryAuthHints("https://api.example.com/", input);

        Assert.Equal("https://api.example.com/?has%20space=with%2Fslash%20and%3Dequals", url);
    }

    [Fact]
    public void MultipleQueryHints_ChainWithAmpersand()
    {
        var input = new Dictionary<string, string>
        {
            ["__bowireQuery__a"] = "1",
            ["__bowireQuery__b"] = "2"
        };
        var (url, meta) = BowireEndpointHelpers.ApplyQueryAuthHints("https://api.example.com/", input);

        // Order isn't guaranteed by the dict iteration but both must be present
        Assert.StartsWith("https://api.example.com/?", url, StringComparison.Ordinal);
        Assert.Contains("a=1", url, StringComparison.Ordinal);
        Assert.Contains("b=2", url, StringComparison.Ordinal);
        Assert.Contains("&", url, StringComparison.Ordinal);
        // When EVERY metadata entry was a query hint, the sanitized dict
        // is null — there's nothing left to forward as HTTP headers.
        Assert.Null(meta);
    }
}
