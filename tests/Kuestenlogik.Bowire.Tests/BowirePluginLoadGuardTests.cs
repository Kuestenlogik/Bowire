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
    // InlineData carries only constants (a first token + the expected
    // decision); the args array is built in the body via a collection
    // expression, which sidesteps the attribute-constant restriction that
    // an array-creation InlineData argument trips (CS0182).
    [Theory]
    [InlineData("plugin", true)]       // the `plugin` group skips the eager load
    [InlineData("discover", false)]    // everything else needs protocols loaded
    [InlineData("mock", false)]
    [InlineData("mcp", false)]
    [InlineData("call", false)]
    [InlineData("pluginish", false)]   // not the exact `plugin` token
    [InlineData("", false)]
    public void FirstToken_DecidesEagerLoad(string firstToken, bool skipsLoad)
        => Assert.Equal(skipsLoad, BowireCli.IsPluginManagementCommand([firstToken]));

    [Fact]
    public void MultiTokenPluginVerb_SkipsLoad()
        => Assert.True(BowireCli.IsPluginManagementCommand(["plugin", "uninstall", "Some.Pkg"]));

    [Fact]
    public void NoArgs_EagerLoad()  // bare `bowire` -> browser UI, needs plugins loaded
        => Assert.False(BowireCli.IsPluginManagementCommand([]));

    [Fact]
    public void NullArgs_Throws()
        => Assert.Throws<ArgumentNullException>(() => BowireCli.IsPluginManagementCommand(null!));
}
