// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Rest.OpenApi2;

namespace Kuestenlogik.Bowire.Protocol.Rest.OpenApi2.Tests;

/// <summary>
/// Mirror of the OpenApi3 adapter's discovery-coverage suite. Drives the
/// OpenApi2 adapter's pure-parsing surface against in-memory fixtures so
/// the parsing branches (path/query/header buckets, body schemas, enums,
/// arrays, server URL composition) light up without any HTTP fetch.
/// </summary>
// CA1861 (inline array allocs) and CA2000 / CA2025 (handler lifetime in
// short test methods) are noisy here: the inline expected arrays are
// fixture-local and live for one Assert, and HttpClient takes ownership
// of the stub handler before its `using` block disposes both together.
#pragma warning disable CA1861, CA2000, CA2025
public sealed class OpenApiDiscoveryTests
{
    [Fact]
    public async Task ParseRawAsync_Empty_Returns_Null()
    {
        var parsed = await OpenApiDiscovery.ParseRawAsync("", TestContext.Current.CancellationToken);

        Assert.Null(parsed);
    }

    [Fact]
    public async Task ParseRawAsync_Whitespace_Returns_Null()
    {
        var parsed = await OpenApiDiscovery.ParseRawAsync("   ", TestContext.Current.CancellationToken);

        Assert.Null(parsed);
    }

    [Fact]
    public async Task ParseRawAsync_Garbage_Returns_Null()
    {
        var parsed = await OpenApiDiscovery.ParseRawAsync(
            "this isn't OpenAPI at all", TestContext.Current.CancellationToken);

        Assert.Null(parsed);
    }

    [Fact]
    public async Task ParseRawAsync_OpenApi_With_No_Paths_Returns_Null()
    {
        const string emptyDoc = """
            { "openapi": "3.0.0", "info": { "title": "Empty", "version": "1" }, "paths": {} }
            """;

        var parsed = await OpenApiDiscovery.ParseRawAsync(emptyDoc, TestContext.Current.CancellationToken);

        Assert.Null(parsed);
    }

    [Fact]
    public async Task ParseRawAsync_Valid_Doc_Returns_DiscoveredApi_With_RawText_Preserved()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/x": { "get": { "operationId": "getX", "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;

        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Document);
        Assert.NotEmpty(parsed.Document.Paths);
        Assert.Equal("uploaded", parsed.ServerUrl);
        Assert.Equal(doc, parsed.RawText);
    }

    [Fact]
    public async Task ParseRawAsync_Yaml_Document_Round_Trips()
    {
        // OpenApi 2.x doesn't auto-register YAML readers; the adapter
        // calls AddYamlReader() explicitly. This exercises that branch.
        const string yaml = """
            openapi: 3.0.0
            info:
              title: YAML Doc
              version: '1'
            paths:
              /pets:
                get:
                  operationId: listPets
                  responses:
                    '200':
                      description: OK
            """;

        var parsed = await OpenApiDiscovery.ParseRawAsync(yaml, TestContext.Current.CancellationToken);

        Assert.NotNull(parsed);
        Assert.Equal("YAML Doc", parsed!.Document.Info!.Title);
    }

    [Fact]
    public async Task BuildServices_Groups_Operations_By_Tag()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Multi", "description": "Catalog", "version": "1" },
              "paths": {
                "/users": {
                  "get": {
                    "operationId": "listUsers",
                    "tags": ["Users"],
                    "responses": { "200": { "description": "ok" } }
                  }
                },
                "/orders": {
                  "get": {
                    "operationId": "listOrders",
                    "tags": ["Orders"],
                    "responses": { "200": { "description": "ok" } }
                  }
                },
                "/untagged": {
                  "get": {
                    "operationId": "untagged",
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);

        var services = OpenApiDiscovery.BuildServices(parsed!.Document);

        Assert.Equal(3, services.Count);
        var names = services.Select(s => s.Name).ToHashSet();
        Assert.Contains("Users", names);
        Assert.Contains("Orders", names);
        Assert.Contains("Default", names);  // tagless operations land here

        // Each service should pick up the doc's title as its package + the
        // description / version surfaces.
        Assert.All(services, s => Assert.Equal("Multi", s.Package));
        Assert.All(services, s => Assert.Equal("Catalog", s.Description));
        Assert.All(services, s => Assert.Equal("1", s.Version));
        Assert.All(services, s => Assert.Equal("rest", s.Source));
    }

    [Fact]
    public async Task BuildServices_Methods_Sorted_By_Http_Path()
    {
        // Sort order is HttpPath-string-ordinal so the UI shows
        // related routes adjacent.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/z": { "get": { "operationId": "z", "tags": ["S"], "responses": { "200": { "description": "ok" } } } },
                "/a": { "get": { "operationId": "a", "tags": ["S"], "responses": { "200": { "description": "ok" } } } },
                "/m": { "get": { "operationId": "m", "tags": ["S"], "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var svc = services.Single();

        Assert.Equal(new[] { "/a", "/m", "/z" }, svc.Methods.Select(m => m.HttpPath));
    }

    [Fact]
    public async Task BuildServices_Picks_Up_Path_Query_Header_Parameters_And_Drops_Cookie()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "P", "version": "1" },
              "paths": {
                "/items/{id}": {
                  "get": {
                    "operationId": "fetchItem",
                    "parameters": [
                      { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } },
                      { "name": "limit", "in": "query", "schema": { "type": "integer" } },
                      { "name": "X-Trace", "in": "header", "schema": { "type": "string" } },
                      { "name": "session", "in": "cookie", "schema": { "type": "string" } }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);

        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var method = services.SelectMany(s => s.Methods).Single(m => m.Name == "fetchItem");

        var sources = method.InputType.Fields.Select(f => f.Source).ToHashSet();
        Assert.Contains("path", sources);
        Assert.Contains("query", sources);
        Assert.Contains("header", sources);
        Assert.DoesNotContain("cookie", sources);

        var idField = method.InputType.Fields.Single(f => f.Name == "id");
        Assert.True(idField.Required);

        var limitField = method.InputType.Fields.Single(f => f.Name == "limit");
        Assert.Equal("int32", limitField.Type);
    }

    [Fact]
    public async Task BuildServices_Parameter_Without_Name_Is_Skipped()
    {
        // SchemaToField bails on name == "" — this drives the early-continue
        // branch in BuildMethod's parameter loop.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/x": {
                  "get": {
                    "operationId": "x",
                    "parameters": [
                      { "name": "", "in": "query", "schema": { "type": "string" } },
                      { "name": "real", "in": "query", "schema": { "type": "string" } }
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

        var field = Assert.Single(method.InputType.Fields);
        Assert.Equal("real", field.Name);
    }

    [Fact]
    public async Task BuildServices_Body_Schema_Is_Flattened_To_Body_Fields()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "P", "version": "1" },
              "paths": {
                "/users": {
                  "post": {
                    "operationId": "createUser",
                    "requestBody": {
                      "required": true,
                      "content": {
                        "application/json": {
                          "schema": {
                            "type": "object",
                            "required": ["name"],
                            "properties": {
                              "name":  { "type": "string", "description": "Display name" },
                              "age":   { "type": "integer", "format": "int64" }
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
        var method = services.SelectMany(s => s.Methods).Single(m => m.Name == "createUser");

        var nameField = method.InputType.Fields.Single(f => f.Name == "name");
        Assert.Equal("body", nameField.Source);
        Assert.True(nameField.Required);
        Assert.Equal("string", nameField.Type);
        Assert.Equal("Display name", nameField.Description);

        var ageField = method.InputType.Fields.Single(f => f.Name == "age");
        Assert.Equal("body", ageField.Source);
        Assert.False(ageField.Required);
        Assert.Equal("int64", ageField.Type);
    }

    [Fact]
    public async Task BuildServices_Vendor_Plus_Json_Body_Is_Picked_Up()
    {
        // ExtractJsonBodySchema's second-pass fallback handles content-types
        // ending in +json (e.g. application/hal+json).
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Hal API", "version": "1" },
              "paths": {
                "/widgets": {
                  "post": {
                    "operationId": "createWidget",
                    "requestBody": {
                      "content": {
                        "application/hal+json": {
                          "schema": {
                            "type": "object",
                            "properties": { "name": { "type": "string" } }
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

        var nameField = Assert.Single(method.InputType.Fields);
        Assert.Equal("name", nameField.Name);
        Assert.Equal("body", nameField.Source);
    }

    [Fact]
    public async Task BuildServices_X_WwwForm_Urlencoded_Body_Is_Treated_As_Form()
    {
        // ExtractMultipartSchema also recognises application/x-www-form-urlencoded.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Form API", "version": "1" },
              "paths": {
                "/login": {
                  "post": {
                    "operationId": "login",
                    "requestBody": {
                      "required": true,
                      "content": {
                        "application/x-www-form-urlencoded": {
                          "schema": {
                            "type": "object",
                            "required": ["username"],
                            "properties": {
                              "username": { "type": "string" },
                              "password": { "type": "string" }
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

        Assert.Equal(2, method.InputType.Fields.Count);
        Assert.All(method.InputType.Fields, f => Assert.Equal("formdata", f.Source));
        var usernameField = method.InputType.Fields.Single(f => f.Name == "username");
        Assert.True(usernameField.Required);
        Assert.False(usernameField.IsBinary);
    }

    [Fact]
    public async Task BuildServices_Free_Form_Body_Becomes_Single_Body_Field()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/raw": {
                  "post": {
                    "operationId": "postRaw",
                    "requestBody": {
                      "required": true,
                      "content": {
                        "application/json": { "schema": { "type": "string", "description": "raw text body" } }
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
        var method = services.SelectMany(s => s.Methods).Single(m => m.Name == "postRaw");

        var bodyField = Assert.Single(method.InputType.Fields);
        Assert.Equal("body", bodyField.Name);
        Assert.Equal("body", bodyField.Source);
        Assert.True(bodyField.Required);
        Assert.Equal("string", bodyField.Type);
        Assert.Equal("raw text body", bodyField.Description);
    }

    [Fact]
    public async Task BuildServices_Uses_Operation_Summary_And_Description_And_Deprecation()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/x": {
                  "get": {
                    "operationId": "getX",
                    "summary": "Short summary",
                    "description": "Long description",
                    "deprecated": true,
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var method = services.SelectMany(s => s.Methods).Single(m => m.Name == "getX");

        Assert.Equal("Short summary", method.Summary);
        Assert.Equal("Long description", method.Description);
        Assert.True(method.Deprecated);
    }

    [Fact]
    public async Task BuildServices_Operation_Without_OperationId_Synthesises_Method_Name_From_Verb_And_Path()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/foo/{id}": {
                  "delete": {
                    "tags": ["F"],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var method = services.SelectMany(s => s.Methods).Single();

        // SanitizeName replaces non letter/digit chars with '_'.
        Assert.Equal("DELETE__foo__id_", method.Name);
        Assert.Equal("DELETE /foo/{id}", method.FullName);
        Assert.Equal("DELETE", method.HttpMethod);
        Assert.Equal("/foo/{id}", method.HttpPath);
    }

    [Fact]
    public async Task GetFirstServerUrl_Returns_Null_When_No_Servers()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": { "/x": { "get": { "operationId": "getX", "responses": { "200": { "description": "ok" } } } } }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);

        Assert.Null(OpenApiDiscovery.GetFirstServerUrl(parsed!.Document));
    }

    [Fact]
    public async Task GetFirstServerUrl_Returns_Plain_Url_When_No_Variables()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "servers": [{ "url": "https://api.example.com/v1" }],
              "paths": { "/x": { "get": { "operationId": "getX", "responses": { "200": { "description": "ok" } } } } }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);

        var url = OpenApiDiscovery.GetFirstServerUrl(parsed!.Document);

        Assert.Equal("https://api.example.com/v1", url);
    }

    [Fact]
    public async Task GetFirstServerUrl_Returns_Url_With_Variable_Substitution()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "servers": [
                {
                  "url": "https://{host}.example.com/{basePath}",
                  "variables": {
                    "host": { "default": "api" },
                    "basePath": { "default": "v2" }
                  }
                }
              ],
              "paths": { "/x": { "get": { "operationId": "getX", "responses": { "200": { "description": "ok" } } } } }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);

        var url = OpenApiDiscovery.GetFirstServerUrl(parsed!.Document);

        Assert.Equal("https://api.example.com/v2", url);
    }

    [Fact]
    public async Task BuildServices_Enum_Schema_Yields_Enum_Values()
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
                      {
                        "name": "level",
                        "in": "query",
                        "schema": { "type": "string", "enum": ["low", "med", "high"] }
                      }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var method = services.SelectMany(s => s.Methods).Single(m => m.Name == "getX");
        var levelField = method.InputType.Fields.Single(f => f.Name == "level");

        Assert.NotNull(levelField.EnumValues);
        Assert.Equal(3, levelField.EnumValues!.Count);
        Assert.Equal(new[] { "low", "med", "high" }, levelField.EnumValues.Select(e => e.Name));
        Assert.Equal(new[] { 0, 1, 2 }, levelField.EnumValues.Select(e => e.Number));
    }

    [Fact]
    public async Task BuildServices_Array_Param_Becomes_Repeated_Field()
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
                      {
                        "name": "tags",
                        "in": "query",
                        "schema": { "type": "array", "items": { "type": "string" } }
                      }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var method = services.SelectMany(s => s.Methods).Single(m => m.Name == "getX");
        var tagsField = method.InputType.Fields.Single(f => f.Name == "tags");

        Assert.True(tagsField.IsRepeated);
        Assert.Equal("string", tagsField.Type);
    }

    [Fact]
    public async Task BuildServices_Nested_Object_Param_Becomes_Message_Type()
    {
        // Object schema with properties triggers SchemaToMessage's nested-builder
        // branch, surfaced as Type="message" + a populated MessageType.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/users": {
                  "post": {
                    "operationId": "createUser",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": {
                            "type": "object",
                            "properties": {
                              "name": { "type": "string" },
                              "address": {
                                "type": "object",
                                "required": ["city"],
                                "properties": {
                                  "city":   { "type": "string" },
                                  "street": { "type": "string" }
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

        var addressField = method.InputType.Fields.Single(f => f.Name == "address");
        Assert.Equal("message", addressField.Type);
        Assert.NotNull(addressField.MessageType);
        Assert.Equal("addressType", addressField.MessageType!.Name);
        Assert.Equal(2, addressField.MessageType.Fields.Count);

        var cityField = addressField.MessageType.Fields.Single(f => f.Name == "city");
        Assert.True(cityField.Required);

        var streetField = addressField.MessageType.Fields.Single(f => f.Name == "street");
        Assert.False(streetField.Required);
    }

    [Fact]
    public async Task BuildServices_Number_Format_Float_Maps_To_Float_Otherwise_Double()
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
                      { "name": "f", "in": "query", "schema": { "type": "number", "format": "float" } },
                      { "name": "d", "in": "query", "schema": { "type": "number" } },
                      { "name": "b", "in": "query", "schema": { "type": "boolean" } }
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

        Assert.Equal("float", method.InputType.Fields.Single(f => f.Name == "f").Type);
        Assert.Equal("double", method.InputType.Fields.Single(f => f.Name == "d").Type);
        Assert.Equal("bool", method.InputType.Fields.Single(f => f.Name == "b").Type);
    }

    [Fact]
    public async Task BuildServices_String_Byte_Format_Maps_To_Bytes()
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
                      { "name": "blob", "in": "query", "schema": { "type": "string", "format": "byte" } }
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

        Assert.Equal("bytes", method.InputType.Fields.Single(f => f.Name == "blob").Type);
    }

    [Fact]
    public async Task BuildServices_Parameter_Example_Wins_Over_Schema_Example()
    {
        // Parameter-level example takes precedence over the schema's
        // example, which takes precedence over the schema's default.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/x": {
                  "get": {
                    "operationId": "getX",
                    "parameters": [
                      {
                        "name": "p1",
                        "in": "query",
                        "example": "from-parameter",
                        "schema": { "type": "string", "example": "from-schema", "default": "from-default" }
                      },
                      {
                        "name": "p2",
                        "in": "query",
                        "schema": { "type": "string", "example": "from-schema" }
                      },
                      {
                        "name": "p3",
                        "in": "query",
                        "schema": { "type": "string", "default": "from-default" }
                      }
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

        Assert.Equal("\"from-parameter\"", method.InputType.Fields.Single(f => f.Name == "p1").Example);
        Assert.Equal("\"from-schema\"", method.InputType.Fields.Single(f => f.Name == "p2").Example);
        Assert.Equal("\"from-default\"", method.InputType.Fields.Single(f => f.Name == "p3").Example);
    }

    [Fact]
    public async Task BuildServices_Response_With_Json_Schema_Is_Captured_As_Output()
    {
        // ExtractOutputType picks the 2xx response with a JSON-shape schema
        // and converts its properties into the method's output message.
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
                              "required": ["id"],
                              "properties": {
                                "id":   { "type": "string" },
                                "name": { "type": "string" }
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
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var method = services.SelectMany(s => s.Methods).Single();

        Assert.Equal("getXResponse", method.OutputType.Name);
        Assert.Equal(2, method.OutputType.Fields.Count);
        Assert.True(method.OutputType.Fields.Single(f => f.Name == "id").Required);
        Assert.False(method.OutputType.Fields.Single(f => f.Name == "name").Required);
    }

    [Fact]
    public async Task BuildServices_Response_Without_Json_Falls_Through_To_Empty_Output()
    {
        // 200 declared without content -> ExtractOutputType returns the
        // empty-placeholder message so the schema tab still renders.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/x": {
                  "get": {
                    "operationId": "noResp",
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var method = services.SelectMany(s => s.Methods).Single();

        Assert.Equal("Response", method.OutputType.Name);
        Assert.Empty(method.OutputType.Fields);
    }

    [Fact]
    public async Task BuildServices_Response_With_Vendor_Plus_Json_Is_Captured()
    {
        // ExtractOutputType's second branch — contentType.Contains("+json").
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/x": {
                  "get": {
                    "operationId": "halResp",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": {
                          "application/hal+json": {
                            "schema": {
                              "type": "object",
                              "properties": { "_links": { "type": "string" } }
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
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var method = services.SelectMany(s => s.Methods).Single();

        Assert.Equal("halRespResponse", method.OutputType.Name);
        Assert.Single(method.OutputType.Fields);
    }

    [Fact]
    public async Task FetchAndParseAsync_Empty_Url_Returns_Null()
    {
        using var http = new HttpClient();

        var result = await OpenApiDiscovery.FetchAndParseAsync(
            "", http, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAndParseAsync_Invalid_Uri_Returns_Null()
    {
        using var http = new HttpClient();

        var result = await OpenApiDiscovery.FetchAndParseAsync(
            "not://a real url::", http, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAndParseAsync_Returns_Null_On_404()
    {
        // Stub handler that returns a 404 — IsSuccessStatusCode == false,
        // adapter swallows and returns null.
        using var http = new HttpClient(new StubHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));

        var result = await OpenApiDiscovery.FetchAndParseAsync(
            "http://example.com/openapi.json", http, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAndParseAsync_Returns_Null_For_Html_Content_Type()
    {
        // Content-type contains "html" — the adapter refuses to parse the
        // body as OpenAPI even when the response is 200.
        using var http = new HttpClient(new StubHandler((req, ct) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>not openapi</html>", System.Text.Encoding.UTF8, "text/html")
            };
            return Task.FromResult(resp);
        }));

        var result = await OpenApiDiscovery.FetchAndParseAsync(
            "http://example.com/index.html", http, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAndParseAsync_Throws_From_Handler_Is_Caught_And_Returns_Null()
    {
        // Network failure path: HTTP send throws; adapter's catch-all
        // wraps it and returns null so other protocol plugins can retry.
        using var http = new HttpClient(new StubHandler((req, ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("dns failed"))));

        var result = await OpenApiDiscovery.FetchAndParseAsync(
            "http://example.com/spec.json", http, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAndParseAsync_Valid_Document_Populates_Cache_And_Returns_DiscoveredApi()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Cached", "version": "1" },
              "paths": {
                "/x": { "get": { "operationId": "getX", "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;
        const string url = "http://example.com/openapi-cached.json";

        SourceSchemaCache.Clear();
        try
        {
            using var http = new HttpClient(new StubHandler((req, ct) =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(doc, System.Text.Encoding.UTF8, "application/json")
                };
                return Task.FromResult(resp);
            }));

            var result = await OpenApiDiscovery.FetchAndParseAsync(
                url, http, TestContext.Current.CancellationToken);

            Assert.NotNull(result);
            Assert.Equal(url, result!.ServerUrl);
            Assert.Equal(doc, result.RawText);
            Assert.Equal("Cached", result.Document.Info!.Title);

            // The discovery layer must have written the raw text into
            // SourceSchemaCache keyed by the source URL so the recording
            // endpoint can later stamp it on saved recordings.
            var cached = SourceSchemaCache.Get(url);
            Assert.NotNull(cached);
            Assert.Equal(doc, cached!.Content);
            Assert.Equal(url, cached.SourceUrl);
            Assert.Equal("openapi-3.0", cached.Format);
        }
        finally
        {
            SourceSchemaCache.Clear();
        }
    }

    [Fact]
    public async Task FetchAndParseAsync_Doc_Without_Paths_Returns_Null()
    {
        const string doc = """
            { "openapi": "3.0.0", "info": { "title": "E", "version": "1" }, "paths": {} }
            """;
        using var http = new HttpClient(new StubHandler((req, ct) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(doc, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }));

        var result = await OpenApiDiscovery.FetchAndParseAsync(
            "http://example.com/empty.json", http, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _f;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) => _f = f;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _f(request, cancellationToken);
    }
}
#pragma warning restore CA1861, CA2000, CA2025
