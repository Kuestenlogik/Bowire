// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests the bind-value dispatch in <see cref="McpServeCommand.RunAsync"/>.
/// The two happy paths (stdio + http) launch a real host so they're left
/// to the integration harness; the unknown-bind error path is pure
/// validation logic and worth keeping as a fast unit test.
/// </summary>
public sealed class McpServeCommandTests
{
    [Fact]
    public async Task RunAsync_UnknownBind_ReturnsUsageExit()
    {
        var rc = await McpServeCommand.RunAsync(
            bind: "nonsense",
            port: 5081,
            allowArbitraryUrls: false,
            noEnvAllowlist: false);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_EmptyBind_ReturnsUsageExit()
    {
        // Empty string also doesn't match either case in the switch.
        var rc = await McpServeCommand.RunAsync(
            bind: "",
            port: 5081,
            allowArbitraryUrls: false,
            noEnvAllowlist: false);
        Assert.Equal(2, rc);
    }
}
