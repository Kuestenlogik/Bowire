// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Semantics.Detectors;

/// <summary>
/// Detects paired latitude/longitude fields at the same parent object
/// path. Requires <strong>both</strong> name patterns to match — the
/// range-only fallback the ADR mentions for the future is deliberately
/// not enabled in v1 so a coordinate-shaped pair of pixel offsets
/// (e.g. <c>{x: 5.2, y: 9.1}</c> or <c>{u: -45.0, v: 87.3}</c>) cannot
/// drag the map widget onto a non-geographic field pair.
/// </summary>
/// <remarks>
/// <para>
/// Name patterns (case-insensitive, anchored):
/// </para>
/// <list type="bullet">
///   <item><description>Latitude — <c>lat</c> or <c>latitude</c>.</description></item>
///   <item><description>Longitude — <c>lon</c>, <c>long</c>, <c>lng</c>, or <c>longitude</c>.</description></item>
/// </list>
/// <para>
/// Range constraints rule out swapped fields and grossly-out-of-range
/// values: <c>[-90, 90]</c> for latitude, <c>[-180, 180]</c> for
/// longitude.
/// </para>
/// <para>
/// Two distinct lat-shaped fields at the same parent (e.g. a
/// <c>start</c> + <c>end</c> pair of <c>lat</c> entries with no
/// matching <c>lon</c>) do not emit any results — the rule is
/// strictly "one lat + one lon at the same parent."
/// </para>
/// </remarks>
public sealed partial class Wgs84CoordinateDetector : IBowireFieldDetector
{
    /// <inheritdoc/>
    public string Id => "kuestenlogik.wgs84-coordinate";

    /// <inheritdoc/>
    public IEnumerable<DetectionResult> Detect(in DetectionContext ctx)
    {
        var results = new List<DetectionResult>();
        var service = ctx.ServiceId;
        var method = ctx.MethodId;
        var messageType = ctx.MessageType;

        DetectorHelpers.Walk(ctx.Frame,
            onObject: (path, node) =>
            {
                // Within a single object: collect every name that
                // looks like a lat/lon and every name that looks like
                // the other half, then pair them. With at most two of
                // each, the cross-product is tiny — the inner cost is
                // dominated by the regex check.
                string? latName = null;
                double? latValue = null;
                string? lonName = null;
                double? lonValue = null;
                var latCount = 0;
                var lonCount = 0;

                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Number) continue;
                    if (!prop.Value.TryGetDouble(out var d)) continue;

                    if (LatPattern().IsMatch(prop.Name))
                    {
                        latCount++;
                        if (latCount == 1)
                        {
                            latName = prop.Name;
                            latValue = d;
                        }
                    }
                    else if (LonPattern().IsMatch(prop.Name))
                    {
                        lonCount++;
                        if (lonCount == 1)
                        {
                            lonName = prop.Name;
                            lonValue = d;
                        }
                    }
                }

                // Ambiguous shapes (two lats, two lons) don't fire —
                // the user resolves with one right-click. False
                // positives are the more costly failure mode here.
                if (latCount != 1 || lonCount != 1) return;
                if (latValue is null || lonValue is null) return;
                if (latValue < -90.0 || latValue > 90.0) return;
                if (lonValue < -180.0 || lonValue > 180.0) return;

                var latPath = path + "." + latName;
                var lonPath = path + "." + lonName;

                results.Add(new DetectionResult(
                    new AnnotationKey(service, method, messageType, latPath),
                    BuiltInSemanticTags.CoordinateLatitude));
                results.Add(new DetectionResult(
                    new AnnotationKey(service, method, messageType, lonPath),
                    BuiltInSemanticTags.CoordinateLongitude));
            },
            onLeaf: (_, _, _) => { });

        return results;
    }

    // Anchored case-insensitive name patterns. ADR keeps them strict —
    // "Lat" / "latitude" yes, "latency" no.
    [GeneratedRegex("^lat(itude)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LatPattern();

    [GeneratedRegex("^lo?ng(itude)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LonPattern();
}
