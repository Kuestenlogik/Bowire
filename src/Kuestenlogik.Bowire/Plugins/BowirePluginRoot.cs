// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>Which tier a plugin directory belongs to (#28 Phase D).</summary>
public enum BowirePluginTier
{
    /// <summary>
    /// The calling user's own directory — <c>~/.bowire/plugins</c>, a
    /// project's <c>.bowire/plugins</c>, or whatever <c>--plugin-dir</c>
    /// named. Writable without administrative rights, which is what makes it
    /// the place installs land.
    /// </summary>
    User = 0,

    /// <summary>
    /// The machine-wide directory an administrator manages —
    /// <c>%ProgramData%\Bowire\plugins</c> or <c>/var/lib/bowire/plugins</c>.
    /// Every account on the host sees it; typically none of them can write to
    /// it.
    /// </summary>
    Machine = 1,
}

/// <summary>
/// The one answer to "which directories hold the installed plugins" (#549,
/// then #28 Phase D).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BowirePaths"/> (#616) gave every storage question a single
/// resolver, and the plugin directory rides on it — but only for the default.
/// Two things sit on top of that default, and both are why this type exists.
/// </para>
/// <para>
/// <b>The explicit override (#549).</b> The plugin directory is the one
/// storage location a host can also point somewhere else outright, through
/// <c>Bowire:PluginDir</c> — the CLI's <c>--plugin-dir</c>, the
/// <c>BOWIRE_PLUGIN_DIR</c> environment variable and an
/// <c>appsettings.json</c> entry all bind to that key. Before this type, that
/// override reached the loader alone, so a host started with
/// <c>--plugin-dir X</c> installed into X and then listed somewhere else.
/// </para>
/// <para>
/// <b>Two tiers (#28 Phase D).</b> "Everyone shares one
/// <c>~/.bowire/plugins</c>" has two failure modes at once: a user cannot
/// install a plugin for their own workflow without administrative rights, and
/// an administrator cannot provision one for everybody. So there are now two
/// directories — a machine-wide tier an admin manages, and the user's own
/// overlay on top of it. <see cref="EnumeratePackages"/> walks them in
/// precedence order and is the only place that ordering is expressed.
/// </para>
/// <para>
/// Static, and set once at start-up, for the same reason
/// <see cref="BowirePaths"/> is: several readers are static properties with no
/// constructor to inject into. Anything that <em>does</em> have a constructor
/// should keep taking the path as a parameter.
/// </para>
/// </remarks>
public static class BowirePluginRoot
{
    private static string? s_configured;

    /// <summary>
    /// Where a plugin install lands, and the tier the user owns: the
    /// configured directory when a host set one, the storage resolver's
    /// default otherwise.
    /// </summary>
    /// <remarks>
    /// A property rather than a captured field: <see cref="Apply"/> runs when
    /// the host is built, which can be after this type is first touched, and
    /// <see cref="BowirePaths.Current"/> can be swapped later still. Reading
    /// through on every call is what keeps both honest.
    /// </remarks>
    public static string Current
        => s_configured ?? BowirePaths.Resolve(BowireStorageScope.Data, "plugins");

    /// <summary>
    /// The machine-wide tier: <c>%ProgramData%\Bowire\plugins</c> on Windows,
    /// <c>/var/lib/bowire/plugins</c> elsewhere.
    /// </summary>
    /// <remarks>
    /// Resolved even when the directory does not exist — most installs have no
    /// machine tier at all, and an absent directory reads as "no plugins
    /// there" rather than as an error.
    /// </remarks>
    public static string MachineRoot
        => BowirePaths.Resolve(BowireStorageScope.Machine, "plugins");

    /// <summary>Whether a host explicitly configured the user directory.</summary>
    public static bool IsConfigured => s_configured is not null;

    /// <summary>
    /// The directories to search, in precedence order: the user's own first,
    /// then the machine tier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The user tier comes first so a locally installed plugin shadows a
    /// machine-wide one of the same package id — that is what makes it an
    /// overlay, and it is how someone tries a newer build without asking an
    /// administrator to change what everybody else gets.
    /// </para>
    /// <para>
    /// An explicitly configured directory yields <em>only</em> that
    /// directory. <c>--plugin-dir /tmp/isolated</c> is an operator saying
    /// which plugins are in play, and quietly adding a machine tier they did
    /// not ask for would make an isolated run stop being isolated.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(string Path, BowirePluginTier Tier)> Roots
    {
        get
        {
            var user = Current;
            if (IsConfigured) return [(user, BowirePluginTier.User)];

            var machine = MachineRoot;
            // A machine root that resolves onto the user's own directory
            // would list every package twice. Not expected on a normal
            // install, but BOWIRE_DATA_DIR can point both at one place.
            return string.Equals(Path.GetFullPath(user), Path.GetFullPath(machine), PathComparison)
                ? [(user, BowirePluginTier.User)]
                : [(user, BowirePluginTier.User), (machine, BowirePluginTier.Machine)];
        }
    }

    /// <summary>
    /// Every installed package directory across both tiers, in precedence
    /// order, with the tier it came from. A package id present in both is
    /// yielded once, from the higher-precedence tier.
    /// </summary>
    /// <remarks>
    /// The single place the overlay rule is applied. Callers that scanned one
    /// directory with <c>Directory.GetDirectories</c> should read this
    /// instead — the alternative is each of them re-deciding what happens when
    /// the same plugin exists twice, which is how the four readers in #549
    /// came to disagree in the first place.
    /// </remarks>
    public static IEnumerable<(string Directory, string PackageId, BowirePluginTier Tier)> EnumeratePackages()
        => Enumerate(Roots);

    /// <summary>
    /// The same walk, for a caller that resolved the user tier itself.
    /// </summary>
    /// <param name="userRoot">The directory to treat as the user tier.</param>
    /// <param name="includeMachineTier">
    /// Whether to search the machine tier as well. Pass <c>false</c> when
    /// <paramref name="userRoot"/> came from an operator naming a directory —
    /// an isolated run has to stay isolated.
    /// </param>
    /// <remarks>
    /// Exists because the Tool resolves the directory through
    /// <c>BowirePluginOptions</c>, which owns a precedence chain
    /// (<c>--plugin-dir</c> over the environment over configuration) that
    /// Core has no notion of. Rather than have the loader trust that some
    /// earlier call already pushed that answer into <see cref="Apply"/>, it
    /// passes what it resolved and gets the same overlay rule applied to it.
    /// </remarks>
    public static IEnumerable<(string Directory, string PackageId, BowirePluginTier Tier)>
        EnumeratePackagesUnder(string userRoot, bool includeMachineTier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userRoot);
        var user = Path.GetFullPath(userRoot);
        var machine = MachineRoot;
        var roots = !includeMachineTier
                || string.Equals(user, Path.GetFullPath(machine), PathComparison)
            ? new[] { (user, BowirePluginTier.User) }
            : [(user, BowirePluginTier.User), (machine, BowirePluginTier.Machine)];
        return Enumerate(roots);
    }

    private static IEnumerable<(string Directory, string PackageId, BowirePluginTier Tier)> Enumerate(
        IReadOnlyList<(string Path, BowirePluginTier Tier)> roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, tier) in roots)
        {
            if (!Directory.Exists(root)) continue;

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(root);
            }
            // A machine tier is frequently unreadable by the calling account,
            // which is not a reason to fail the whole enumeration — the user's
            // own plugins still load.
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var directory in subdirectories)
            {
                var packageId = Path.GetFileName(directory);
                if (packageId.Length == 0) continue;
                if (!seen.Add(packageId)) continue;
                yield return (directory, packageId, tier);
            }
        }
    }

    /// <summary>
    /// Point every plugin-directory reader at <paramref name="directory"/>.
    /// Null, empty or whitespace clears the override and returns the resolver
    /// default, so a host can call this unconditionally with whatever
    /// <c>Bowire:PluginDir</c> yielded.
    /// </summary>
    /// <returns>The user-tier directory in force after the call.</returns>
    public static string Apply(string? directory)
    {
        s_configured = string.IsNullOrWhiteSpace(directory)
            ? null
            // Rooted here rather than at each reader: the value arrives from a
            // command line or a config file and is routinely relative to the
            // working directory, which the update-check service (running on a
            // timer, long after start-up) has no reason to still be in.
            : Path.GetFullPath(directory);
        return Current;
    }

    // Windows and macOS treat paths case-insensitively; Linux does not. The
    // comparison only guards the "both tiers resolved to one directory" case,
    // where being wrong means listing every plugin twice.
    private static StringComparison PathComparison
        => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
