// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mcp;

namespace Kuestenlogik.Bowire.Mcp.Tests;

/// <summary>
/// The rule <see cref="BowireMcpEndpointRegistry.Register"/> enforces: one
/// endpoint per path, while nested prefixes coexist and the longest one
/// takes the request.
/// </summary>
public sealed class BowireMcpEndpointRegistryTests
{
    [Fact]
    public void The_Same_Path_Twice_Throws()
    {
        var registry = new BowireMcpEndpointRegistry();
        registry.Register("/bowire/mcp", BowireMcpEndpointMode.Server);

        var ex = Assert.Throws<InvalidOperationException>(
            () => registry.Register("/bowire/mcp/", BowireMcpEndpointMode.Adapter));
        Assert.Contains("/bowire/mcp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Default_Prefixes_Nest_And_The_Longest_Prefix_Takes_The_Request()
    {
        // /bowire/mcp/adapter lies below /bowire/mcp. That is allowed — it is
        // how the two defaults are laid out — and routing by the longest
        // registered prefix keeps each endpoint's traffic its own.
        var registry = new BowireMcpEndpointRegistry();
        registry.Register("/bowire/mcp", BowireMcpEndpointMode.Server);
        registry.Register("/bowire/mcp/adapter", BowireMcpEndpointMode.Adapter);

        Assert.Equal(BowireMcpEndpointMode.Server, registry.ResolveMode("/bowire/mcp"));
        Assert.Equal(BowireMcpEndpointMode.Adapter, registry.ResolveMode("/bowire/mcp/adapter"));
        Assert.Equal(BowireMcpEndpointMode.Adapter, registry.ResolveMode("/bowire/mcp/adapter/sse"));
        Assert.Equal(BowireMcpEndpointMode.Server, registry.ResolveMode("/bowire/mcp/adapterx"));
    }
}
