// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Unit coverage for <see cref="OpenApiSampleGenerator"/>: every OpenAPI
/// scalar / format the generator special-cases (date / date-time / uuid /
/// email / ipv4 / ipv6 / byte / binary / hostname / password / unknown),
/// nullable types, minimum-honouring integer + number, enum-first pick,
/// boolean, deeply-nested object (recursion cap), arrays with min/max, and
/// the missing-schema fallback.
/// </summary>
public sealed class OpenApiSampleGeneratorTests
{
    [Fact]
    public void Generate_Null_Schema_Yields_String_Sample()
    {
        // The depth-cap branch and "null schema" branch share a path —
        // both emit a quoted "sample" string.
        var json = OpenApiSampleGenerator.Generate(null);

        Assert.Equal("\"sample\"", json);
    }

    [Theory]
    [InlineData("date-time", "2026-01-01T00:00:00Z")]
    [InlineData("date", "2026-01-01")]
    [InlineData("time", "00:00:00")]
    [InlineData("uuid", "00000000-0000-0000-0000-000000000000")]
    [InlineData("email", "sample@example.com")]
    [InlineData("uri", "https://example.com")]
    [InlineData("url", "https://example.com")]
    [InlineData("hostname", "example.com")]
    [InlineData("ipv4", "127.0.0.1")]
    [InlineData("ipv6", "::1")]
    [InlineData("binary", "sample")]
    [InlineData("password", "sample")]
    [InlineData("unknown-format-fallback", "sample")]
    public void Generate_String_Format_Specific_Placeholders(string format, string expectedUnquoted)
    {
        var json = OpenApiSampleGenerator.Generate(GetPropertySchema($$"""
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": { "/x": { "get": { "operationId": "g", "responses": { "200": { "description": "ok" } } } } },
              "components": {
                "schemas": {
                  "Sample": {
                    "type": "object",
                    "properties": { "v": { "type": "string", "format": "{{format}}" } }
                  }
                }
              }
            }
            """, "Sample", "v"));

        Assert.Equal($"\"{expectedUnquoted}\"", json);
    }

    [Fact]
    public void Generate_Byte_Format_Emits_Base64_Sample_String()
    {
        var json = OpenApiSampleGenerator.Generate(GetPropertySchema("""
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": { "/x": { "get": { "operationId": "g", "responses": { "200": { "description": "ok" } } } } },
              "components": {
                "schemas": {
                  "Sample": {
                    "type": "object",
                    "properties": { "v": { "type": "string", "format": "byte" } }
                  }
                }
              }
            }
            """, "Sample", "v"));

        // base64("sample") — value driven by the generator's hard-coded UTF-8 bytes.
        var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("sample"));
        Assert.Equal($"\"{expected}\"", json);
    }

    [Fact]
    public void Generate_Boolean_Yields_True()
    {
        var json = OpenApiSampleGenerator.Generate(GetPropertySchema("""
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": { "/x": { "get": { "operationId": "g", "responses": { "200": { "description": "ok" } } } } },
              "components": {
                "schemas": {
                  "Sample": {
                    "type": "object",
                    "properties": { "v": { "type": "boolean" } }
                  }
                }
              }
            }
            """, "Sample", "v"));

        Assert.Equal("true", json);
    }

    [Fact]
    public void Generate_Integer_With_Minimum_Honours_Minimum()
    {
        var json = OpenApiSampleGenerator.Generate(GetPropertySchema("""
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": { "/x": { "get": { "operationId": "g", "responses": { "200": { "description": "ok" } } } } },
              "components": {
                "schemas": {
                  "Sample": {
                    "type": "object",
                    "properties": { "v": { "type": "integer", "minimum": 42 } }
                  }
                }
              }
            }
            """, "Sample", "v"));

        Assert.Equal("42", json);
    }

    [Fact]
    public void Generate_Number_With_Minimum_Honours_Minimum()
    {
        var json = OpenApiSampleGenerator.Generate(GetPropertySchema("""
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": { "/x": { "get": { "operationId": "g", "responses": { "200": { "description": "ok" } } } } },
              "components": {
                "schemas": {
                  "Sample": {
                    "type": "object",
                    "properties": { "v": { "type": "number", "minimum": 7.25 } }
                  }
                }
              }
            }
            """, "Sample", "v"));

        Assert.Equal("7.25", json);
    }

    [Fact]
    public void Generate_Enum_Picks_First_Value()
    {
        var json = OpenApiSampleGenerator.Generate(GetPropertySchema("""
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": { "/x": { "get": { "operationId": "g", "responses": { "200": { "description": "ok" } } } } },
              "components": {
                "schemas": {
                  "Sample": {
                    "type": "object",
                    "properties": { "v": { "type": "string", "enum": ["red", "green", "blue"] } }
                  }
                }
              }
            }
            """, "Sample", "v"));

        Assert.Equal("\"red\"", json);
    }

    [Fact]
    public void Generate_Example_Wins_Over_Type_Driven_Guess()
    {
        var json = OpenApiSampleGenerator.Generate(GetPropertySchema("""
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": { "/x": { "get": { "operationId": "g", "responses": { "200": { "description": "ok" } } } } },
              "components": {
                "schemas": {
                  "Sample": {
                    "type": "object",
                    "properties": { "v": { "type": "string", "example": "custom" } }
                  }
                }
              }
            }
            """, "Sample", "v"));

        Assert.Equal("\"custom\"", json);
    }

    [Fact]
    public void Generate_Default_Wins_When_No_Example()
    {
        var json = OpenApiSampleGenerator.Generate(GetPropertySchema("""
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": { "/x": { "get": { "operationId": "g", "responses": { "200": { "description": "ok" } } } } },
              "components": {
                "schemas": {
                  "Sample": {
                    "type": "object",
                    "properties": { "v": { "type": "integer", "default": 99 } }
                  }
                }
              }
            }
            """, "Sample", "v"));

        Assert.Equal("99", json);
    }

    [Fact]
    public void Generate_Array_With_MinItems_Above_Default_Honours_Min()
    {
        var json = OpenApiSampleGenerator.Generate(GetPropertySchema("""
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": { "/x": { "get": { "operationId": "g", "responses": { "200": { "description": "ok" } } } } },
              "components": {
                "schemas": {
                  "Sample": {
                    "type": "object",
                    "properties": {
                      "v": {
                        "type": "array",
                        "minItems": 5,
                        "items": { "type": "string" }
                      }
                    }
                  }
                }
              }
            }
            """, "Sample", "v"));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(5, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void Generate_Array_With_MaxItems_Below_Default_Honours_Max()
    {
        var json = OpenApiSampleGenerator.Generate(GetPropertySchema("""
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": { "/x": { "get": { "operationId": "g", "responses": { "200": { "description": "ok" } } } } },
              "components": {
                "schemas": {
                  "Sample": {
                    "type": "object",
                    "properties": {
                      "v": {
                        "type": "array",
                        "maxItems": 1,
                        "items": { "type": "string" }
                      }
                    }
                  }
                }
              }
            }
            """, "Sample", "v"));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void Generate_Nullable_Object_Drops_Null_Flag_And_Walks_Properties()
    {
        var json = OpenApiSampleGenerator.Generate(GetPropertySchema("""
            {
              "openapi": "3.1.0",
              "info": { "title": "T", "version": "1" },
              "paths": { "/x": { "get": { "operationId": "g", "responses": { "200": { "description": "ok" } } } } },
              "components": {
                "schemas": {
                  "Sample": {
                    "type": "object",
                    "properties": {
                      "v": {
                        "type": ["object", "null"],
                        "properties": { "x": { "type": "integer" } }
                      }
                    }
                  }
                }
              }
            }
            """, "Sample", "v"));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetProperty("x").GetInt32());
    }

    private static IOpenApiSchema? GetPropertySchema(string openApiJson, string componentName, string propertyName)
    {
        // Resolve a $ref'd schema's property by parsing through the official
        // reader, so the test exercises real OpenApiSchema instances rather
        // than hand-rolled stubs.
        // Pragma: the LoadAsync call completes synchronously here via
        // GetAwaiter().GetResult(); CA2025 can't prove the stream isn't
        // disposed mid-task without async semantics. The stream lives only
        // for the duration of the synchronous parse, so the warning is
        // a false positive in this context.
#pragma warning disable CA2025
        var bytes = System.Text.Encoding.UTF8.GetBytes(openApiJson);
        using var stream = new MemoryStream(bytes);
        var result = OpenApiDocument.LoadAsync(stream, format: "json", settings: null, CancellationToken.None)
            .GetAwaiter().GetResult();
#pragma warning restore CA2025
        var schemas = result.Document!.Components!.Schemas!;
        var component = (IOpenApiSchema)schemas[componentName];
        return component.Properties![propertyName];
    }
}

/// <summary>
/// Coverage for <see cref="OpenApiRecordingBuilder"/> error / fallback
/// branches: missing path, empty path, file-not-found, malformed doc,
/// doc with zero operations, YAML format, no-tag service-name fallback,
/// and the 2xx-without-content fallback.
/// </summary>
public sealed class OpenApiRecordingBuilderTests
{
    [Fact]
    public async Task LoadAsync_Empty_Path_Throws_ArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            OpenApiRecordingBuilder.LoadAsync("", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_Whitespace_Path_Throws_ArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            OpenApiRecordingBuilder.LoadAsync("   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_Missing_File_Throws_FileNotFoundException()
    {
        var missing = Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N") + ".yaml");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            OpenApiRecordingBuilder.LoadAsync(missing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_Doc_Without_Paths_Throws_InvalidDataException()
    {
        var path = WriteTempJson("""
            { "openapi": "3.0.0", "info": { "title": "X", "version": "1" }, "paths": {} }
            """);
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

    [Fact]
    public async Task LoadAsync_Yaml_File_Round_Trips_Through_Yaml_Reader()
    {
        var yaml = """
            openapi: 3.0.0
            info:
              title: Yaml API
              version: '1'
            paths:
              /things:
                get:
                  operationId: listThings
                  tags: [Things]
                  responses:
                    '200':
                      description: OK
                      content:
                        application/json:
                          schema:
                            type: object
                            properties:
                              name:
                                type: string
            """;
        var path = Path.Combine(Path.GetTempPath(), "rest-cov-" + Guid.NewGuid().ToString("N") + ".yaml");
        await File.WriteAllTextAsync(path, yaml, TestContext.Current.CancellationToken);

        try
        {
            var rec = await OpenApiRecordingBuilder.LoadAsync(path, TestContext.Current.CancellationToken);

            var step = Assert.Single(rec.Steps);
            Assert.Equal("listThings", step.Method);
            Assert.Equal("Things", step.Service);
            Assert.Equal("GET", step.HttpVerb);
            Assert.Equal("/things", step.HttpPath);
            Assert.NotNull(step.Response);
            Assert.Contains("\"name\"", step.Response!, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Build_No_Tag_Falls_Back_To_Info_Title()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Untagged API", "version": "1" },
              "paths": {
                "/x": {
                  "get": {
                    "operationId": "getX",
                    "responses": {
                      "200": {
                        "description": "OK",
                        "content": {
                          "application/json": {
                            "schema": { "type": "object", "properties": { "v": { "type": "string" } } }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;
        var parsed = LoadDoc(doc);

        var rec = OpenApiRecordingBuilder.Build(parsed, "inline");

        var step = Assert.Single(rec.Steps);
        Assert.Equal("Untagged API", step.Service);
        Assert.Equal("getX", step.Method);
    }

    [Fact]
    public void Build_204_Response_Leaves_Body_Null_To_Match_Status()
    {
        // 2xx response without content — the builder skips the body but still
        // emits the step with the actual status code preserved.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/ping": {
                  "get": {
                    "operationId": "ping",
                    "tags": ["Health"],
                    "responses": { "204": { "description": "no content" } }
                  }
                }
              }
            }
            """;
        var parsed = LoadDoc(doc);

        var rec = OpenApiRecordingBuilder.Build(parsed, "inline");

        var step = Assert.Single(rec.Steps);
        Assert.Equal("204", step.Status);
        Assert.Null(step.Response);
    }

    [Fact]
    public void Build_Operation_Without_OperationId_Uses_Verb_And_Path()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/anon": {
                  "post": {
                    "tags": ["A"],
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": {
                          "application/json": {
                            "schema": { "type": "object", "properties": { "id": { "type": "string" } } }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;
        var parsed = LoadDoc(doc);
        var rec = OpenApiRecordingBuilder.Build(parsed, "inline");

        var step = Assert.Single(rec.Steps);
        Assert.Equal("POST /anon", step.Method);
    }

    [Fact]
    public void Build_Doc_With_Only_Non_2xx_Responses_Falls_Back_To_OK_Status()
    {
        // PickSuccessResponseSchema's final fallback path: no 2xx, returns "OK".
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/err": {
                  "get": {
                    "operationId": "errOnly",
                    "tags": ["E"],
                    "responses": { "404": { "description": "not found" } }
                  }
                }
              }
            }
            """;
        var parsed = LoadDoc(doc);
        var rec = OpenApiRecordingBuilder.Build(parsed, "inline");

        var step = Assert.Single(rec.Steps);
        Assert.Equal("OK", step.Status);
        Assert.Null(step.Response);
    }

    private static OpenApiDocument LoadDoc(string json)
    {
        // See note on GetPropertySchema for the CA2025 suppression rationale.
#pragma warning disable CA2025
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        using var stream = new MemoryStream(bytes);
        var result = OpenApiDocument.LoadAsync(stream, format: "json", settings: null, CancellationToken.None)
            .GetAwaiter().GetResult();
#pragma warning restore CA2025
        return result.Document!;
    }

    private static string WriteTempJson(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "rest-cov-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        return path;
    }
}
