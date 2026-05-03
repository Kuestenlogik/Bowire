// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.Sse;

namespace Kuestenlogik.Bowire.Tests;

public class SseEventPayloadTests
{
    [Fact]
    public void Record_Stores_All_Fields()
    {
        var payload = new SseEventPayload("evt-1", "tick", "{\"price\":42}", 1000);

        Assert.Equal("evt-1", payload.Id);
        Assert.Equal("tick", payload.Event);
        Assert.Equal("{\"price\":42}", payload.Data);
        Assert.Equal(1000, payload.Retry);
    }

    [Fact]
    public void Record_Allows_Null_Optional_Fields()
    {
        var payload = new SseEventPayload(null, null, "data", null);

        Assert.Null(payload.Id);
        Assert.Null(payload.Event);
        Assert.Equal("data", payload.Data);
        Assert.Null(payload.Retry);
    }

    [Fact]
    public void Record_Equality_Holds_For_Same_Values()
    {
        var a = new SseEventPayload("1", "e", "d", 100);
        var b = new SseEventPayload("1", "e", "d", 100);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Record_Serializes_To_Json_With_Property_Names()
    {
        var payload = new SseEventPayload("42", "tick", "hello", 500);

        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"Id\":\"42\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Event\":\"tick\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Data\":\"hello\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Retry\":500", json, StringComparison.Ordinal);
    }
}
