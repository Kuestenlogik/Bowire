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
// No [Collection] needed any more — BowireCli.RunAsync takes
// stdout/stderr TextWriter parameters that flow into
// InvocationConfiguration, so System.CommandLine's help output goes
// straight into the test's StringWriter without touching Console.Out.
public sealed class BowireCliTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().Build();

    [Fact]
    public async Task RunAsync_RootHelp_PrintsAndReturnsZero()
    {
        using var sw = new StringWriter();
        var rc = await BowireCli.RunAsync(["--help"], EmptyConfig(), pluginDir: "",
            stdout: sw, stderr: TextWriter.Null);
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
        // scan ships as an IBowireCliCommand contribution from
        // Kuestenlogik.Bowire.Security.Scanner — assert it lands
        // in the root help blob so the auto-discovery + Scanner-
        // assembly force-load stay wired. The 1.5.1 release went
        // out specifically to repair this path after a previous
        // refactor lost the eager assembly reference.
        Assert.Contains("scan", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("describe")]
    [InlineData("call")]
    [InlineData("mock")]
    [InlineData("plugin")]
    [InlineData("test")]
    [InlineData("scan")]
    public async Task RunAsync_SubcommandHelp_PrintsAndReturnsZero(string subcommand)
    {
        using var sw = new StringWriter();
        var rc = await BowireCli.RunAsync([subcommand, "--help"], EmptyConfig(), pluginDir: "",
            stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);
        Assert.NotEmpty(sw.ToString());
    }

    [Fact]
    public async Task RunAsync_TestHelp_ListsEnvFileAndVarsAlias()
    {
        // #181 — the CI-runner flag surface: --env-file and the --vars
        // alias for --env must be wired on the test subcommand.
        using var sw = new StringWriter();
        var rc = await BowireCli.RunAsync(["test", "--help"], EmptyConfig(), pluginDir: "",
            stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);
        var output = sw.ToString();
        Assert.Contains("--env-file", output, StringComparison.Ordinal);
        Assert.Contains("--vars", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_McpServe_Help_PrintsAndReturnsZero()
    {
        // mcp is a parent command — its concrete handler lives on the
        // serve subcommand. Help on either should be a no-op exit.
        using var sw = new StringWriter();
        var rc = await BowireCli.RunAsync(
            ["mcp", "serve", "--help"], EmptyConfig(), pluginDir: "",
            stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);
        Assert.Contains("--bind", sw.ToString(), StringComparison.Ordinal);
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
        using var sw = new StringWriter();
        var rc = await BowireCli.RunAsync(
            ["plugin", sub, "--help"], EmptyConfig(), pluginDir: "",
            stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);
        Assert.NotEmpty(sw.ToString());
    }
}
