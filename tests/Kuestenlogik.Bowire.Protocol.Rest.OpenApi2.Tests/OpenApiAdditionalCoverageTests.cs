// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Rest.OpenApi2;

namespace Kuestenlogik.Bowire.Protocol.Rest.OpenApi2.Tests;

/// <summary>
/// Last-mile coverage for branches that the symmetric OpenApi3 suite
/// doesn't reach: schema-less parameters, recording-builder failure
/// edges, and the unknown-extension format fallback.
/// </summary>
public sealed class OpenApiAdditionalCoverageTests
{
    [Fact]
    public async Task BuildServices_Parameter_Without_Schema_Becomes_String_Field()
    {
        // SchemaToField's null-schema branch is reached when a parameter
        // declares no schema at all — the field falls back to type=string.
        // Microsoft.OpenApi 2.x doesn't model parameter content-types, so
        // this happens whenever the parameter's `schema` key is absent.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/x": {
                  "get": {
                    "operationId": "getX",
                    "parameters": [
                      { "name": "raw", "in": "query", "description": "free-form" }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var method = services.SelectMany(s => s.Methods).Single();

        var rawField = method.InputType.Fields.Single(f => f.Name == "raw");
        Assert.Equal("string", rawField.Type);
        Assert.Equal("query", rawField.Source);
        Assert.False(rawField.Required);
        Assert.Equal("free-form", rawField.Description);
    }

    [Fact]
    public async Task LoadAsync_File_With_Unknown_Extension_Still_Parses_When_Body_Is_Valid_Json()
    {
        // InferFormat's "neither json nor yaml" branch returns null, leaving
        // the reader to sniff. A `.txt` file with JSON body still loads.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "TxtExt", "version": "1" },
              "paths": {
                "/x": {
                  "get": {
                    "operationId": "getX",
                    "tags": ["X"],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var path = Path.Combine(Path.GetTempPath(), "openapi2-txt-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, doc, TestContext.Current.CancellationToken);

        try
        {
            var recording = await OpenApiRecordingBuilder.LoadAsync(
                path, TestContext.Current.CancellationToken);

            var step = Assert.Single(recording.Steps);
            Assert.Equal("getX", step.Method);
            Assert.Equal("X", step.Service);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_Yml_Extension_Goes_Through_Yaml_Reader()
    {
        // The .yml branch in InferFormat mirrors .yaml — both route to the
        // YAML reader.
        const string yaml = """
            openapi: 3.0.0
            info:
              title: YML
              version: '1'
            paths:
              /ping:
                get:
                  operationId: ping
                  tags: [Ping]
                  responses:
                    '200':
                      description: OK
            """;
        var path = Path.Combine(Path.GetTempPath(), "openapi2-yml-" + Guid.NewGuid().ToString("N") + ".yml");
        await File.WriteAllTextAsync(path, yaml, TestContext.Current.CancellationToken);

        try
        {
            var recording = await OpenApiRecordingBuilder.LoadAsync(
                path, TestContext.Current.CancellationToken);

            var step = Assert.Single(recording.Steps);
            Assert.Equal("ping", step.Method);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_Malformed_Json_Throws_InvalidDataException()
    {
        var path = Path.Combine(Path.GetTempPath(), "openapi2-bad-" + Guid.NewGuid().ToString("N") + ".json");
        // Not valid OpenAPI — Microsoft.OpenApi yields a null document, and
        // LoadAsync raises InvalidDataException.
        await File.WriteAllTextAsync(path, "not even close to JSON ::: {{{",
            TestContext.Current.CancellationToken);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                OpenApiRecordingBuilder.LoadAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
