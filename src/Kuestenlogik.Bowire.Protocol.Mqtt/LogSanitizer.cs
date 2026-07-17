// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Kuestenlogik.Bowire.Protocol.Mqtt;

/// <summary>
/// Strips CR + LF from values that flow from a client-controlled MQTT
/// message (topic / response-topic) into ILogger calls — CodeQL rule
/// <c>cs/log-forging</c>. An MQTT client picks its own topic strings, so
/// a topic carrying an embedded newline could otherwise forge a second
/// log line. The structured-log path doesn't escape parameter values, so
/// sanitise at the boundary before the value reaches the logger.
/// </summary>
/// <remarks>
/// Mirrors <c>Kuestenlogik.Bowire.Interceptor.LogSanitizer</c>; kept as a
/// local copy so this protocol plugin stays self-contained and doesn't
/// take a dependency on the interceptor assembly for a leaf utility.
/// </remarks>
internal static class LogSanitizer
{
    /// <summary>
    /// Returns <paramref name="value"/> with CR + LF removed. Null +
    /// empty pass through unchanged.
    /// </summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Strip(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        // The fast path: no CR + no LF → no allocation.
        if (value.AsSpan().IndexOfAny('\r', '\n') < 0) return value;
        return value.Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Replace("\n", string.Empty, StringComparison.Ordinal);
    }
}
