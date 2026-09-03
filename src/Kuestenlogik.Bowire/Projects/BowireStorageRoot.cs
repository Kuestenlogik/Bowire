// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Projects;

/// <summary>
/// Decides where a Bowire instance keeps its collections, environments,
/// recordings and presets (#591).
/// </summary>
/// <remarks>
/// <para>
/// Two answers are possible, and which one applies is a property of the
/// <em>repository</em>, not of whoever started the process:
/// </para>
/// <list type="bullet">
/// <item>
/// No manifest, or one without <c>"storage": "project"</c> — the machine-wide
/// <c>~/.bowire/</c>. Unchanged behaviour, and still the default.
/// </item>
/// <item>
/// A <c>.bowire/project.json</c> that asks for project storage — that
/// directory. The data then travels with the checkout, is git-diff-able, and
/// two repos open at once no longer share one set of collections.
/// </item>
/// </list>
/// <para>
/// Reading it from the manifest rather than from a CLI flag is what makes it
/// consistent: the CLI, the standalone tool and an IDE extension all discover
/// the same file by walking up from where they were started, so the same
/// checkout resolves to the same place no matter how Bowire was launched.
/// A flag would have made the answer depend on the launcher, which is the
/// class of bug this issue exists to remove — the VS Code extension claimed
/// for months that running the CLI with the workspace as its working directory
/// put <c>.bowire/</c> next to the code, and the working directory had no
/// bearing on it whatsoever.
/// </para>
/// <para>
/// Opt-in rather than automatic on purpose: rooting storage at the project the
/// moment any manifest exists would silently relocate the data of everyone
/// already using <c>project.json</c> for sources and rules.
/// </para>
/// </remarks>
public static class BowireStorageRoot
{
    /// <summary>
    /// The directory Bowire should store user data under, given where it was
    /// started.
    /// </summary>
    /// <param name="startDirectory">
    /// Where to begin the walk up for <c>.bowire/project.json</c>. Defaults to
    /// the current working directory.
    /// </param>
    /// <returns>
    /// <c>BOWIRE_DATA_DIR</c> when it is set; the project's <c>.bowire/</c>
    /// directory when its manifest opts in;
    /// <see cref="DefaultBowireUserStore.UserProfileRoot"/> otherwise.
    /// </returns>
    public static string Resolve(string? startDirectory = null)
    {
        // #643 — BOWIRE_DATA_DIR outranks everything, including a project
        // manifest, exactly as it does in BowirePathResolver. Without this
        // the variable moved the plugin directory and left every
        // workspace-scoped artifact — collections, recordings, flows, plugin
        // settings — in the real ~/.bowire, so a run that believed it was
        // isolated wrote into the developer's own storage and left
        // directories behind.
        if (BowirePathResolver.DataDirOverride() is { } redirected) return redirected;

        var located = BowireProjectLoader.Discover(startDirectory);
        if (located is null) return DefaultBowireUserStore.UserProfileRoot;

        BowireProjectFile manifest;
        try
        {
            manifest = BowireProjectLoader.Load(located.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException or ArgumentException)
        {
            // A manifest that cannot be read must not take the storage down
            // with it. Falling back to the machine-wide default keeps the
            // instance usable; `bowire project validate` is where a broken
            // manifest gets reported, loudly and on purpose.
            return DefaultBowireUserStore.UserProfileRoot;
        }

        return manifest.UsesProjectStorage
            ? Path.Combine(located.ProjectRoot, BowireProjectLoader.ConventionDirName)
            : DefaultBowireUserStore.UserProfileRoot;
    }

    /// <summary>
    /// Point <see cref="BowireUserContext.Current"/> at whatever
    /// <see cref="Resolve"/> decides. Called once at host start-up.
    /// </summary>
    /// <returns>The root that was applied, for logging.</returns>
    public static string Apply(string? startDirectory = null)
    {
        var root = Resolve(startDirectory);
        BowireUserContext.Current = new DefaultBowireUserStore(root);
        return root;
    }
}
