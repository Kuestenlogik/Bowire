// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Mcp;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tests;

/// <summary>
/// Tests for the MCP protocol plugin. Identity, defensive guards on
/// <c>DiscoverAsync</c>, the JSON-RPC JsonElement extractors, and the
/// adapter server's stateless message handlers (initialize / ping /
/// unknown method) are all reachable without a live MCP server. Real
/// JSON-RPC against an HTTP endpoint is integration-tier territory.
/// </summary>
public sealed class BowireMcpProtocolTests
{
    [Fact]
    public void Identity_Properties_Are_Stable()
    {
        var protocol = new BowireMcpProtocol();

        Assert.Equal("MCP", protocol.Name);
        Assert.Equal("mcp", protocol.Id);
        Assert.NotNull(protocol.IconSvg);
        Assert.Contains("<svg", protocol.IconSvg, StringComparison.Ordinal);
    }

    [Fact]
    public void Implements_IBowireProtocol()
    {
        var protocol = new BowireMcpProtocol();

        Assert.IsAssignableFrom<IBowireProtocol>(protocol);
    }

    [Fact]
    public async Task DiscoverAsync_Empty_Url_Returns_Empty()
    {
        var protocol = new BowireMcpProtocol();

        var services = await protocol.DiscoverAsync(
            "", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Whitespace_Url_Returns_Empty()
    {
        var protocol = new BowireMcpProtocol();

        var services = await protocol.DiscoverAsync(
            "   ", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Unreachable_Endpoint_Returns_Empty_Without_Throwing()
    {
        var protocol = new BowireMcpProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var services = await protocol.DiscoverAsync(
            "http://127.0.0.2:1", showInternalServices: false, cts.Token);

        Assert.Empty(services);
    }

    [Fact]
    public async Task InvokeAsync_Unreachable_Endpoint_Returns_Error_Status_Without_Throwing()
    {
        var protocol = new BowireMcpProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var result = await protocol.InvokeAsync(
            "http://127.0.0.2:1",
            "Tools",
            "myTool",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            cts.Token);

        Assert.NotNull(result);
        // The exception message becomes the Status; "OK" only happens on the
        // happy path which we can't reach without a real server.
        Assert.NotEqual("OK", result.Status);
    }

    [Fact]
    public async Task InvokeStreamAsync_Against_An_Unreachable_Server_Is_One_Error_Frame()
    {
        // #46 — the stream used to be empty by design. Now it carries the
        // call; a call that cannot be made says why, once.
        var protocol = new BowireMcpProtocol();

        var collected = new List<string>();
        await foreach (var item in protocol.InvokeStreamAsync(
            "http://127.0.0.1:1", "Tools", "x", ["{}"], false, null,
            TestContext.Current.CancellationToken))
        {
            collected.Add(item);
        }

        using var frame = System.Text.Json.JsonDocument.Parse(Assert.Single(collected));
        Assert.Equal("error", frame.RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public async Task OpenChannelAsync_Returns_Null_Because_Mcp_Has_No_Channel_Surface()
    {
        var protocol = new BowireMcpProtocol();

        var channel = await protocol.OpenChannelAsync(
            "http://localhost:5000", "Tools", "x", false, null,
            TestContext.Current.CancellationToken);

        Assert.Null(channel);
    }

}
