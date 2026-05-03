// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mcp;
using Kuestenlogik.Bowire.Mock;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the MCP-side registry that owns running <see cref="MockServer"/>
/// handles. The registry's negative paths (unknown handle, empty snapshot,
/// dispose-on-empty) are exercised here without spinning up a real Kestrel
/// instance — that's the integration tests' job.
/// </summary>
public class BowireMockHandleRegistryTests
{
    [Fact]
    public async Task New_Registry_Snapshot_Is_Empty()
    {
        await using var registry = new BowireMockHandleRegistry();

        var snapshot = registry.Snapshot();

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot);
    }

    [Fact]
    public async Task TryGet_Unknown_Handle_Returns_False()
    {
        await using var registry = new BowireMockHandleRegistry();

        var found = registry.TryGet("does-not-exist", out var server);

        Assert.False(found);
        Assert.Null(server);
    }

    [Fact]
    public async Task RemoveAndDisposeAsync_Unknown_Handle_Returns_False()
    {
        await using var registry = new BowireMockHandleRegistry();

        var stopped = await registry.RemoveAndDisposeAsync("nope");

        Assert.False(stopped);
    }

    [Fact]
    public async Task DisposeAsync_On_Empty_Registry_Is_Idempotent()
    {
        var registry = new BowireMockHandleRegistry();

        await registry.DisposeAsync();
        await registry.DisposeAsync(); // second call must not throw

        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public async Task TryGet_With_Empty_Or_Whitespace_Returns_False()
    {
        await using var registry = new BowireMockHandleRegistry();

        Assert.False(registry.TryGet("", out var s1));
        Assert.Null(s1);

        Assert.False(registry.TryGet(" ", out var s2));
        Assert.Null(s2);
    }
}
