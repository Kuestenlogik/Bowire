// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Kuestenlogik.Bowire.Mock.Chaos;

/// <summary>
/// Chaos-injection tunables (Phase 3a). Applied by the mock middleware
/// before it dispatches to the replayer, so every matched request gets
/// the same treatment regardless of protocol or method type.
/// </summary>
/// <remarks>
/// Defaults mean "off": <see cref="FailRate"/> is <c>0</c>, latency bounds
/// are <c>null</c>. Populated either programmatically (embedded mode) or
/// from <c>--chaos "latency:100-500,fail-rate:0.05"</c> (CLI mode) via
/// <see cref="Parse"/>.
/// </remarks>
public sealed class ChaosOptions
{
    /// <summary>Lower bound of the injected delay, in milliseconds. <c>null</c> disables latency jitter.</summary>
    public int? LatencyMinMs { get; set; }

    /// <summary>Upper bound of the injected delay, in milliseconds. Must be &gt;= <see cref="LatencyMinMs"/> when both are set.</summary>
    public int? LatencyMaxMs { get; set; }

    /// <summary>Probability of failing a request before it reaches the replayer. <c>0</c> = never (default), <c>1</c> = always.</summary>
    public double FailRate { get; set; }

    /// <summary>HTTP status code returned when a fail-rate hit fires. Defaults to <c>503 Service Unavailable</c>.</summary>
    public int FailStatusCode { get; set; } = 503;

    /// <summary>True when any chaos dimension is turned on.</summary>
    public bool IsActive =>
        LatencyMinMs is not null || LatencyMaxMs is not null || FailRate > 0;

    /// <summary>
    /// Parse a <c>--chaos</c> CLI argument. Accepts a comma-separated list of
    /// <c>key:value</c> pairs. Recognised keys:
    /// <list type="bullet">
    /// <item><c>latency</c> — either a single number (fixed delay in ms) or a <c>min-max</c> range.</item>
    /// <item><c>fail-rate</c> — a probability between 0 and 1 (e.g. <c>0.05</c> for 5%).</item>
    /// <item><c>fail-status</c> — override the HTTP status for fail-rate hits (default 503).</item>
    /// </list>
    /// Throws <see cref="FormatException"/> on unrecognised keys or malformed values so CLI users see the problem early.
    /// </summary>
    public static ChaosOptions Parse(string spec)
    {
        ArgumentException.ThrowIfNullOrEmpty(spec);

        var opts = new ChaosOptions();
        var parts = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || colon == part.Length - 1)
                throw new FormatException($"--chaos: expected 'key:value', got '{part}'.");

            var key = part[..colon].Trim();
            var value = part[(colon + 1)..].Trim();

            if (Matches(key, "latency"))
            {
                ParseLatency(value, opts);
            }
            else if (Matches(key, "fail-rate") || Matches(key, "failrate"))
            {
                opts.FailRate = double.Parse(value, CultureInfo.InvariantCulture);
                if (opts.FailRate is < 0 or > 1)
                    throw new FormatException($"--chaos: fail-rate must be between 0 and 1, got {value}.");
            }
            else if (Matches(key, "fail-status") || Matches(key, "failstatus"))
            {
                opts.FailStatusCode = int.Parse(value, CultureInfo.InvariantCulture);
            }
            else
            {
                throw new FormatException(
                    $"--chaos: unknown key '{key}'. Expected one of: latency, fail-rate, fail-status.");
            }
        }

        return opts;
    }

    private static bool Matches(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static void ParseLatency(string value, ChaosOptions opts)
    {
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            var fixedMs = int.Parse(value, CultureInfo.InvariantCulture);
            if (fixedMs < 0) throw new FormatException($"--chaos: latency must be >= 0, got {value}.");
            opts.LatencyMinMs = fixedMs;
            opts.LatencyMaxMs = fixedMs;
            return;
        }

        var lo = int.Parse(value[..dash], CultureInfo.InvariantCulture);
        var hi = int.Parse(value[(dash + 1)..], CultureInfo.InvariantCulture);
        if (lo < 0 || hi < 0) throw new FormatException($"--chaos: latency bounds must be >= 0, got {value}.");
        if (hi < lo) throw new FormatException($"--chaos: latency range '{value}' is inverted.");
        opts.LatencyMinMs = lo;
        opts.LatencyMaxMs = hi;
    }
}
