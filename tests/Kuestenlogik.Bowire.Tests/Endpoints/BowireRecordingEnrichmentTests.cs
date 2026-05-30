// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Tests.Mocking;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// Coverage for <see cref="BowireRecordingEndpoints"/>'s automatic
/// source-schema enrichment on the PUT path. Verifies both wire
/// shapes (single bare recording, store wrapper), the don't-overwrite
/// rule, and the silent no-op when no cache entry matches.
/// </summary>
[Collection(nameof(SourceSchemaCacheTestGroup))]
public sealed class BowireRecordingEnrichmentTests : IDisposable
{
    public BowireRecordingEnrichmentTests() => SourceSchemaCache.Clear();
    public void Dispose() => SourceSchemaCache.Clear();

    [Fact]
    public void Enrich_stamps_sourceSchema_on_store_wrapper_from_first_step_url()
    {
        var schema = new RecordingSourceSchema("openapi-3.0", "openapi: 3.0.0", "http://api.example.com/openapi.yaml");
        SourceSchemaCache.Set("http://api.example.com", schema);

        const string input = """
            {
              "recordings": [
                {
                  "id": "r1",
                  "name": "User flows",
                  "steps": [
                    { "id": "s1", "serverUrl": "http://api.example.com", "method": "getUser" }
                  ]
                }
              ]
            }
            """;
        var enriched = BowireRecordingEndpoints.TryEnrichWithSourceSchema(input);
        Assert.NotNull(enriched);
        var node = JsonNode.Parse(enriched!);
        var rec = node!["recordings"]![0]!;
        Assert.Equal("openapi-3.0", rec["sourceSchema"]!["format"]!.GetValue<string>());
        Assert.Contains("openapi: 3.0.0", rec["sourceSchema"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Enrich_handles_bare_recording_shape()
    {
        SourceSchemaCache.Set("mqtt://broker:1883",
            new RecordingSourceSchema("asyncapi-3.0", "asyncapi: 3.0.0", "./asyncapi.yaml"));

        const string input = """
            {
              "id": "r1",
              "name": "Sensor",
              "steps": [
                { "id": "s1", "serverUrl": "mqtt://broker:1883", "method": "sensors/temperature" }
              ]
            }
            """;
        var enriched = BowireRecordingEndpoints.TryEnrichWithSourceSchema(input);
        Assert.NotNull(enriched);
        var node = JsonNode.Parse(enriched!);
        Assert.Equal("asyncapi-3.0", node!["sourceSchema"]!["format"]!.GetValue<string>());
    }

    [Fact]
    public void Enrich_does_not_overwrite_existing_sourceSchema()
    {
        // Workbench wrote a SourceSchema explicitly — discovery-cache
        // entry must NOT clobber it.
        SourceSchemaCache.Set("http://api.example.com",
            new RecordingSourceSchema("openapi-3.0", "from-cache", null));

        const string input = """
            {
              "recordings": [
                {
                  "id": "r1",
                  "name": "n",
                  "sourceSchema": { "format": "openapi-3.0", "content": "from-workbench" },
                  "steps": [
                    { "id": "s1", "serverUrl": "http://api.example.com" }
                  ]
                }
              ]
            }
            """;
        var enriched = BowireRecordingEndpoints.TryEnrichWithSourceSchema(input);
        // Either returns the input unchanged or the parsed/re-emitted
        // equivalent — content of sourceSchema must still be the
        // workbench-supplied value.
        var node = JsonNode.Parse(enriched ?? input);
        var content = node!["recordings"]![0]!["sourceSchema"]!["content"]!.GetValue<string>();
        Assert.Equal("from-workbench", content);
    }

    [Fact]
    public void Enrich_returns_input_unchanged_when_no_cache_match()
    {
        // Cache has an entry but for a different URL — no stamp.
        SourceSchemaCache.Set("http://something/else",
            new RecordingSourceSchema("openapi-3.0", "x", null));
        const string input = """
            {
              "recordings": [
                { "id": "r1", "name": "n", "steps": [
                  { "id": "s1", "serverUrl": "http://api.example.com" }
                ]}
              ]
            }
            """;
        var enriched = BowireRecordingEndpoints.TryEnrichWithSourceSchema(input);
        // No change → caller passes raw input through; we return the
        // verbatim string for that case.
        Assert.Equal(input, enriched);
    }

    [Fact]
    public void Enrich_walks_steps_for_first_non_empty_serverUrl()
    {
        SourceSchemaCache.Set("http://target",
            new RecordingSourceSchema("openapi-3.0", "ok", null));

        const string input = """
            {
              "recordings": [
                {
                  "id": "r1",
                  "name": "n",
                  "steps": [
                    { "id": "s1" },
                    { "id": "s2", "serverUrl": "" },
                    { "id": "s3", "serverUrl": "http://target" }
                  ]
                }
              ]
            }
            """;
        var enriched = BowireRecordingEndpoints.TryEnrichWithSourceSchema(input);
        Assert.NotNull(enriched);
        var node = JsonNode.Parse(enriched!);
        Assert.Equal("ok", node!["recordings"]![0]!["sourceSchema"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Enrich_returns_null_for_malformed_json()
    {
        var enriched = BowireRecordingEndpoints.TryEnrichWithSourceSchema("{ not json ");
        Assert.Null(enriched);
    }

    [Fact]
    public void Enrich_silent_when_recording_has_no_steps()
    {
        SourceSchemaCache.Set("http://h", new RecordingSourceSchema("openapi-3.0", "x", null));
        const string input = """
            {"recordings":[{"id":"r1","name":"n","steps":[]}]}
            """;
        var enriched = BowireRecordingEndpoints.TryEnrichWithSourceSchema(input);
        Assert.Equal(input, enriched);
    }
}
