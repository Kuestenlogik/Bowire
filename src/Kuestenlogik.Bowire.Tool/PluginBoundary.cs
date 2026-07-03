// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// THE exception filter for third-party protocol-plugin boundaries.
/// Plugin transports (<c>DiscoverAsync</c> / <c>InvokeAsync</c>) may throw
/// anything, and the runners' contract is soft-fail-per-step — so a broad
/// catch is intentional there. Expressing it as
/// <c>catch (Exception ex) when (PluginBoundary.NonFatal(ex))</c> keeps
/// that intent in one documented place, lets genuinely fatal process
/// states (OOM, AV) escape instead of being swallowed, and needs no
/// analyzer suppression.
/// </summary>
internal static class PluginBoundary
{
    /// <summary>False only for exceptions no handler can meaningfully recover from.</summary>
    public static bool NonFatal(Exception ex)
        => ex is not OutOfMemoryException and not AccessViolationException;
}
