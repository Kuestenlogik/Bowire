// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Program.cs skips the eager <c>plugins.Load()</c> for the <c>plugin</c>
/// management group so an eager assembly load can't memory-map (and on
/// Windows lock) the DLL that a subsequent <c>plugin uninstall</c> /
/// <c>update</c> must delete. <see cref="BowireCli.IsPluginManagementCommand"/>
/// is that decision; these guard which invocations skip the load.
/// </summary>
public sealed class BowirePluginLoadGuardTests
{
    [Theory]
    [InlineData(new[] { "plugin" })]                          // bare group -> help, no protocols needed
    [InlineData(new[] { "plugin", "uninstall", "Some.Pkg" })]
    [InlineData(new[] { "plugin", "update" })]
    [InlineData(new[] { "plugin", "install", "Some.Pkg" })]
    [InlineData(new[] { "plugin", "list" })]
    public void PluginVerbs_SkipEagerLoad(string[] args)
        => Assert.True(BowireCli.IsPluginManagementCommand(args));

    [Theory]
    [InlineData(new string[0])]                               // no args -> browser UI, needs plugins loaded
    [InlineData(new[] { "discover", "http://localhost:6000" })]
    [InlineData(new[] { "mock", "--schema", "s.yaml" })]
    [InlineData(new[] { "mcp", "serve" })]
    [InlineData(new[] { "call", "Svc/Method" })]
    [InlineData(new[] { "pluginish" })]                       // not the exact `plugin` token
    public void NonPluginVerbs_EagerLoad(string[] args)
        => Assert.False(BowireCli.IsPluginManagementCommand(args));

    [Fact]
    public void NullArgs_Throws()
        => Assert.Throws<ArgumentNullException>(() => BowireCli.IsPluginManagementCommand(null!));
}
