// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// Where the MCP surface reads Bowire's own configuration from (#616).
/// </summary>
/// <remarks>
/// <para>
/// Four call sites across three types each rebuilt
/// <c>Path.Combine(home, ".bowire", …)</c> with their own copy of the home
/// override. Which meant that when the storage root moved — a project that
/// opted into <c>.bowire/</c>, an instance segment, the machine scope — the
/// MCP tools kept answering from the old place, and did so while reporting
/// success.
/// </para>
/// <para>
/// <see cref="HomeDirOverride"/> survives as the narrower seam the MCP tests
/// already use. It is a <em>home directory</em>, not a storage root, which is
/// why it still appends <c>.bowire</c>; <c>BOWIRE_DATA_DIR</c> is the broader
/// replacement and needs no such assumption.
/// </para>
/// </remarks>
internal static class McpPaths
{
    /// <summary>
    /// Test-only home-directory override. Production callers leave it null.
    /// </summary>
    /// <remarks>
    /// One property rather than one per type: the tests set it to redirect
    /// "everything MCP reads", and three separate switches made that a promise
    /// each new call site had to remember to keep.
    /// </remarks>
    internal static string? HomeDirOverride { get; set; }

    /// <summary>A file directly under the Bowire storage root.</summary>
    internal static string Config(string filename)
        => HomeDirOverride is { Length: > 0 } home
            ? Path.Combine(home, ".bowire", filename)
            : BowirePaths.Resolve(BowireStorageScope.Data, filename);

    /// <summary>The plugin directory.</summary>
    /// <remarks>
    /// The test override still wins — it exists to keep a suite off the
    /// developer's real <c>~/.bowire</c>. Below it,
    /// <see cref="Kuestenlogik.Bowire.Plugins.BowirePluginRoot"/> rather than the raw resolver,
    /// so <c>bowire.plugins</c> reports the directory a host started with
    /// <c>--plugin-dir</c> actually loads from (#549).
    /// </remarks>
    internal static string Plugins()
        => HomeDirOverride is { Length: > 0 } home
            ? Path.Combine(home, ".bowire", "plugins")
            : Kuestenlogik.Bowire.Plugins.BowirePluginRoot.Current;
}
