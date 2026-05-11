// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Semantics.Detectors;

/// <summary>
/// Detects fields that carry timestamps — either an ISO-8601 string
/// parsed via <see cref="DateTimeOffset.TryParse(string?, IFormatProvider?, DateTimeStyles, out DateTimeOffset)"/>
/// with <see cref="DateTimeStyles.RoundtripKind"/>, or a numeric epoch
/// (seconds or milliseconds since 1970) within ±20 years of "now".
/// </summary>
/// <remarks>
/// <para>
/// Name pattern is required (the value-shape alone is not enough —
/// "42" looks like an epoch second, but at 42 seconds past the
/// Unix epoch it's almost certainly a duration in disguise). Name
/// matches:
/// </para>
/// <list type="bullet">
///   <item><description>contains <c>timestamp</c> (case-insensitive),</description></item>
///   <item><description>contains <c>time</c> (case-insensitive),</description></item>
///   <item><description>ends in <c>at</c> (case-insensitive) — <c>createdAt</c>, <c>updatedAt</c>.</description></item>
/// </list>
/// <para>
/// Epoch plausibility window: ±20 years around <see cref="DateTimeOffset.UtcNow"/>.
/// Wide enough to cover most application data; narrow enough to
/// reject random small integers (counters, sequence numbers,
/// pixel coordinates) that share the name pattern.
/// </para>
/// </remarks>
public sealed partial class TimestampDetector : IBowireFieldDetector
{
    // Bounds resolved lazily through TimeProvider so tests with a
    // frozen clock can still pin the plausibility window. Production
    // path uses TimeProvider.System.
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Default constructor — uses <see cref="TimeProvider.System"/>.
    /// Public so the DI container can wire it up without registering
    /// the time provider itself.
    /// </summary>
    public TimestampDetector() : this(TimeProvider.System) { }

    /// <summary>
    /// Test-seam constructor — accepts a custom
    /// <see cref="TimeProvider"/> so the epoch-plausibility window
    /// is pinned to a known "now" in unit tests.
    /// </summary>
    public TimestampDetector(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public string Id => "kuestenlogik.timestamp";

    /// <inheritdoc/>
    public IEnumerable<DetectionResult> Detect(in DetectionContext ctx)
    {
        var results = new List<DetectionResult>();
        var service = ctx.ServiceId;
        var method = ctx.MethodId;
        var messageType = ctx.MessageType;

        // Resolve the plausibility window once per Detect call — the
        // window's wide enough that drift inside a single frame is
        // immaterial.
        var now = _timeProvider.GetUtcNow();
        var minMs = now.AddYears(-20).ToUnixTimeMilliseconds();
        var maxMs = now.AddYears(20).ToUnixTimeMilliseconds();
        var minSec = now.AddYears(-20).ToUnixTimeSeconds();
        var maxSec = now.AddYears(20).ToUnixTimeSeconds();

        DetectorHelpers.Walk(ctx.Frame,
            onObject: (_, _) => { },
            onLeaf: (path, name, value) =>
            {
                if (string.IsNullOrEmpty(name)) return;
                if (!NamePattern().IsMatch(name)) return;

                if (value.ValueKind == JsonValueKind.String)
                {
                    var s = value.GetString();
                    if (string.IsNullOrEmpty(s)) return;
                    if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out _))
                    {
                        results.Add(new DetectionResult(
                            new AnnotationKey(service, method, messageType, path),
                            BuiltInSemanticTags.TimeseriesTimestamp));
                    }
                }
                else if (value.ValueKind == JsonValueKind.Number)
                {
                    if (!value.TryGetInt64(out var i64))
                    {
                        // Numbers with a fractional component — accept
                        // them as seconds (e.g. 1715472000.123) when
                        // their integer part falls in the plausibility
                        // window. Defends against pixel coordinates
                        // disguised as floats.
                        if (!value.TryGetDouble(out var d)) return;
                        var asInt = (long)d;
                        if (asInt >= minSec && asInt <= maxSec)
                        {
                            results.Add(new DetectionResult(
                                new AnnotationKey(service, method, messageType, path),
                                BuiltInSemanticTags.TimeseriesTimestamp));
                        }
                        return;
                    }

                    // Two plausibility tests: epoch seconds, then
                    // epoch milliseconds. Either window passing is
                    // enough — they're disjoint at the typical-data
                    // end (a millisecond reading from 2026 is ~1.7e12,
                    // which falls outside the seconds window's upper
                    // bound).
                    if ((i64 >= minSec && i64 <= maxSec)
                        || (i64 >= minMs && i64 <= maxMs))
                    {
                        results.Add(new DetectionResult(
                            new AnnotationKey(service, method, messageType, path),
                            BuiltInSemanticTags.TimeseriesTimestamp));
                    }
                }
            });

        return results;
    }

    // Per ADR: *timestamp* / *time* / *at$ — "timestamp" and "time"
    // match anywhere in the name (so "eventTime" / "lastTimestamp"
    // both fire); "at" only matches at the end (so "createdAt"
    // matches but "atmosphere" / "data" doesn't).
    [GeneratedRegex(
        @"timestamp|time|at$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
