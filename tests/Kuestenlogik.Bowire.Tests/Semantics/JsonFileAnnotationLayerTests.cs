// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Semantics;

namespace Kuestenlogik.Bowire.Tests.Semantics;

public sealed class JsonFileAnnotationLayerTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    public void Dispose()
    {
        foreach (var p in _tempPaths)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
        GC.SuppressFinalize(this);
    }

    private string NewTempPath()
    {
        var p = Path.Combine(Path.GetTempPath(), $"bowire-schema-hints-{Guid.NewGuid():N}.json");
        _tempPaths.Add(p);
        return p;
    }

    [Fact]
    public async Task LoadAsync_Treats_Missing_File_As_Empty()
    {
        using var layer = new JsonFileAnnotationLayer(NewTempPath());
        await layer.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(layer.IsLoaded);
        Assert.Equal(0, layer.Count);
    }

    [Fact]
    public async Task SaveAsync_Then_LoadAsync_RoundTrips_The_Entries()
    {
        var path = NewTempPath();
        var entries = new[]
        {
            new KeyValuePair<AnnotationKey, SemanticTag>(
                new AnnotationKey("dis", "Subscribe", "EntityStatePdu", "$.entityLocation.x"),
                BuiltInSemanticTags.CoordinateEcefX),
            new KeyValuePair<AnnotationKey, SemanticTag>(
                new AnnotationKey("dis", "Subscribe", "EntityStatePdu", "$.entityLocation.y"),
                BuiltInSemanticTags.CoordinateEcefY),
            new KeyValuePair<AnnotationKey, SemanticTag>(
                AnnotationKey.ForSingleType("harbor", "WatchCrane", "$.position.lat"),
                BuiltInSemanticTags.CoordinateLatitude),
        };

        using (var writer = new JsonFileAnnotationLayer(path))
        {
            writer.Replace(entries);
            await writer.SaveAsync(TestContext.Current.CancellationToken);
        }

        using var reader = new JsonFileAnnotationLayer(path);
        await reader.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, reader.Count);
        foreach (var (k, v) in entries)
        {
            Assert.Equal(v, reader.Get(k));
        }
    }

    [Fact]
    public async Task On_Disk_Shape_Matches_Adr_Example()
    {
        // Build the exact entries the ADR's "Persistence" section
        // demonstrates: a DIS Subscribe method with EntityStatePdu /
        // FirePdu sub-shapes, plus a single-type Harbor WatchCrane
        // method under the "*" wildcard.
        var path = NewTempPath();
        using (var layer = new JsonFileAnnotationLayer(path))
        {
            layer.Replace(
            [
                new(new("dis.LiveExercise", "Subscribe", "EntityStatePdu", "$.entityLocation.x"), BuiltInSemanticTags.CoordinateEcefX),
                new(new("dis.LiveExercise", "Subscribe", "EntityStatePdu", "$.entityLocation.y"), BuiltInSemanticTags.CoordinateEcefY),
                new(new("dis.LiveExercise", "Subscribe", "EntityStatePdu", "$.entityLocation.z"), BuiltInSemanticTags.CoordinateEcefZ),
                new(new("dis.LiveExercise", "Subscribe", "FirePdu", "$.locationInWorldCoords.x"), BuiltInSemanticTags.CoordinateEcefX),
                new(new("dis.LiveExercise", "Subscribe", "FirePdu", "$.locationInWorldCoords.y"), BuiltInSemanticTags.CoordinateEcefY),
                new(new("dis.LiveExercise", "Subscribe", "FirePdu", "$.locationInWorldCoords.z"), BuiltInSemanticTags.CoordinateEcefZ),
                new(AnnotationKey.ForSingleType("harbor.HarborService", "WatchCrane", "$.position.lat"), BuiltInSemanticTags.CoordinateLatitude),
                new(AnnotationKey.ForSingleType("harbor.HarborService", "WatchCrane", "$.position.lon"), BuiltInSemanticTags.CoordinateLongitude),
            ]);
            await layer.SaveAsync(TestContext.Current.CancellationToken);
        }

        var written = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(written);
        var root = doc.RootElement;

        Assert.Equal(SchemaHintsFile.CurrentVersion, root.GetProperty("version").GetInt32());

        var schemas = root.GetProperty("schemas");
        Assert.Equal(2, schemas.GetArrayLength());

        // Schemas are written in sorted (service, method) order — dis < harbor.
        var dis = schemas[0];
        Assert.Equal("dis.LiveExercise", dis.GetProperty("service").GetString());
        Assert.Equal("Subscribe", dis.GetProperty("method").GetString());
        var disTypes = dis.GetProperty("types");
        Assert.Equal(BuiltInSemanticTags.CoordinateEcefX.Kind,
            disTypes.GetProperty("EntityStatePdu").GetProperty("$.entityLocation.x").GetString());
        Assert.Equal(BuiltInSemanticTags.CoordinateEcefY.Kind,
            disTypes.GetProperty("EntityStatePdu").GetProperty("$.entityLocation.y").GetString());
        Assert.Equal(BuiltInSemanticTags.CoordinateEcefZ.Kind,
            disTypes.GetProperty("EntityStatePdu").GetProperty("$.entityLocation.z").GetString());
        Assert.Equal(BuiltInSemanticTags.CoordinateEcefX.Kind,
            disTypes.GetProperty("FirePdu").GetProperty("$.locationInWorldCoords.x").GetString());

        var harbor = schemas[1];
        Assert.Equal("harbor.HarborService", harbor.GetProperty("service").GetString());
        Assert.Equal("WatchCrane", harbor.GetProperty("method").GetString());
        var harborTypes = harbor.GetProperty("types");
        // The "*" key is the literal wildcard for single-type methods.
        Assert.Equal("coordinate.latitude",
            harborTypes.GetProperty("*").GetProperty("$.position.lat").GetString());
        Assert.Equal("coordinate.longitude",
            harborTypes.GetProperty("*").GetProperty("$.position.lon").GetString());
    }

    [Fact]
    public async Task LoadAsync_Parses_The_Adr_Documented_Shape()
    {
        // Hand-written file in the exact shape the ADR shows.
        // Keep this synchronised with frame-semantics-framework.md's
        // "Persistence" section.
        var path = NewTempPath();
        var adrShape = """
            {
              "version": 1,
              "schemas": [
                {
                  "service": "dis.LiveExercise",
                  "method": "Subscribe",
                  "discriminator": {
                    "wirePath": "byte[1]",
                    "registry": "dis.PduType"
                  },
                  "types": {
                    "EntityStatePdu": {
                      "$.entityLocation.x": "coordinate.ecef.x",
                      "$.entityLocation.y": "coordinate.ecef.y",
                      "$.entityLocation.z": "coordinate.ecef.z"
                    },
                    "FirePdu": {
                      "$.locationInWorldCoords.x": "coordinate.ecef.x",
                      "$.locationInWorldCoords.y": "coordinate.ecef.y",
                      "$.locationInWorldCoords.z": "coordinate.ecef.z"
                    }
                  }
                },
                {
                  "service": "harbor.HarborService",
                  "method": "WatchCrane",
                  "types": {
                    "*": {
                      "$.position.lat": "coordinate.latitude",
                      "$.position.lon": "coordinate.longitude"
                    }
                  }
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(path, adrShape, TestContext.Current.CancellationToken);

        using var layer = new JsonFileAnnotationLayer(path);
        await layer.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(8, layer.Count);
        Assert.Equal(BuiltInSemanticTags.CoordinateEcefX,
            layer.Get(new("dis.LiveExercise", "Subscribe", "EntityStatePdu", "$.entityLocation.x")));
        Assert.Equal(BuiltInSemanticTags.CoordinateEcefZ,
            layer.Get(new("dis.LiveExercise", "Subscribe", "FirePdu", "$.locationInWorldCoords.z")));
        Assert.Equal(BuiltInSemanticTags.CoordinateLatitude,
            layer.Get(AnnotationKey.ForSingleType("harbor.HarborService", "WatchCrane", "$.position.lat")));
        Assert.Equal(BuiltInSemanticTags.CoordinateLongitude,
            layer.Get(AnnotationKey.ForSingleType("harbor.HarborService", "WatchCrane", "$.position.lon")));
    }

    [Fact]
    public async Task SaveAsync_Creates_Parent_Directory_On_Demand()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bowire-schema-hints-dir-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(dir, "nested", "schema-hints.json");
            using var layer = new JsonFileAnnotationLayer(path);
            layer.Replace(
            [
                new(AnnotationKey.ForSingleType("s", "m", "$.x"), BuiltInSemanticTags.CoordinateLatitude),
            ]);

            await layer.SaveAsync(TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Concurrent_Writes_From_Two_Layer_Instances_Do_Not_Corrupt_File()
    {
        // Two LayeredAnnotationStore-equivalent instances pointed at
        // the same path. The atomic-rename in SaveAsync guarantees the
        // file on disk is always a valid, parseable JSON document —
        // never a half-written intermediate. Last writer wins the
        // content; interleaved writes don't tear.
        var path = NewTempPath();

        using var layerA = new JsonFileAnnotationLayer(path);
        layerA.Replace(
        [
            new(new("a", "m", "*", "$.x"), BuiltInSemanticTags.CoordinateLatitude),
        ]);

        using var layerB = new JsonFileAnnotationLayer(path);
        layerB.Replace(
        [
            new(new("b", "m", "*", "$.x"), BuiltInSemanticTags.CoordinateLongitude),
        ]);

        // Hammer the file with N parallel SaveAsync calls from each
        // layer. With the temp-file + atomic-rename pattern, every
        // intermediate state on disk is a complete, well-formed file.
        var tasks = new List<Task>();
        for (var i = 0; i < 16; i++)
        {
            tasks.Add(layerA.SaveAsync(TestContext.Current.CancellationToken));
            tasks.Add(layerB.SaveAsync(TestContext.Current.CancellationToken));
        }
        await Task.WhenAll(tasks);

        // Whatever survived as the final write, it MUST be parseable
        // and reload to one of the two starting states.
        using var finalReader = new JsonFileAnnotationLayer(path);
        await finalReader.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, finalReader.Count);

        var laTag = finalReader.Get(new("a", "m", "*", "$.x"));
        var lbTag = finalReader.Get(new("b", "m", "*", "$.x"));
        Assert.True(
            (laTag is not null && lbTag is null) || (laTag is null && lbTag is not null),
            "exactly one of the two writers should have won the final state");
    }
}
