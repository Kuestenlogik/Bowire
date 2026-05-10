// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Rest;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Pure-in-process coverage extensions for OpenAPI discovery edge cases:
/// null parameter schema, nested objects, array-of-objects, integer
/// enums, multipart binary fields, vendor +json content type, and the
/// JSON-wins-over-multipart precedence. All driven against in-memory
/// documents — no HTTP fetch.
/// </summary>
public sealed class OpenApiDiscoveryEdgeCasesTests
{
    [Fact]
    public async Task BuildServices_Null_Parameter_Schema_Falls_Back_To_String_Field()
    {
        // OpenAPI 3 lets a parameter declare no schema (purely descriptive).
        // SchemaToField's null-schema branch then has to fabricate a string field.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/x": {
                  "get": {
                    "operationId": "getX",
                    "parameters": [
                      { "name": "anything", "in": "query", "example": "hello" }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);

        var field = services.SelectMany(s => s.Methods).Single().InputType.Fields.Single();
        Assert.Equal("anything", field.Name);
        Assert.Equal("string", field.Type);
        // parameterExample propagates through the null-schema branch
        Assert.Contains("hello", field.Example ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildServices_Body_With_Nested_Object_Property_Builds_Message_Type()
    {
        // Exercises the SchemaToField branch where a property is itself an
        // object with sub-properties — typeName becomes "message" and a
        // nested BowireMessageInfo gets emitted.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/orders": {
                  "post": {
                    "operationId": "createOrder",
                    "requestBody": {
                      "required": true,
                      "content": {
                        "application/json": {
                          "schema": {
                            "type": "object",
                            "required": ["address"],
                            "properties": {
                              "address": {
                                "type": "object",
                                "properties": {
                                  "street": { "type": "string" },
                                  "zip": { "type": "string", "default": "12345" }
                                }
                              }
                            }
                          }
                        }
                      }
                    },
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);

        var method = services.SelectMany(s => s.Methods).Single();
        var addr = method.InputType.Fields.Single(f => f.Name == "address");
        Assert.Equal("message", addr.Type);
        Assert.NotNull(addr.MessageType);
        var zip = addr.MessageType!.Fields.Single(f => f.Name == "zip");
        // Default propagates as the example
        Assert.Contains("12345", zip.Example ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildServices_Array_Of_Objects_Becomes_Repeated_Message()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/x": {
                  "post": {
                    "operationId": "postX",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": {
                            "type": "object",
                            "properties": {
                              "items": {
                                "type": "array",
                                "items": {
                                  "type": "object",
                                  "properties": { "name": { "type": "string" } }
                                }
                              }
                            }
                          }
                        }
                      }
                    },
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var items = services.SelectMany(s => s.Methods).Single().InputType.Fields.Single(f => f.Name == "items");

        Assert.True(items.IsRepeated);
        Assert.Equal("message", items.Type);
        Assert.NotNull(items.MessageType);
    }

    [Fact]
    public async Task BuildServices_Integer_Enum_Carries_Numeric_EnumValues()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/x": {
                  "get": {
                    "operationId": "getX",
                    "parameters": [
                      { "name": "p", "in": "query", "schema": { "type": "integer", "enum": [1, 2, 3] } }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var p = services.SelectMany(s => s.Methods).Single().InputType.Fields.Single();

        Assert.NotNull(p.EnumValues);
        Assert.Equal(3, p.EnumValues!.Count);
        Assert.Contains(p.EnumValues, ev => ev.Name == "1");
        Assert.Contains(p.EnumValues, ev => ev.Name == "3");
    }

    [Fact]
    public async Task BuildServices_Multipart_With_Binary_File_Sets_IsBinary()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/upload": {
                  "post": {
                    "operationId": "upload",
                    "requestBody": {
                      "required": true,
                      "content": {
                        "multipart/form-data": {
                          "schema": {
                            "type": "object",
                            "required": ["file"],
                            "properties": {
                              "file": { "type": "string", "format": "binary" },
                              "note": { "type": "string" }
                            }
                          }
                        }
                      }
                    },
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var fields = OpenApiDiscovery.BuildServices(parsed!.Document)
            .SelectMany(s => s.Methods).Single().InputType.Fields;

        var file = fields.Single(f => f.Name == "file");
        Assert.True(file.IsBinary);
        Assert.Equal("formdata", file.Source);
        var note = fields.Single(f => f.Name == "note");
        Assert.False(note.IsBinary);
        Assert.Equal("formdata", note.Source);
    }

    [Fact]
    public async Task BuildServices_Json_Body_Preferred_Over_Multipart()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/x": {
                  "post": {
                    "operationId": "postX",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": {
                            "type": "object",
                            "properties": { "name": { "type": "string" } }
                          }
                        },
                        "multipart/form-data": {
                          "schema": {
                            "type": "object",
                            "properties": { "file": { "type": "string", "format": "binary" } }
                          }
                        }
                      }
                    },
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var fields = OpenApiDiscovery.BuildServices(parsed!.Document)
            .SelectMany(s => s.Methods).Single().InputType.Fields;

        Assert.Single(fields);
        Assert.Equal("name", fields[0].Name);
        Assert.Equal("body", fields[0].Source);
    }

    [Fact]
    public async Task BuildServices_Vendor_Json_Content_Type_Is_Recognised_For_Body()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/x": {
                  "post": {
                    "operationId": "postX",
                    "requestBody": {
                      "content": {
                        "application/vnd.api+json": {
                          "schema": {
                            "type": "object",
                            "properties": { "title": { "type": "string" } }
                          }
                        }
                      }
                    },
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": {
                          "application/vnd.api+json": {
                            "schema": {
                              "type": "object",
                              "properties": { "id": { "type": "string" } }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var method = OpenApiDiscovery.BuildServices(parsed!.Document)
            .SelectMany(s => s.Methods).Single();

        Assert.Equal("title", method.InputType.Fields[0].Name);
        // 2xx vendor +json response → SchemaToMessage picks it up
        Assert.Single(method.OutputType.Fields);
    }

    [Fact]
    public async Task GetFirstServerUrl_Returns_Url_Verbatim_When_No_Variables()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "servers": [{ "url": "https://api.example.com" }],
              "paths": { "/x": { "get": { "operationId": "g", "responses": { "200": { "description": "ok" } } } } }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);

        Assert.Equal("https://api.example.com", OpenApiDiscovery.GetFirstServerUrl(parsed!.Document));
    }

    [Fact]
    public async Task BuildServices_2xx_Json_Response_Schema_Builds_Output_Message()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/x": {
                  "get": {
                    "operationId": "getX",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": {
                          "application/json": {
                            "schema": {
                              "type": "object",
                              "properties": {
                                "id": { "type": "string" },
                                "count": { "type": "integer" }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var method = OpenApiDiscovery.BuildServices(parsed!.Document)
            .SelectMany(s => s.Methods).Single();

        Assert.Equal(2, method.OutputType.Fields.Count);
        Assert.Contains(method.OutputType.Fields, f => f.Name == "id");
        Assert.Contains(method.OutputType.Fields, f => f.Name == "count");
    }

    [Fact]
    public async Task FetchAndParseAsync_Empty_Url_Returns_Null()
    {
        using var http = new HttpClient();
        var parsed = await OpenApiDiscovery.FetchAndParseAsync("", http, TestContext.Current.CancellationToken);
        Assert.Null(parsed);
    }

    [Fact]
    public async Task FetchAndParseAsync_Malformed_Url_Returns_Null()
    {
        using var http = new HttpClient();
        var parsed = await OpenApiDiscovery.FetchAndParseAsync(
            "not a url", http, TestContext.Current.CancellationToken);
        Assert.Null(parsed);
    }
}

/// <summary>
/// Pure-unit RestInvoker coverage: source-bucket branches, request URL
/// builder failure paths, default body field bucket, no-op when there are
/// no headers, all driven without a real HTTP server.
/// </summary>
public sealed class RestInvokerUnitExtensionTests
{
    [Fact]
    public async Task InvokeAsync_With_Unknown_Source_Field_Falls_Through_To_Body()
    {
        // Source "mystery" hits the default branch (treated as body).
        // POST with body — body field bucket gets exercised; unreachable
        // target so the request fails at SendAsync with a NetworkError.
        var fields = new List<BowireFieldInfo>
        {
            new("weird", 1, "string", "optional", false, false, null, null) { Source = "mystery" }
        };
        var method = new BowireMethodInfo(
            "X", "POST /x", false, false,
            new BowireMessageInfo("In", "In", fields),
            new BowireMessageInfo("Out", "Out", []), "Unary")
        { HttpMethod = "POST", HttpPath = "/x" };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var result = await RestInvoker.InvokeAsync(
            http, "http://127.0.0.1:1", method,
            ["{\"weird\":\"v\"}"], null, TestContext.Current.CancellationToken);

        Assert.Equal("NetworkError", result.Status);
    }

    [Fact]
    public async Task InvokeAsync_Malformed_Base_Url_Returns_Error()
    {
        var method = new BowireMethodInfo(
            "X", "GET /x", false, false,
            new BowireMessageInfo("In", "In", []),
            new BowireMessageInfo("Out", "Out", []), "Unary")
        { HttpMethod = "GET", HttpPath = "/x" };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        // Relative base URL — Uri.TryCreate(absolute) fails and the invoker
        // emits the "Could not build request URL" error path.
        var result = await RestInvoker.InvokeAsync(
            http, "/relative-only", method,
            ["{}"], null, TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Contains("Could not build", result.Metadata["error"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Empty_JsonMessages_Defaults_To_Empty_Object()
    {
        var method = new BowireMethodInfo(
            "X", "GET /x", false, false,
            new BowireMessageInfo("In", "In", []),
            new BowireMessageInfo("Out", "Out", []), "Unary")
        { HttpMethod = "GET", HttpPath = "/x" };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var result = await RestInvoker.InvokeAsync(
            http, "http://127.0.0.1:1", method,
            jsonMessages: [], requestMetadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("NetworkError", result.Status);
    }

    [Fact]
    public async Task InvokeAsync_Whitespace_Body_Treated_As_Empty_Object()
    {
        var method = new BowireMethodInfo(
            "X", "GET /x", false, false,
            new BowireMessageInfo("In", "In", []),
            new BowireMessageInfo("Out", "Out", []), "Unary")
        { HttpMethod = "GET", HttpPath = "/x" };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var result = await RestInvoker.InvokeAsync(
            http, "http://127.0.0.1:1", method,
            jsonMessages: ["   "], requestMetadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("NetworkError", result.Status);
    }

    [Fact]
    public async Task InvokeAsync_NonObject_Json_Body_Yields_Empty_Values_No_Crash()
    {
        // JsonDocument.Parse accepts a top-level array — ExtractTopLevel
        // returns an empty dict because root isn't an object. The request
        // still reaches send.
        var method = new BowireMethodInfo(
            "X", "GET /x", false, false,
            new BowireMessageInfo("In", "In", []),
            new BowireMessageInfo("Out", "Out", []), "Unary")
        { HttpMethod = "GET", HttpPath = "/x" };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var result = await RestInvoker.InvokeAsync(
            http, "http://127.0.0.1:1", method,
            jsonMessages: ["[1,2,3]"], requestMetadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("NetworkError", result.Status);
    }

    [Fact]
    public async Task InvokeAsync_Path_With_SubPath_Constraint_Strips_Constraint()
    {
        // ApplyPathParams' equals-strip branch — {name=path/*} becomes {name}
        // before the lookup happens. The encoded value carries through.
        var method = new BowireMethodInfo(
            "X", "GET /files/{name=path/*}", false, false,
            new BowireMessageInfo("In", "In",
            [
                new("name", 1, "string", "required", false, false, null, null) { Source = "path", Required = true }
            ]),
            new BowireMessageInfo("Out", "Out", []), "Unary")
        { HttpMethod = "GET", HttpPath = "/files/{name=path/*}" };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var result = await RestInvoker.InvokeAsync(
            http, "http://127.0.0.1:1", method,
            jsonMessages: ["{\"name\":\"data\"}"], requestMetadata: null,
            TestContext.Current.CancellationToken);

        // Reached send → NetworkError (not the path-param error).
        Assert.Equal("NetworkError", result.Status);
    }

    [Fact]
    public async Task InvokeAsync_Path_Template_With_Unclosed_Brace_Tolerated()
    {
        // ApplyPathParams' "end < 0" branch — unclosed brace gets appended
        // verbatim and the rest of the template is dropped. URL build
        // still has to succeed because the resulting path stays absolute.
        var method = new BowireMethodInfo(
            "X", "GET /x/{id", false, false,
            new BowireMessageInfo("In", "In", []),
            new BowireMessageInfo("Out", "Out", []), "Unary")
        { HttpMethod = "GET", HttpPath = "/x/{id" };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var result = await RestInvoker.InvokeAsync(
            http, "http://127.0.0.1:1", method,
            jsonMessages: ["{}"], requestMetadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("NetworkError", result.Status);
    }
}
