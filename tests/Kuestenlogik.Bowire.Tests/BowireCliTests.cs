// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Smoke tests for <see cref="BowireCli.RunAsync"/>'s parser surface.
/// The handlers behind each subcommand spin up real servers / network
/// calls so we can't exercise them as units; instead we drive the
/// entry point with --help / unknown-subcommand inputs that
/// System.CommandLine resolves locally without dispatching to a
/// handler. The result confirms BuildRoot wires every documented
/// subcommand at the correct nesting level.
/// </summary>
[Collection("ConsoleOutSerialised")]
public sealed class BowireCliTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().Build();

    [Fact]
    public async Task RunAsync_RootHelp_PrintsAndReturnsZero()
    {
        var prev = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var rc = await BowireCli.RunAsync(["--help"], EmptyConfig(), pluginDir: "");
            Assert.Equal(0, rc);
            var output = sw.ToString();
            // Every subcommand should appear in the help blob.
            Assert.Contains("list", output, StringComparison.Ordinal);
            Assert.Contains("describe", output, StringComparison.Ordinal);
            Assert.Contains("call", output, StringComparison.Ordinal);
            Assert.Contains("mock", output, StringComparison.Ordinal);
            Assert.Contains("mcp", output, StringComparison.Ordinal);
            Assert.Contains("plugin", output, StringComparison.Ordinal);
            Assert.Contains("test", output, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(prev);
        }
    }

    [Theory]
    [InlineData("list")]
    [InlineData("describe")]
    [InlineData("call")]
    [InlineData("mock")]
    [InlineData("plugin")]
    [InlineData("test")]
    public async Task RunAsync_SubcommandHelp_PrintsAndReturnsZero(string subcommand)
    {
        var prev = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var rc = await BowireCli.RunAsync([subcommand, "--help"], EmptyConfig(), pluginDir: "");
            Assert.Equal(0, rc);
            Assert.NotEmpty(sw.ToString());
        }
        finally
        {
            Console.SetOut(prev);
        }
    }

    [Fact]
    public async Task RunAsync_McpServe_Help_PrintsAndReturnsZero()
    {
        // mcp is a parent command — its concrete handler lives on the
        // serve subcommand. Help on either should be a no-op exit.
        var prev = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var rc = await BowireCli.RunAsync(
                ["mcp", "serve", "--help"], EmptyConfig(), pluginDir: "");
            Assert.Equal(0, rc);
            Assert.Contains("--bind", sw.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(prev);
        }
    }

    [Theory]
    [InlineData("install")]
    [InlineData("download")]
    [InlineData("list")]
    [InlineData("uninstall")]
    [InlineData("update")]
    [InlineData("inspect")]
    public async Task RunAsync_PluginSubcommand_Help_PrintsAndReturnsZero(string sub)
    {
        var prev = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var rc = await BowireCli.RunAsync(
                ["plugin", sub, "--help"], EmptyConfig(), pluginDir: "");
            Assert.Equal(0, rc);
            Assert.NotEmpty(sw.ToString());
        }
        finally
        {
            Console.SetOut(prev);
        }
    }
}
