// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.PluginLoading;

namespace Kuestenlogik.Bowire.App.Plugins;

/// <summary>
/// Owns the plugin load contexts for one Bowire instance (#546).
/// </summary>
/// <remarks>
/// <para>
/// This is the whole state-owning surface of plugin management. Everything
/// else on <see cref="PluginManager"/> — install, update, uninstall,
/// inspect, list, download — is a stateless function of a package id, a
/// resolved directory and a writer pair, and stays static.
/// </para>
/// <para>
/// Deliberately small. The interface exists so a caller holds plugin
/// loading as an object instead of reaching for process-global fields,
/// not so every verb gets an abstraction.
/// </para>
/// <para>
/// One thing an interface cannot fix, and the ticket says so plainly:
/// assembly loading is process-wide. Two loaders keep separate ledgers and
/// separate <see cref="System.Runtime.Loader.AssemblyLoadContext"/>s, but
/// once an assembly is loaded it stays visible to the whole process
/// through <c>AppDomain.CurrentDomain.GetAssemblies()</c>. The goal is one
/// owner for that global state, reached through an interface — not the
/// absence of global state, which is not on offer.
/// </para>
/// </remarks>
internal interface IBowirePluginLoader
{
    /// <summary>The directory this loader reads, and the layers it came from.</summary>
    BowirePluginOptions Options { get; }

    /// <summary>
    /// Load every plugin package under <see cref="BowirePluginOptions.PluginDirectory"/>.
    /// Idempotent: a package already in this loader's ledger comes back as
    /// <see cref="PluginLoadStatus.AlreadyLoaded"/> rather than getting a
    /// second load context. Returns one result per package directory,
    /// including the ones that failed and why.
    /// </summary>
    IReadOnlyList<PluginLoadResult> Load();

    /// <summary>
    /// Results of the most recent <see cref="Load"/>, or an empty list
    /// before the first call.
    /// </summary>
    IReadOnlyList<PluginLoadResult> LastResults { get; }

    /// <summary>
    /// Instantiate every <typeparamref name="T"/> contributed by a loaded
    /// plugin or by a <c>Kuestenlogik.Bowire*</c> assembly shipped next to
    /// the host. Types need a public parameterless constructor, matching
    /// the discovery contract <see cref="IBowireProtocol"/> already uses.
    /// </summary>
    List<T> EnumerateServices<T>() where T : class;
}
