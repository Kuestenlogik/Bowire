// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #39 — HAR import/export round-trip. <see cref="BowireHarConverter.ToHar"/> is
/// the inverse of <see cref="BowireHarConverter.Convert(string, string?)"/> for
/// unary steps: exporting an imported recording is a byte-identical fixed point
/// (golden without a checked-in file), and the step fields survive the trip.
/// </summary>
public sealed class HarRoundTripTests
{
    // A HAR entry built via object graph → JSON so there's no brace escaping.
    private static string SampleHar()
    {
        var doc = new
        {
            log = new
            {
                version = "1.2",
                creator = new { name = "DevTools", version = "1" },
                entries = new[]
                {
                    new
                    {
                        startedDateTime = "2026-04-01T10:00:00.000Z",
                        time = 42,
                        request = new
                        {
                            method = "GET",
                            url = "https://api.example.com/users/42",
                            headers = new[]
                            {
                                new { name = "Accept", value = "application/json" },
                                new { name = "Authorization", value = "Bearer tok" },
                            },
                            postData = new { text = "{\"q\":1}" },
                        },
                        response = new
                        {
                            status = 200,
                            statusText = "OK",
                            headers = new[] { new { name = "Content-Type", value = "application/json" } },
                            content = new { size = 9, mimeType = "application/json", text = "{\"id\":42}" },
                        },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(doc);
    }

    [Fact]
    public void Export_IsAByteIdenticalFixedPoint()
    {
        var rec1 = BowireHarConverter.Convert(SampleHar());
        var har2 = BowireHarConverter.ToHar(rec1);
        var rec2 = BowireHarConverter.Convert(har2);
        var har3 = BowireHarConverter.ToHar(rec2);

        // Exporting an already-exported-then-reimported recording is byte-identical.
        Assert.Equal(har2, har3);
    }

    [Fact]
    public void RoundTrip_PreservesUnaryStepFields()
    {
        var rec1 = BowireHarConverter.Convert(SampleHar());
        var rec2 = BowireHarConverter.Convert(BowireHarConverter.ToHar(rec1));

        var s1 = Assert.Single(rec1.Steps);
        var s2 = Assert.Single(rec2.Steps);
        Assert.Equal(s1.HttpVerb, s2.HttpVerb);
        Assert.Equal(s1.HttpPath, s2.HttpPath);
        Assert.Equal(s1.ServerUrl, s2.ServerUrl);
        Assert.Equal(s1.Status, s2.Status);
        Assert.Equal(s1.Body, s2.Body);
        Assert.Equal(s1.Response, s2.Response);
        Assert.Equal(s1.CapturedAt, s2.CapturedAt);
        Assert.Equal(s1.DurationMs, s2.DurationMs);
        Assert.Equal(s1.Metadata?["Authorization"], s2.Metadata?["Authorization"]);
        Assert.Equal(s1.ResponseHeaders?["Content-Type"], s2.ResponseHeaders?["Content-Type"]);
    }

    [Fact]
    public void Export_ReconstructsUrlStatusAndSortedHeaders()
    {
        var rec = BowireHarConverter.Convert(SampleHar());
        using var doc = JsonDocument.Parse(BowireHarConverter.ToHar(rec));
        var entry = doc.RootElement.GetProperty("log").GetProperty("entries")[0];

        Assert.Equal("GET", entry.GetProperty("request").GetProperty("method").GetString());
        Assert.Equal("https://api.example.com/users/42", entry.GetProperty("request").GetProperty("url").GetString());
        Assert.Equal(200, entry.GetProperty("response").GetProperty("status").GetInt32());
        Assert.Equal("2026-04-01T10:00:00.000Z", entry.GetProperty("startedDateTime").GetString());
        // Headers are sorted by name for deterministic export.
        var names = entry.GetProperty("request").GetProperty("headers").EnumerateArray()
            .Select(h => h.GetProperty("name").GetString() ?? "").ToArray();
        Assert.Equal(["Accept", "Authorization"], names);
    }
}
