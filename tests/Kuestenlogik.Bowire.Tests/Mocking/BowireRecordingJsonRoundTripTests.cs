// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Tests.Mocking;

/// <summary>
/// Round-trip tests for the Phase-5 extended <see cref="BowireRecording"/>
/// schema. Two flavours:
/// <list type="bullet">
///   <item>Synthetic v1 file (no <c>schemaSnapshot</c>, no per-step
///   <c>discriminator</c> / <c>interpretations</c>) must load cleanly —
///   the backwards-compat guarantee the ADR pins.</item>
///   <item>v2 file (full Phase-5 surface) must round-trip byte-for-byte
///   structurally — save, load, re-serialize, compare.</item>
/// </list>
/// </summary>
public sealed class BowireRecordingJsonRoundTripTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // -----------------------------------------------------------------
    // v1 backwards-compat — hand-crafted JSON, no Phase-5 fields.
    // -----------------------------------------------------------------

    [Fact]
    public void V1_Recording_Without_SchemaSnapshot_Loads_Cleanly()
    {
        // Hand-crafted JSON file shape exactly as pre-Phase-5 Bowire
        // wrote it: no schemaSnapshot, no discriminator, no
        // interpretations. Tolerated by the Phase-5 loader because every
        // new field is optional / nullable.
        const string v1Json = """
        {
          "id": "rec_old",
          "name": "Pre-Phase-5",
          "description": "",
          "createdAt": 1234567890,
          "recordingFormatVersion": 2,
          "steps": [
            {
              "id": "step_a",
              "capturedAt": 1234567891,
              "protocol": "grpc",
              "service": "Greeter",
              "method": "SayHello",
              "methodType": "Unary",
              "body": "{}",
              "status": "OK",
              "durationMs": 12,
              "response": "{\"message\":\"hi\"}"
            }
          ]
        }
        """;

        var rec = JsonSerializer.Deserialize<BowireRecording>(v1Json, JsonOpts);

        Assert.NotNull(rec);
        Assert.Equal("rec_old", rec!.Id);
        Assert.Equal(2, rec.RecordingFormatVersion);
        Assert.Null(rec.SchemaSnapshot);
        Assert.Single(rec.Steps);
        Assert.Null(rec.Steps[0].Discriminator);
        Assert.Null(rec.Steps[0].Interpretations);
    }

    [Fact]
    public void V1_Recording_Reserialises_Without_Phase5_Fields_When_Defaults_Stay_Null()
    {
        // Round-trip a v1-shape record through the model and back to
        // JSON; the Phase-5 fields stay null on save, so the new shape
        // is strictly additive — a v2 host can still emit v1-readable
        // output if it never touches the new properties.
        var rec = new BowireRecording
        {
            Id = "rec_old",
            Name = "Pre-Phase-5",
            CreatedAt = 1234567890,
            RecordingFormatVersion = 2,
        };
        rec.Steps.Add(new BowireRecordingStep
        {
            Id = "step_a",
            CapturedAt = 1234567891,
            Protocol = "grpc",
            Service = "Greeter",
            Method = "SayHello",
            MethodType = "Unary",
            Body = "{}",
            Status = "OK",
            DurationMs = 12,
            Response = """{"message":"hi"}""",
        });

        var json = JsonSerializer.Serialize(rec, JsonOpts);

        // Save with WhenWritingNull → the new Phase-5 fields don't
        // appear in the on-disk shape unless they were set.
        Assert.DoesNotContain("schemaSnapshot", json, StringComparison.Ordinal);
        Assert.DoesNotContain("discriminator", json, StringComparison.Ordinal);
        Assert.DoesNotContain("interpretations", json, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // v2 round-trip — the full Phase-5 surface.
    // -----------------------------------------------------------------

    [Fact]
    public void V2_Recording_With_Interpretations_And_Snapshot_RoundTrips()
    {
        var rec = new BowireRecording
        {
            Id = "rec_new",
            Name = "Phase-5 capture",
            CreatedAt = 9999,
            RecordingFormatVersion = 2,
            SchemaSnapshot = new BowireRecordingSchemaSnapshot
            {
                Annotations =
                {
                    new BowireRecordingSchemaAnnotation
                    {
                        Service = "harbor.HarborService",
                        Method = "WatchCrane",
                        MessageType = "*",
                        JsonPath = "$.position.lat",
                        Semantic = "coordinate.latitude",
                    },
                    new BowireRecordingSchemaAnnotation
                    {
                        Service = "harbor.HarborService",
                        Method = "WatchCrane",
                        MessageType = "*",
                        JsonPath = "$.position.lon",
                        Semantic = "coordinate.longitude",
                    },
                },
            },
        };

        var payload = JsonSerializer.SerializeToElement(new { lat = 53.5478, lon = 9.9925 });
        rec.Steps.Add(new BowireRecordingStep
        {
            Id = "step_a",
            CapturedAt = 10000,
            Protocol = "grpc",
            Service = "harbor.HarborService",
            Method = "WatchCrane",
            MethodType = "ServerStreaming",
            Body = "{}",
            Status = "OK",
            DurationMs = 100,
            Discriminator = "*",
            Interpretations = new List<RecordedInterpretation>
            {
                new(Kind: "coordinate.wgs84",
                    Path: "$.position",
                    Payload: payload),
            },
        });

        var json = JsonSerializer.Serialize(rec, JsonOpts);
        var loaded = JsonSerializer.Deserialize<BowireRecording>(json, JsonOpts);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.RecordingFormatVersion);
        Assert.NotNull(loaded.SchemaSnapshot);
        Assert.Equal(2, loaded.SchemaSnapshot!.Annotations.Count);
        Assert.Equal("coordinate.latitude", loaded.SchemaSnapshot.Annotations[0].Semantic);

        Assert.Single(loaded.Steps);
        Assert.Equal("*", loaded.Steps[0].Discriminator);
        Assert.NotNull(loaded.Steps[0].Interpretations);
        Assert.Single(loaded.Steps[0].Interpretations!);

        var interp = loaded.Steps[0].Interpretations![0];
        Assert.Equal("coordinate.wgs84", interp.Kind);
        Assert.Equal("$.position", interp.Path);
        Assert.Equal(53.5478, interp.Payload.GetProperty("lat").GetDouble());
        Assert.Equal(9.9925, interp.Payload.GetProperty("lon").GetDouble());
    }

    [Fact]
    public void V2_Wire_Format_Uses_Camel_Case_Property_Names()
    {
        // The JsonPropertyName attributes pin the on-disk shape. The
        // workbench JS reads these names verbatim, so a casing drift
        // would break the recorder ↔ workbench contract silently.
        var rec = new BowireRecording
        {
            Id = "rec",
            RecordingFormatVersion = 2,
            SchemaSnapshot = new BowireRecordingSchemaSnapshot
            {
                Annotations = { new BowireRecordingSchemaAnnotation { Service = "s", Method = "m", MessageType = "*", JsonPath = "$.x", Semantic = "k" } },
            },
        };
        rec.Steps.Add(new BowireRecordingStep
        {
            Id = "s",
            Service = "s",
            Method = "m",
            Discriminator = "*",
            Interpretations = new List<RecordedInterpretation>
            {
                new("k", "$", JsonSerializer.SerializeToElement(new { x = 1 })),
            },
        });

        var json = JsonSerializer.Serialize(rec, JsonOpts);

        Assert.Contains("\"schemaSnapshot\"", json, StringComparison.Ordinal);
        Assert.Contains("\"annotations\"", json, StringComparison.Ordinal);
        Assert.Contains("\"messageType\"", json, StringComparison.Ordinal);
        Assert.Contains("\"jsonPath\"", json, StringComparison.Ordinal);
        Assert.Contains("\"semantic\"", json, StringComparison.Ordinal);
        Assert.Contains("\"discriminator\"", json, StringComparison.Ordinal);
        Assert.Contains("\"interpretations\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\"", json, StringComparison.Ordinal);
        Assert.Contains("\"payload\"", json, StringComparison.Ordinal);
    }
}
