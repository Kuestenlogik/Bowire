// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Kuestenlogik.Bowire.Mock.Replay;

namespace Kuestenlogik.Bowire.Mock.Tests;

public sealed class ResponseBodySubstitutorTests
{
    [Fact]
    public void Substitute_EmptyOrNull_PassesThrough()
    {
        Assert.Equal("", ResponseBodySubstitutor.Substitute(""));
        Assert.Null(ResponseBodySubstitutor.Substitute(null!));
    }

    [Fact]
    public void Substitute_NoPlaceholders_ReturnsInputUnchanged()
    {
        const string body = """{"name":"alice","age":30}""";
        Assert.Equal(body, ResponseBodySubstitutor.Substitute(body));
    }

    [Fact]
    public void Substitute_UuidToken_ReplacedWithValidGuid()
    {
        var result = ResponseBodySubstitutor.Substitute("""{"id":"${uuid}"}""");

        // Extract what's between the quotes after "id":"
        var start = result.IndexOf("\"id\":\"", StringComparison.Ordinal) + "\"id\":\"".Length;
        var end = result.IndexOf('"', start);
        var id = result[start..end];

        Assert.True(Guid.TryParse(id, out _), $"Expected a valid UUID, got '{id}'.");
    }

    [Fact]
    public void Substitute_UuidToken_DifferentPerOccurrence()
    {
        var result = ResponseBodySubstitutor.Substitute("${uuid}|${uuid}");
        var parts = result.Split('|');
        Assert.Equal(2, parts.Length);
        Assert.NotEqual(parts[0], parts[1]);
        Assert.True(Guid.TryParse(parts[0], out _));
        Assert.True(Guid.TryParse(parts[1], out _));
    }

    [Fact]
    public void Substitute_NowToken_ReplacedWithCurrentUnixSeconds()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = ResponseBodySubstitutor.Substitute("${now}");
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.True(long.TryParse(result, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed));
        Assert.InRange(parsed, before, after);
    }

    [Fact]
    public void Substitute_NowMsToken_ReplacedWithCurrentUnixMilliseconds()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = ResponseBodySubstitutor.Substitute("${nowMs}");
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.True(long.TryParse(result, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed));
        Assert.InRange(parsed, before, after);
    }

    [Fact]
    public void Substitute_TimestampToken_ReplacedWithIso8601Utc()
    {
        var result = ResponseBodySubstitutor.Substitute("${timestamp}");
        Assert.True(DateTimeOffset.TryParseExact(
            result,
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out _),
            $"Expected ISO 8601 UTC timestamp, got '{result}'.");
    }

    [Fact]
    public void Substitute_NowPlusToken_AdvancesTimeBySeconds()
    {
        var nowResult = ResponseBodySubstitutor.Substitute("${now}");
        var plusResult = ResponseBodySubstitutor.Substitute("${now+3600}");

        var now = long.Parse(nowResult, CultureInfo.InvariantCulture);
        var plus = long.Parse(plusResult, CultureInfo.InvariantCulture);

        // 3600 seconds ahead, with a few-second tolerance for test-run jitter.
        Assert.InRange(plus - now, 3595, 3605);
    }

    [Fact]
    public void Substitute_NowMinusToken_RetreatsTimeBySeconds()
    {
        var nowResult = ResponseBodySubstitutor.Substitute("${now}");
        var minusResult = ResponseBodySubstitutor.Substitute("${now-600}");

        var now = long.Parse(nowResult, CultureInfo.InvariantCulture);
        var minus = long.Parse(minusResult, CultureInfo.InvariantCulture);

        Assert.InRange(now - minus, 595, 605);
    }

    [Fact]
    public void Substitute_RandomToken_ReplacedWithDecimalNumber()
    {
        var result = ResponseBodySubstitutor.Substitute("${random}");
        Assert.True(uint.TryParse(result, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
    }

    [Fact]
    public void Substitute_UnknownToken_LeftVerbatim()
    {
        Assert.Equal("${completely_unknown_token}",
            ResponseBodySubstitutor.Substitute("${completely_unknown_token}"));
        Assert.Equal("prefix ${foo} suffix",
            ResponseBodySubstitutor.Substitute("prefix ${foo} suffix"));
    }

    [Fact]
    public void Substitute_MixedBody_ResolvesAllKnownTokens()
    {
        var result = ResponseBodySubstitutor.Substitute(
            """{"id":"${uuid}","created":${now},"ttl":${now+60}}""");

        // Sanity: the shape is preserved and no placeholder leaks through.
        Assert.DoesNotContain("${", result, StringComparison.Ordinal);
        Assert.StartsWith("{\"id\":\"", result, StringComparison.Ordinal);
        Assert.EndsWith("}", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Substitute_MalformedOffset_KeepsLiteralIntact()
    {
        // Non-numeric offset: neither 'now+' nor 'now-' should silently
        // resolve. Keep the token so the user notices the typo.
        Assert.Equal("${now+notanumber}",
            ResponseBodySubstitutor.Substitute("${now+notanumber}"));
    }
}
