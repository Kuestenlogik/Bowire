// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.PluginLoading;

/// <summary>
/// Outcome of attempting to load one plugin from disk. Returned (alongside
/// every other plugin's result) by the loader so the host can surface
/// per-plugin health to operators instead of silently skipping a broken
/// install. <see cref="Status"/> distinguishes the failure modes the
/// loader can detect deterministically; <see cref="ErrorMessage"/> carries
/// a human-readable explanation suitable for logs and /api/plugins/health.
/// </summary>
public sealed record PluginLoadResult(
    string PackageId,
    string DirectoryPath,
    PluginLoadStatus Status,
    string? ErrorMessage);

/// <summary>
/// Reasons a plugin load can land in.
/// </summary>
public enum PluginLoadStatus
{
    /// <summary>Manifest assembly found, loaded into the plugin ALC, no detectable contract issue.</summary>
    Loaded,

    /// <summary>The subdirectory exists but does not contain a <c>&lt;packageId&gt;.dll</c> manifest.</summary>
    ManifestMissing,

    /// <summary>
    /// The plugin's referenced Kuestenlogik.Bowire major version doesn't match the host's.
    /// Plugin would technically load but its types would call into a contract the host no longer ships.
    /// Detected pre-load by reading the AssemblyRef table directly via
    /// <see cref="System.Reflection.Metadata.MetadataReader"/>.
    /// </summary>
    ContractMajorMismatch,

    /// <summary>
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext.LoadFromAssemblyPath"/> threw.
    /// Usually a corrupt DLL, a missing transitive native dep, or an incompatible target framework.
    /// </summary>
    AssemblyLoadFailed,

    /// <summary>
    /// Loader saw the subdirectory before this call and skipped it on idempotency grounds —
    /// not a failure, just informational so /api/plugins/health can still list the plugin.
    /// </summary>
    AlreadyLoaded
}
