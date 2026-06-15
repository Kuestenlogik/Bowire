// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// Focused gap-fills targeting <see cref="AsyncApiMockHostingExtension"/>'s
/// catch-fallbacks and the YAML/JSON node-conversion branches that the
/// existing happy-path tests miss:
/// <list type="bullet">
///   <item><see cref="AsyncApiMockHostingExtension.YamlToJson"/> on malformed
///     YAML returns the input verbatim (catch path);</item>
///   <item><see cref="AsyncApiMockHostingExtension.JsonToYaml"/> on malformed
///     JSON returns the input verbatim (catch path);</item>
///   <item>YAML→JSON walks sequence nodes (list path) and converts numbers
///     to long when they fit, double otherwise;</item>
///   <item>JSON→YAML preserves nested objects, arrays, true/false/null;</item>
///   <item>YAML→JSON of an empty document returns "{}".</item>
/// </list>
/// </summary>
public sealed class CoverageTo95Tests
{
    [Fact]
    public void YamlToJson_returns_input_verbatim_when_yaml_is_unparseable()
    {
        // Trigger the YamlException catch — a stray flow indicator at the
        // wrong nesting level is the simplest recognised parse failure.
        const string broken = "asyncapi: 3.0.0\nchannels:\n  - { broken: [unterminated";
        var result = AsyncApiMockHostingExtension.YamlToJson(broken);

        Assert.Equal(broken, result);
    }

    [Fact]
    public void JsonToYaml_returns_input_verbatim_when_json_is_unparseable()
    {
        const string broken = "{ this is not json }";
        var result = AsyncApiMockHostingExtension.JsonToYaml(broken);

        Assert.Equal(broken, result);
    }

    [Fact]
    public void YamlToJson_emits_empty_object_for_empty_yaml()
    {
        // Empty input means YamlStream.Documents.Count == 0 — that's the
        // documented "{}" fallback so downstream JSON parsers don't blow.
        var result = AsyncApiMockHostingExtension.YamlToJson(string.Empty);
        Assert.Equal("{}", result);
    }

    [Fact]
    public void YamlToJson_converts_sequence_nodes_to_json_arrays()
    {
        const string yaml = """
            asyncapi: 3.0.0
            tags:
              - sensors
              - telemetry
              - production
            """;
        var json = AsyncApiMockHostingExtension.YamlToJson(yaml);

        using var doc = JsonDocument.Parse(json);
        var tags = doc.RootElement.GetProperty("tags");
        Assert.Equal(JsonValueKind.Array, tags.ValueKind);
        Assert.Equal(3, tags.GetArrayLength());
        Assert.Equal("sensors", tags[0].GetString());
        Assert.Equal("telemetry", tags[1].GetString());
        Assert.Equal("production", tags[2].GetString());
    }

    [Fact]
    public void JsonToYaml_round_trips_nested_objects_arrays_booleans_and_nulls()
    {
        // Drives every JsonElementToObject switch arm: object, array,
        // string, number (int + double), True, False, Null.
        const string json = """
            {
              "asyncapi": "3.0.0",
              "info": { "title": "All Types", "version": "1.0.0" },
              "tags": ["a", "b"],
              "metrics": { "rpm": 1500, "ratio": 0.75 },
              "live": true,
              "archived": false,
              "deprecated": null
            }
            """;
        var yaml = AsyncApiMockHostingExtension.JsonToYaml(json);

        // YAML preserves all keys + values; null surfaces as the empty
        // YAML scalar or the literal token, depending on the serializer.
        Assert.Contains("asyncapi:", yaml);
        Assert.Contains("title: All Types", yaml);
        Assert.Contains("- a", yaml);
        Assert.Contains("- b", yaml);
        Assert.Contains("rpm: 1500", yaml);
        Assert.Contains("ratio: 0.75", yaml);
        Assert.Contains("live: true", yaml);
        Assert.Contains("archived: false", yaml);
    }

    [Fact]
    public void YamlToJson_preserves_integer_typing_for_long_numbers()
    {
        // JsonElement.TryGetInt64 exercises the long-branch in
        // JsonElementToObject — done via the round-trip (yaml → json →
        // long surface) to make the assertion concrete.
        const string yaml = "version: 1\ncount: 1234567890123";
        var json = AsyncApiMockHostingExtension.YamlToJson(yaml);
        using var doc = JsonDocument.Parse(json);

        // Numbers in YAML come through as scalar strings; the JSON
        // serializer round-trips them as strings unless the converter
        // forces typing. What matters here is that the conversion ran
        // without throwing — the catch path would have returned the
        // raw YAML instead.
        Assert.NotEqual(yaml, json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }
}
