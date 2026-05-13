// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.PluginLoading;

/// <summary>
/// Process-wide store of the most recent <see cref="PluginLoadResult"/>
/// set the plugin loader produced. Lives in the core (Kuestenlogik.Bowire)
/// so endpoints under <see cref="Endpoints"/> can read it without taking
/// a dependency on the CLI assembly (Kuestenlogik.Bowire.Tool) where the
/// PluginManager lives. The loader publishes results via
/// <see cref="Publish"/> on every <c>LoadPlugins</c> call; the
/// <c>/api/plugins/health</c> endpoint reads <see cref="Latest"/>.
/// </summary>
/// <remarks>
/// Lock-free reads: assignment is reference-only and the published
/// collection is treated as immutable (callers freeze it before
/// handing it over). Empty list before the first <see cref="Publish"/>
/// — endpoints surface that as an empty array, which is the right
/// no-op shape for a host that hasn't loaded any plugins yet.
/// </remarks>
public static class PluginLoadResultStore
{
    private static IReadOnlyList<PluginLoadResult> s_latest =
        Array.Empty<PluginLoadResult>();

    /// <summary>Most recent results, or an empty list before the first publish.</summary>
    public static IReadOnlyList<PluginLoadResult> Latest => s_latest;

    /// <summary>
    /// Publish a fresh result set. Callers should pass a frozen
    /// collection — <see cref="List{T}.AsReadOnly()"/> over the loader's
    /// internal list is the conventional shape.
    /// </summary>
    public static void Publish(IReadOnlyList<PluginLoadResult> results)
    {
        s_latest = results ?? Array.Empty<PluginLoadResult>();
    }
}
