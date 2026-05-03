// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.PluginLoading;

namespace Kuestenlogik.Bowire.Mock.Tests;

public class PluginPackageMapTests
{
    [Theory]
    [InlineData("grpc", "Kuestenlogik.Bowire.Protocol.Grpc")]
    [InlineData("rest", "Kuestenlogik.Bowire.Protocol.Rest")]
    [InlineData("signalr", "Kuestenlogik.Bowire.Protocol.SignalR")]
    [InlineData("graphql", "Kuestenlogik.Bowire.Protocol.GraphQL")]
    [InlineData("storm", "Kuestenlogik.Bowire.Protocol.Storm")]
    [InlineData("kafka", "Kuestenlogik.Bowire.Protocol.Kafka")]
    [InlineData("dis", "Kuestenlogik.Bowire.Protocol.Dis")]
    [InlineData("udp", "Kuestenlogik.Bowire.Protocol.Udp")]
    public void TryGetPackageId_KnownProtocol_ReturnsCanonicalPackage(string protocolId, string expected)
    {
        Assert.Equal(expected, PluginPackageMap.TryGetPackageId(protocolId));
    }

    [Theory]
    [InlineData("GRPC")]
    [InlineData("Grpc")]
    [InlineData("gRPC")]
    public void TryGetPackageId_CaseInsensitive(string protocolId)
    {
        Assert.Equal("Kuestenlogik.Bowire.Protocol.Grpc", PluginPackageMap.TryGetPackageId(protocolId));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(" ")]
    public void TryGetPackageId_UnknownOrEmpty_ReturnsNull(string protocolId)
    {
        Assert.Null(PluginPackageMap.TryGetPackageId(protocolId));
    }
}
