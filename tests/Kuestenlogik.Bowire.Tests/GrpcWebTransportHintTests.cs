// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Protocol.Grpc;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for the gRPC-Web transport hint plumbing: the
/// <c>grpcweb@</c> URL hint, the <c>X-Bowire-Grpc-Transport</c> metadata
/// key, and the bidirectional bridges between the two
/// (<see cref="GrpcChannelBuilder.ResolveMode"/>,
/// <see cref="GrpcChannelBuilder.ExtractTransportFromUrl"/>,
/// <see cref="BowireEndpointHelpers.ResolveHint"/>).
/// </summary>
public sealed class GrpcWebTransportHintTests
{
    [Fact]
    public void ResolveHint_grpcweb_Maps_To_Grpc_Plugin_With_Web_Metadata()
    {
        var (pluginId, meta) = BowireEndpointHelpers.ResolveHint("grpcweb");

        Assert.Equal("grpc", pluginId);
        Assert.NotNull(meta);
        Assert.Equal("X-Bowire-Grpc-Transport", meta!.Value.Key);
        Assert.Equal("web", meta.Value.Value);
    }

    [Fact]
    public void ResolveHint_Plain_Grpc_Does_Not_Add_Metadata()
    {
        var (pluginId, meta) = BowireEndpointHelpers.ResolveHint("grpc");

        Assert.Equal("grpc", pluginId);
        Assert.Null(meta);
    }

    [Fact]
    public void ResolveHint_Is_Case_Insensitive()
    {
        // The hint grammar (BowireServerUrl) accepts mixed case; the
        // resolver normalises before mapping. GRPCWEB and GrpcWeb both
        // route the same way.
        var upper = BowireEndpointHelpers.ResolveHint("GRPCWEB");
        var mixed = BowireEndpointHelpers.ResolveHint("GrpcWeb");

        Assert.Equal("grpc", upper.PluginId);
        Assert.Equal("grpc", mixed.PluginId);
        Assert.NotNull(upper.TransportMetadata);
        Assert.NotNull(mixed.TransportMetadata);
    }

    [Fact]
    public void ResolveHint_Unknown_Hint_Passes_Through()
    {
        var (pluginId, meta) = BowireEndpointHelpers.ResolveHint("signalr");

        Assert.Equal("signalr", pluginId);
        Assert.Null(meta);
    }

    [Fact]
    public void ResolveMode_Empty_Metadata_Returns_Native()
    {
        Assert.Equal(GrpcTransportMode.Native, GrpcChannelBuilder.ResolveMode(null));
        Assert.Equal(GrpcTransportMode.Native,
            GrpcChannelBuilder.ResolveMode(new Dictionary<string, string>()));
    }

    [Fact]
    public void ResolveMode_Explicit_Web_Metadata_Selects_Web_Transport()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "web"
        };

        Assert.Equal(GrpcTransportMode.Web, GrpcChannelBuilder.ResolveMode(meta));
    }

    [Fact]
    public void ResolveMode_Explicit_Native_Metadata_Selects_Native()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "native"
        };

        Assert.Equal(GrpcTransportMode.Native, GrpcChannelBuilder.ResolveMode(meta));
    }

    [Fact]
    public void ResolveMode_Header_Lookup_Is_Case_Insensitive()
    {
        // Hand-rolled HTTP clients spell the header in lowercase; the
        // resolver matches case-insensitively.
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-bowire-grpc-transport"] = "web"
        };

        Assert.Equal(GrpcTransportMode.Web, GrpcChannelBuilder.ResolveMode(meta));
    }

    [Fact]
    public void ResolveMode_Header_Value_Is_Case_Insensitive()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "WEB"
        };

        Assert.Equal(GrpcTransportMode.Web, GrpcChannelBuilder.ResolveMode(meta));
    }

    [Fact]
    public void ResolveMode_Unrecognised_Value_Falls_Back_To_Native()
    {
        // Garbage in: no second guess, just default to the safe transport.
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "h2c"
        };

        Assert.Equal(GrpcTransportMode.Native, GrpcChannelBuilder.ResolveMode(meta));
    }

    [Fact]
    public void StripTransportMarker_Removes_Marker_Keeps_Others()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "web",
            ["x-trace-id"] = "abc"
        };

        var stripped = GrpcChannelBuilder.StripTransportMarker(meta);

        Assert.NotNull(stripped);
        var only = Assert.Single(stripped!);
        Assert.Equal("x-trace-id", only.Key);
        Assert.Equal("abc", only.Value);
    }

    [Fact]
    public void StripTransportMarker_Null_Returns_Null()
    {
        Assert.Null(GrpcChannelBuilder.StripTransportMarker(null));
    }

    [Fact]
    public void StripTransportMarker_Empty_Returns_Null()
    {
        Assert.Null(GrpcChannelBuilder.StripTransportMarker(new Dictionary<string, string>()));
    }

    [Fact]
    public void ExtractTransportFromUrl_No_Marker_Returns_Native_And_Original_Url()
    {
        var (cleanUrl, mode) = GrpcChannelBuilder.ExtractTransportFromUrl("http://api.example.com:4268");

        Assert.Equal("http://api.example.com:4268", cleanUrl);
        Assert.Equal(GrpcTransportMode.Native, mode);
    }

    [Fact]
    public void ExtractTransportFromUrl_Web_Marker_Selects_Web_And_Strips_Marker()
    {
        // Discovery endpoint stitches __bowireGrpcTransport=web onto URLs
        // that came in via grpcweb@ — the plugin pulls it back off.
        var (cleanUrl, mode) = GrpcChannelBuilder.ExtractTransportFromUrl(
            "http://api.example.com:4268?__bowireGrpcTransport=web");

        Assert.Equal("http://api.example.com:4268", cleanUrl);
        Assert.Equal(GrpcTransportMode.Web, mode);
    }

    [Fact]
    public void ExtractTransportFromUrl_Marker_Mixed_With_Other_Query_Params()
    {
        var (cleanUrl, mode) = GrpcChannelBuilder.ExtractTransportFromUrl(
            "http://api.example.com:4268?apikey=secret&__bowireGrpcTransport=web&trace=1");

        // Marker removed, the other params survive in order.
        Assert.Equal("http://api.example.com:4268?apikey=secret&trace=1", cleanUrl);
        Assert.Equal(GrpcTransportMode.Web, mode);
    }

    [Fact]
    public void ExtractTransportFromUrl_Empty_String_Is_Native()
    {
        var (cleanUrl, mode) = GrpcChannelBuilder.ExtractTransportFromUrl("");

        Assert.Equal("", cleanUrl);
        Assert.Equal(GrpcTransportMode.Native, mode);
    }

    [Fact]
    public void TransportMetadataKey_Is_Public_Stable_Spelling()
    {
        // Library consumers reference this constant directly when wiring
        // a programmatic dispatch — guard against a typo refactor.
        Assert.Equal("X-Bowire-Grpc-Transport", BowireGrpcProtocol.TransportMetadataKey);
    }
}
