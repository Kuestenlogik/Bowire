// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// The one answer to "which directory holds the installed plugins" (#549).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BowirePaths"/> (#616) already gave every storage question a
/// single resolver, and the plugin directory rides on it — but only for the
/// default. The plugin directory is the one storage location a host can also
/// point somewhere else explicitly, through <c>Bowire:PluginDir</c>: the
/// CLI's <c>--plugin-dir</c>, the <c>BOWIRE_PLUGIN_DIR</c> environment
/// variable and an <c>appsettings.json</c> entry all bind to that key.
/// </para>
/// <para>
/// Before this type, that override reached only the loader. The workbench's
/// plugin list, the daily update check, sidecar discovery and the MCP
/// resource each went straight to the resolver default, so a host started
/// with <c>--plugin-dir X</c> installed into X and then listed
/// <c>~/.bowire/plugins</c> — the plugin appeared, in the directory the host
/// was not loading from.
/// </para>
/// <para>
/// Static, and set once at start-up, for the same reason
/// <see cref="BowirePaths"/> is: three of the four readers are static
/// properties with no constructor to inject into. Anything that <em>does</em>
/// have a constructor should keep taking the path as a parameter — this is
/// the fallback for the call sites that cannot.
/// </para>
/// </remarks>
public static class BowirePluginRoot
{
    private static string? s_configured;

    /// <summary>
    /// The configured plugin directory, or the storage resolver's default
    /// (<c>&lt;data root&gt;/plugins</c>) when no host configured one.
    /// </summary>
    /// <remarks>
    /// A property rather than a captured field: <see cref="Apply"/> runs when
    /// the host is built, which can be after this type is first touched, and
    /// <see cref="BowirePaths.Current"/> can be swapped later still. Reading
    /// through on every call is what keeps both honest.
    /// </remarks>
    public static string Current
        => s_configured ?? BowirePaths.Resolve(BowireStorageScope.Data, "plugins");

    /// <summary>Whether a host explicitly configured the directory.</summary>
    /// <remarks>
    /// The shell-out to <c>bowire plugin install</c> passes
    /// <c>--plugin-dir</c> unconditionally, so this is not needed to decide
    /// that. It exists so a caller can tell "the operator chose this" from
    /// "this is where it defaults to" when reporting.
    /// </remarks>
    public static bool IsConfigured => s_configured is not null;

    /// <summary>
    /// Point every plugin-directory reader at <paramref name="directory"/>.
    /// Null, empty or whitespace clears the override and returns the
    /// resolver default, so a host can call this unconditionally with
    /// whatever <c>Bowire:PluginDir</c> yielded.
    /// </summary>
    /// <returns>The directory in force after the call.</returns>
    public static string Apply(string? directory)
    {
        s_configured = string.IsNullOrWhiteSpace(directory)
            ? null
            // Rooted here rather than at each reader: the value arrives from
            // a command line or a config file and is routinely relative to
            // the working directory, which the update-check service (running
            // on a timer, long after start-up) has no reason to still be in.
            : Path.GetFullPath(directory);
        return Current;
    }
}
