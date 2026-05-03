// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Sse;

namespace Kuestenlogik.Bowire.Tests;

public class SseEndpointInfoTests
{
    [Fact]
    public void Constructor_Required_Fields_Populates_Properties()
    {
        var info = new SseEndpointInfo("/events/ticker", "Ticker");

        Assert.Equal("/events/ticker", info.Path);
        Assert.Equal("Ticker", info.Name);
        Assert.Null(info.Description);
        Assert.Null(info.EventTypes);
    }

    [Fact]
    public void Constructor_All_Fields_Populates_Properties()
    {
        var info = new SseEndpointInfo("/events/ticker", "Ticker", "Live ticker stream", "price,volume");

        Assert.Equal("/events/ticker", info.Path);
        Assert.Equal("Ticker", info.Name);
        Assert.Equal("Live ticker stream", info.Description);
        Assert.Equal("price,volume", info.EventTypes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_Empty_Or_Whitespace_Path_Throws(string? path)
    {
        // Null throws ArgumentNullException (subclass), empty/whitespace throws
        // ArgumentException — both are caught by ThrowsAny<ArgumentException>.
        Assert.ThrowsAny<ArgumentException>(() => new SseEndpointInfo(path!, "Ticker"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_Empty_Or_Whitespace_Name_Throws(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SseEndpointInfo("/events/x", name!));
    }
}
