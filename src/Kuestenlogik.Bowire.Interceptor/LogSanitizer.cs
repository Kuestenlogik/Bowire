// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// Strips CR + LF from values that flow from a network request into
/// ILogger calls — CodeQL rule <c>cs/log-forging</c>. Kestrel rejects
/// CR/LF in request lines + headers before they reach user code, so
/// the runtime risk is minimal in practice, but the structured-log
/// path doesn't escape parameter values + the static analyser can't
/// see the Kestrel pre-validation, so it flags every
/// <c>{Method}</c> / <c>{Path}</c> / <c>{Url}</c> interpolation that
/// originates from <c>HttpRequest</c>. Sanitise at the boundary so
/// the warning stops being noise.
/// </summary>
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
