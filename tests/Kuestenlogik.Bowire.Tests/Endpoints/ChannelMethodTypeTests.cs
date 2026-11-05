// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Endpoints;
using Xunit;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// #1002 — the method type of a channel-open request reaches the plugin as
/// the <see cref="BowireMetadataKeys.MethodType"/> marker, which, like every
/// marker, never goes on the wire as a header.
/// </summary>
public sealed class ChannelMethodTypeTests
{
    [Fact]
    public void The_Method_Type_Is_Added_As_The_Marker_And_The_Caller_Metadata_Is_Left_Alone()
    {
        var metadata = new Dictionary<string, string> { ["Authorization"] = "Bearer x" };

        var result = BowireChannelEndpoints.WithMethodType(metadata, "ClientStreaming")!;

        Assert.Equal("ClientStreaming", result[BowireMetadataKeys.MethodType]);
        Assert.Equal("Bearer x", result["Authorization"]);
        Assert.False(metadata.ContainsKey(BowireMetadataKeys.MethodType));
    }

    [Fact]
    public void No_Method_Type_Leaves_The_Metadata_As_It_Was()
    {
        Assert.Null(BowireChannelEndpoints.WithMethodType(null, null));
        var metadata = new Dictionary<string, string> { ["a"] = "1" };
        Assert.Same(metadata, BowireChannelEndpoints.WithMethodType(metadata, " "));
    }

    [Fact]
    public void The_Marker_Is_Not_A_Wire_Header()
    {
        var headers = BowireMetadataKeys.WireHeaders(
            BowireChannelEndpoints.WithMethodType(new Dictionary<string, string> { ["a"] = "1" }, "Duplex"));

        Assert.Equal(["a"], headers.Select(h => h.Key));
    }
}
