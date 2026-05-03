// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.PluginLoading;

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Diff a recording's protocol IDs against the protocols actually
/// registered in <see cref="BowireProtocolRegistry"/>. Anything the
/// recording references but the host doesn't know about is returned
/// as a <see cref="MissingPlugin"/> with a suggested NuGet install
/// line.
/// </summary>
public static class MissingPluginDetector
{
    /// <summary>
    /// Compute the missing plugins for <paramref name="recording"/>
    /// against the supplied <paramref name="loaded"/> protocol ids
    /// (typically the ids surfaced by
    /// <see cref="BowireProtocolRegistry.Protocols"/>).
    /// </summary>
    public static IReadOnlyList<MissingPlugin> Detect(
        BowireRecording recording,
        IEnumerable<string> loaded)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(loaded);

        var loadedSet = new HashSet<string>(loaded, StringComparer.OrdinalIgnoreCase);
        var protocols = RecordingProtocolScanner.Scan(recording);
        var missing = new List<MissingPlugin>();
        foreach (var id in protocols)
        {
            if (loadedSet.Contains(id)) continue;
            missing.Add(new MissingPlugin(id, PluginPackageMap.TryGetPackageId(id)));
        }
        return missing;
    }
}
