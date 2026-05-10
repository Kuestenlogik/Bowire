// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.Mcp;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests the bind-value dispatch in <see cref="McpServeCommand.RunAsync"/>.
/// The two happy paths (stdio + http) normally launch a real host;
/// here we swap the internal runner seams so the dispatch + options
/// configuration can be exercised without ever binding stdin/stdout or
/// a TCP port. The unknown-bind error path remains a fast validation
/// unit.
/// </summary>
[Collection(McpServeCommandTests.CollectionName)]
public sealed class McpServeCommandTests
{
    public const string CollectionName = "McpServeCommandSerial";

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

    [Fact]
    public async Task RunAsync_StdioBind_RoutesToStdioRunnerWithConfiguredOptions()
    {
        var prevStdio = McpServeCommand.StdioRunner;
        var prevHttp = McpServeCommand.HttpRunner;
        McpServeCommand.McpServeConfig? seen = null;
        var httpCalls = 0;
        try
        {
            McpServeCommand.StdioRunner = cfg => { seen = cfg; return Task.FromResult(7); };
            McpServeCommand.HttpRunner = _ => { httpCalls++; return Task.FromResult(99); };

            var rc = await McpServeCommand.RunAsync(
                bind: "stdio",
                port: 5081,
                allowArbitraryUrls: true,
                noEnvAllowlist: true);

            Assert.Equal(7, rc);
            Assert.Equal(0, httpCalls);
            Assert.NotNull(seen);
            Assert.Equal(5081, seen!.Port);
            Assert.True(seen.AllowArbitraryUrls);
            Assert.True(seen.NoEnvAllowlist);

            // ConfigureOptions closes over the typed inputs — apply it to
            // a fresh BowireMcpOptions instance and verify the projection.
            var opts = new BowireMcpOptions();
            seen.ConfigureOptions(opts);
            Assert.True(opts.AllowArbitraryUrls);
            Assert.False(opts.LoadAllowlistFromEnvironments); // noEnvAllowlist=true ⇒ Load=false
        }
        finally
        {
            McpServeCommand.StdioRunner = prevStdio;
            McpServeCommand.HttpRunner = prevHttp;
        }
    }

    [Fact]
    public async Task RunAsync_HttpBind_RoutesToHttpRunnerWithConfiguredOptions()
    {
        var prevStdio = McpServeCommand.StdioRunner;
        var prevHttp = McpServeCommand.HttpRunner;
        McpServeCommand.McpServeConfig? seen = null;
        var stdioCalls = 0;
        try
        {
            McpServeCommand.StdioRunner = _ => { stdioCalls++; return Task.FromResult(99); };
            McpServeCommand.HttpRunner = cfg => { seen = cfg; return Task.FromResult(13); };

            var rc = await McpServeCommand.RunAsync(
                bind: "http",
                port: 6543,
                allowArbitraryUrls: false,
                noEnvAllowlist: false);

            Assert.Equal(13, rc);
            Assert.Equal(0, stdioCalls);
            Assert.NotNull(seen);
            Assert.Equal(6543, seen!.Port);
            Assert.False(seen.AllowArbitraryUrls);
            Assert.False(seen.NoEnvAllowlist);

            var opts = new BowireMcpOptions();
            seen.ConfigureOptions(opts);
            Assert.False(opts.AllowArbitraryUrls);
            Assert.True(opts.LoadAllowlistFromEnvironments); // noEnvAllowlist=false ⇒ Load=true
        }
        finally
        {
            McpServeCommand.StdioRunner = prevStdio;
            McpServeCommand.HttpRunner = prevHttp;
        }
    }

    [Fact]
    public async Task RunAsync_StdioBind_PropagatesRunnerReturn()
    {
        var prevStdio = McpServeCommand.StdioRunner;
        try
        {
            McpServeCommand.StdioRunner = _ => Task.FromResult(42);
            var rc = await McpServeCommand.RunAsync("stdio", 0, false, false);
            Assert.Equal(42, rc);
        }
        finally
        {
            McpServeCommand.StdioRunner = prevStdio;
        }
    }
}

[CollectionDefinition(McpServeCommandTests.CollectionName, DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit collection definitions must be public.
#pragma warning disable CA1711 // Suffix "Collection" is xUnit convention.
public sealed class McpServeCommandTestsCollection { }
#pragma warning restore CA1711
#pragma warning restore CA1515
