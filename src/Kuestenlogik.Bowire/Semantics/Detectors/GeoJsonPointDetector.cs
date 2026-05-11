// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Semantics.Detectors;

/// <summary>
/// Detects <a href="https://datatracker.ietf.org/doc/html/rfc7946#section-3.1.2">
/// GeoJSON Point geometries</a> — an object with
/// <c>type == "Point"</c> and a 2- or 3-element <c>coordinates</c>
/// array of numbers. GeoJSON pins coordinate order as
/// <c>[longitude, latitude]</c> (and optional altitude), so the
/// emitted tags go on <c>coordinates[0]</c> as longitude and
/// <c>coordinates[1]</c> as latitude.
/// </summary>
/// <remarks>
/// <para>
/// The third optional element (altitude in metres) is left
/// unannotated in v1 — there's no built-in <c>coordinate.altitude</c>
/// kind yet, and the map widget reads only lat/lon.
/// </para>
/// <para>
/// Range checks <c>[-90, 90]</c> on latitude and <c>[-180, 180]</c>
/// on longitude catch the most obvious false-positive case: a 2D
/// vector named <c>"coordinates"</c> in an object also called
/// <c>"Point"</c> for unrelated reasons.
/// </para>
/// </remarks>
public sealed class GeoJsonPointDetector : IBowireFieldDetector
{
    /// <inheritdoc/>
    public string Id => "kuestenlogik.geojson-point";

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
                if (!node.TryGetProperty("type", out var typeProp)) return;
                if (typeProp.ValueKind != JsonValueKind.String) return;
                if (!string.Equals(typeProp.GetString(), "Point", StringComparison.Ordinal)) return;

                if (!node.TryGetProperty("coordinates", out var coordsProp)) return;
                if (coordsProp.ValueKind != JsonValueKind.Array) return;

                var length = coordsProp.GetArrayLength();
                if (length != 2 && length != 3) return;

                // All elements must be numbers — a "coordinates" array
                // of strings or nested objects isn't GeoJSON.
                foreach (var el in coordsProp.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Number) return;
                }

                var lon = coordsProp[0].GetDouble();
                var lat = coordsProp[1].GetDouble();
                if (lon < -180.0 || lon > 180.0) return;
                if (lat < -90.0 || lat > 90.0) return;

                results.Add(new DetectionResult(
                    new AnnotationKey(service, method, messageType, path + ".coordinates[0]"),
                    BuiltInSemanticTags.CoordinateLongitude));
                results.Add(new DetectionResult(
                    new AnnotationKey(service, method, messageType, path + ".coordinates[1]"),
                    BuiltInSemanticTags.CoordinateLatitude));
            },
            onLeaf: (_, _, _) => { });

        return results;
    }
}
