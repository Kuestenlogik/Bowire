// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.App.Plugins;

/// <summary>
/// The one answer to "which directory holds the plugins" (#546).
/// </summary>
/// <remarks>
/// <para>
/// Before this type there were two: <c>PluginManager.ResolvePluginDir</c>
/// read <c>BOWIRE_PLUGIN_DIR</c> straight off the process environment,
/// while <c>BowireConfiguration</c> mapped the same variable into
/// <c>Bowire:PluginDir</c> and let the configuration stack rank it against
/// <c>appsettings.json</c> and <c>--plugin-dir</c>. Two code paths for one
/// question means a test that clears the variable changes the answer for
/// whoever runs next — the coupling that defeated four attempted fixes in
/// #543.
/// </para>
/// <para>
/// Precedence, highest first:
/// </para>
/// <list type="number">
///   <item>An explicit path — <c>--plugin-dir</c> already parsed, or a
///   test handing over a temp directory.</item>
///   <item><c>Bowire:PluginDir</c> from a supplied
///   <see cref="IConfiguration"/>. <c>BOWIRE_PLUGIN_DIR</c> arrives through
///   this layer, so a configured caller never needs the environment.</item>
///   <item><c>BOWIRE_PLUGIN_DIR</c> read directly — <b>only</b> when no
///   configuration was supplied. With a configuration in hand, reading the
///   variable again here would reintroduce the second answer this type
///   exists to remove.</item>
///   <item><see cref="DefaultDirectory"/>.</item>
/// </list>
/// <para>
/// Construct it directly — <c>new BowirePluginOptions { PluginDirectory =
/// tmp }</c> — to get plugin management with an explicit directory and no
/// ambient state at all. That is acceptance criterion 1 of #546, and it is
/// why the property is <c>required</c> rather than defaulted.
/// </para>
/// </remarks>
internal sealed record BowirePluginOptions
{
    /// <summary>Environment variable that overrides the default plugin path.</summary>
    public const string EnvVarName = "BOWIRE_PLUGIN_DIR";

    /// <summary>Configuration key the variable and <c>--plugin-dir</c> both land on.</summary>
    public const string ConfigurationKey = "Bowire:PluginDir";

    /// <summary>Per-user, self-contained fallback: <c>~/.bowire/plugins/</c>.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".bowire", "plugins");

    /// <summary>
    /// Absolute path to the active plugin directory. Absolute so install,
    /// list, uninstall and load all agree regardless of working-directory
    /// drift between the CLI parse and the load.
    /// </summary>
    public required string PluginDirectory { get; init; }

    /// <summary>
    /// Resolve the directory from the layers documented on the type.
    /// </summary>
    /// <param name="explicitPath">
    /// A path the caller already has in hand. Whitespace counts as absent,
    /// so <c>--plugin-dir ""</c> falls through instead of resolving to the
    /// working directory.
    /// </param>
    /// <param name="configuration">
    /// The configuration stack, when the caller has one. Supplying it
    /// suppresses the direct environment read.
    /// </param>
    /// <param name="basePath">
    /// Root a relative path is resolved against. Defaults to the process
    /// working directory; pass it explicitly to keep a test off the
    /// process-global cwd.
    /// </param>
    public static BowirePluginOptions Resolve(
        string? explicitPath = null,
        IConfiguration? configuration = null,
        string? basePath = null)
    {
        var picked = FirstConfigured(
            explicitPath,
            configuration?[ConfigurationKey],
            configuration is null ? Environment.GetEnvironmentVariable(EnvVarName) : null);

        return new BowirePluginOptions
        {
            PluginDirectory = picked is null
                ? DefaultDirectory
                : Absolutise(picked, basePath),
        };
    }

    private static string? FirstConfigured(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    private static string Absolutise(string path, string? basePath)
        => string.IsNullOrWhiteSpace(basePath)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, basePath);
}
