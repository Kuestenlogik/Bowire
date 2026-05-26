// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Protocol.Grpc;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for the Connect (Buf) transport hint plumbing. Mirrors
/// the gRPC-Web hint tests — same three bridges
/// (<see cref="BowireEndpointHelpers.ResolveHint"/>,
/// <see cref="GrpcChannelBuilder.ResolveMode"/>,
/// <see cref="GrpcChannelBuilder.ExtractTransportFromUrl"/>) plus the
/// shared <c>X-Bowire-Grpc-Transport</c> metadata key.
/// </summary>
public sealed class ConnectTransportHintTests
{
    [Fact]
    public void ResolveHint_connect_Maps_To_Grpc_Plugin_With_Connect_Metadata()
    {
        var (pluginId, meta) = BowireEndpointHelpers.ResolveHint("connect");

        Assert.Equal("grpc", pluginId);
        Assert.NotNull(meta);
        Assert.Equal("X-Bowire-Grpc-Transport", meta!.Value.Key);
        Assert.Equal("connect", meta.Value.Value);
    }

    [Fact]
    public void ResolveHint_Is_Case_Insensitive()
    {
        var upper = BowireEndpointHelpers.ResolveHint("CONNECT");
        var mixed = BowireEndpointHelpers.ResolveHint("Connect");

        Assert.Equal("grpc", upper.PluginId);
        Assert.Equal("grpc", mixed.PluginId);
        Assert.NotNull(upper.TransportMetadata);
        Assert.NotNull(mixed.TransportMetadata);
    }

    [Fact]
    public void ResolveMode_Connect_Metadata_Selects_Connect_Transport()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "connect"
        };

        Assert.Equal(GrpcTransportMode.Connect, GrpcChannelBuilder.ResolveMode(meta));
    }

    [Fact]
    public void ResolveMode_Metadata_Value_Is_Case_Insensitive()
    {
        var upper = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "Connect"
        };
        var mixed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "CONNECT"
        };

        Assert.Equal(GrpcTransportMode.Connect, GrpcChannelBuilder.ResolveMode(upper));
        Assert.Equal(GrpcTransportMode.Connect, GrpcChannelBuilder.ResolveMode(mixed));
    }

    [Fact]
    public void ResolveMode_UnknownValue_Falls_Back_To_Native()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "something-else"
        };

        Assert.Equal(GrpcTransportMode.Native, GrpcChannelBuilder.ResolveMode(meta));
    }

    [Fact]
    public void ExtractTransportFromUrl_Connect_Marker_Selects_Connect_Transport()
    {
        var (clean, mode) = GrpcChannelBuilder.ExtractTransportFromUrl(
            "https://api.example.com?__bowireGrpcTransport=connect");

        Assert.Equal("https://api.example.com", clean);
        Assert.Equal(GrpcTransportMode.Connect, mode);
    }

    [Fact]
    public void ExtractTransportFromUrl_Connect_Marker_Coexists_With_Other_Query_Params()
    {
        var (clean, mode) = GrpcChannelBuilder.ExtractTransportFromUrl(
            "https://api.example.com?foo=bar&__bowireGrpcTransport=connect&x=y");

        Assert.Equal("https://api.example.com?foo=bar&x=y", clean);
        Assert.Equal(GrpcTransportMode.Connect, mode);
    }

    [Fact]
    public void ExtractTransportFromUrl_Unrecognised_Marker_Value_Falls_Back_To_Native()
    {
        var (clean, mode) = GrpcChannelBuilder.ExtractTransportFromUrl(
            "https://api.example.com?__bowireGrpcTransport=glorp");

        Assert.Equal("https://api.example.com", clean);
        Assert.Equal(GrpcTransportMode.Native, mode);
    }
}
