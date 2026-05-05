// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Rest;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the REST protocol plugin's identity, embedded discovery
/// fall-throughs, the OpenAPI-doc cache contract on cold/missing sources,
/// and the AWS SigV4 marker parser. Live HTTP invocation is exercised by
/// the integration suite — here we drive the in-process branches that don't
/// need a server.
/// </summary>
[Collection(nameof(OpenApiUploadStoreTestGroup))]
public sealed class BowireRestProtocolTests
{
    public BowireRestProtocolTests()
    {
        OpenApiUploadStore.Clear();
    }

    [Fact]
    public void Identity_Properties_Are_Stable()
    {
        var protocol = new BowireRestProtocol();

        Assert.Equal("REST", protocol.Name);
        Assert.Equal("rest", protocol.Id);
        Assert.NotNull(protocol.IconSvg);
        Assert.Contains("<svg", protocol.IconSvg, StringComparison.Ordinal);
    }

    [Fact]
    public void Implements_Protocol_And_Inline_Http_Invoker_Surfaces()
    {
        var protocol = new BowireRestProtocol();

        Assert.IsAssignableFrom<IBowireProtocol>(protocol);
        Assert.IsAssignableFrom<IInlineHttpInvoker>(protocol);
    }

    [Fact]
    public void Initialize_Accepts_Null_Service_Provider()
    {
        var protocol = new BowireRestProtocol();

        protocol.Initialize(null);
    }

    [Fact]
    public async Task DiscoverAsync_Empty_Url_And_No_Uploads_Returns_Empty()
    {
        var protocol = new BowireRestProtocol();

        var services = await protocol.DiscoverAsync(
            "", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Picks_Up_Uploaded_OpenApi_Document()
    {
        const string petsDoc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Pets API", "version": "1.0" },
              "paths": {
                "/pets/{id}": {
                  "get": {
                    "operationId": "getPet",
                    "tags": ["Pets"],
                    "parameters": [
                      { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        OpenApiUploadStore.Add(petsDoc, "pets.json");
        try
        {
            var protocol = new BowireRestProtocol();

            var services = await protocol.DiscoverAsync(
                "", showInternalServices: false, TestContext.Current.CancellationToken);

            var svc = Assert.Single(services);
            Assert.Equal("Pets", svc.Name);
            Assert.True(svc.IsUploaded);
            Assert.Equal("pets.json", svc.OriginUrl);
            var method = Assert.Single(svc.Methods);
            Assert.Equal("getPet", method.Name);
            Assert.Equal("GET", method.HttpMethod);
            Assert.Equal("/pets/{id}", method.HttpPath);
        }
        finally
        {
            OpenApiUploadStore.Clear();
        }
    }

    [Fact]
    public async Task InvokeAsync_Without_Discovery_Or_Cache_Returns_Error_Result()
    {
        // Empty server URL means: cold cache + no embedded provider + no
        // upload registered. Discovery falls through, the cache lookup
        // misses, and the plugin returns a structured error.
        var protocol = new BowireRestProtocol();

        var result = await protocol.InvokeAsync(
            serverUrl: "http://127.0.0.2:1",
            service: "Unknown",
            method: "Nope",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.True(result.Metadata.ContainsKey("error"));
    }

    [Fact]
    public async Task InvokeStreamAsync_Yields_No_Items_Because_Rest_Is_Unary()
    {
        var protocol = new BowireRestProtocol();

        var collected = new List<string>();
        await foreach (var item in protocol.InvokeStreamAsync(
            "http://localhost:5000", "X", "Y", ["{}"], false, null, TestContext.Current.CancellationToken))
        {
            collected.Add(item);
        }

        Assert.Empty(collected);
    }

    [Fact]
    public async Task OpenChannelAsync_Returns_Null_Because_Rest_Is_Stateless()
    {
        var protocol = new BowireRestProtocol();

        var channel = await protocol.OpenChannelAsync(
            "http://localhost:5000", "X", "Y", false, null, TestContext.Current.CancellationToken);

        Assert.Null(channel);
    }
}

/// <summary>
/// Tests for <see cref="OpenApiDiscovery"/>'s pure-parsing surface — no
/// HTTP fetching. Covers the OpenAPI 3 → BowireServiceInfo conversion
/// against in-memory documents.
/// </summary>
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
        var parsed = await OpenApiDiscovery.ParseRawAsync("this isn't OpenAPI at all", TestContext.Current.CancellationToken);

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
    public async Task ParseRawAsync_Valid_Doc_Returns_DiscoveredApi()
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
    }

    [Fact]
    public async Task BuildServices_Groups_Operations_By_Tag()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Multi", "version": "1" },
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

        // Each service should pick up the doc's title as its package.
        Assert.All(services, s => Assert.Equal("Multi", s.Package));
    }

    [Fact]
    public async Task BuildServices_Picks_Up_Path_Query_Header_Parameters()
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

        // Cookie source is dropped — only path / query / header survive.
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
                              "name":  { "type": "string" },
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

        var ageField = method.InputType.Fields.Single(f => f.Name == "age");
        Assert.Equal("body", ageField.Source);
        Assert.False(ageField.Required);
        Assert.Equal("int64", ageField.Type);
    }

    [Fact]
    public async Task BuildServices_Free_Form_Body_Becomes_Single_Body_Field()
    {
        // No Properties on the request-body schema — we should fall through
        // to the "body" sentinel field so the user can edit the raw JSON.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/raw": {
                  "post": {
                    "operationId": "postRaw",
                    "requestBody": {
                      "content": {
                        "application/json": { "schema": { "type": "string" } }
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
    public async Task GetFirstServerUrl_Returns_Url_With_Variable_Substitution()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "servers": [
                {
                  "url": "https://{host}.example.com/v1",
                  "variables": {
                    "host": { "default": "api" }
                  }
                }
              ],
              "paths": { "/x": { "get": { "operationId": "getX", "responses": { "200": { "description": "ok" } } } } }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);

        var url = OpenApiDiscovery.GetFirstServerUrl(parsed!.Document);

        Assert.Equal("https://api.example.com/v1", url);
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
        Assert.Contains(levelField.EnumValues, ev => ev.Name == "low");
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
}

/// <summary>
/// Tests for <see cref="RestInvoker"/> — exercises the path-template /
/// query-string / multipart / header / body buckets via reflection on the
/// internal helpers, and the SigV4 marker JSON parser. Real HTTP send is
/// integration-tier territory, but invoking against an unreachable URL
/// drives the network-error branch end-to-end.
/// </summary>
public sealed class RestInvokerTests
{
    [Fact]
    public async Task InvokeAsync_Method_Without_HttpAnnotation_Returns_Error_Result()
    {
        // No HttpMethod / HttpPath — the invoker rejects it before any HTTP
        // happens.
        var method = new BowireMethodInfo(
            Name: "Bare",
            FullName: "Bare",
            ClientStreaming: false, ServerStreaming: false,
            InputType: new BowireMessageInfo("In", "In", []),
            OutputType: new BowireMessageInfo("Out", "Out", []),
            MethodType: "Unary");

        using var http = new HttpClient();
        var result = await RestInvoker.InvokeAsync(
            http, "http://example.com", method, ["{}"], requestMetadata: null, TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Equal(0, result.DurationMs);
        Assert.Contains("HTTP annotation", result.Metadata["error"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Bad_Json_Body_Returns_Error_Result()
    {
        var method = new BowireMethodInfo(
            Name: "M",
            FullName: "POST /m",
            ClientStreaming: false, ServerStreaming: false,
            InputType: new BowireMessageInfo("In", "In", []),
            OutputType: new BowireMessageInfo("Out", "Out", []),
            MethodType: "Unary")
        {
            HttpMethod = "POST",
            HttpPath = "/m"
        };

        using var http = new HttpClient();
        var result = await RestInvoker.InvokeAsync(
            http, "http://example.com", method, ["{not json}"], requestMetadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Contains("body JSON", result.Metadata["error"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Missing_Path_Param_Returns_Error_Result()
    {
        // Path template needs {id} but the JSON doesn't supply it.
        var method = new BowireMethodInfo(
            Name: "M",
            FullName: "GET /x/{id}",
            ClientStreaming: false, ServerStreaming: false,
            InputType: new BowireMessageInfo("In", "In",
            [
                new BowireFieldInfo("id", 1, "string", "required", false, false, null, null) { Source = "path", Required = true }
            ]),
            OutputType: new BowireMessageInfo("Out", "Out", []),
            MethodType: "Unary")
        {
            HttpMethod = "GET",
            HttpPath = "/x/{id}"
        };

        using var http = new HttpClient();
        var result = await RestInvoker.InvokeAsync(
            http, "http://example.com", method, ["{}"], requestMetadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Contains("path parameter", result.Metadata["error"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Network_Failure_Returns_NetworkError_Status()
    {
        var method = new BowireMethodInfo(
            Name: "M",
            FullName: "GET /m",
            ClientStreaming: false, ServerStreaming: false,
            InputType: new BowireMessageInfo("In", "In", []),
            OutputType: new BowireMessageInfo("Out", "Out", []),
            MethodType: "Unary")
        {
            HttpMethod = "GET",
            HttpPath = "/m"
        };

        // 127.0.0.1:1 — loopback port nobody listens on. The OS rejects the
        // connect with ECONNREFUSED, which Http surfaces as HttpRequestException
        // — RestInvoker's catch turns it into status="NetworkError". Generous
        // timeout so we never hit HttpClient.Timeout (which escapes as
        // TaskCanceledException, not the path we're testing).
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var result = await RestInvoker.InvokeAsync(
            http, "http://127.0.0.1:1", method, ["{}"], requestMetadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("NetworkError", result.Status);
        Assert.True(result.Metadata.ContainsKey("error"));
    }

    [Fact]
    public void HttpStatusToBowireStatus_Maps_2xx_To_OK()
    {
        Assert.Equal("OK", InvokeStatusMap(200));
        Assert.Equal("OK", InvokeStatusMap(204));
        Assert.Equal("OK", InvokeStatusMap(299));
    }

    [Fact]
    public void HttpStatusToBowireStatus_Maps_4xx_To_Specific_Names()
    {
        Assert.Equal("InvalidArgument", InvokeStatusMap(400));
        Assert.Equal("Unauthenticated", InvokeStatusMap(401));
        Assert.Equal("PermissionDenied", InvokeStatusMap(403));
        Assert.Equal("NotFound", InvokeStatusMap(404));
        Assert.Equal("DeadlineExceeded", InvokeStatusMap(408));
        Assert.Equal("AlreadyExists", InvokeStatusMap(409));
        Assert.Equal("ResourceExhausted", InvokeStatusMap(429));
        Assert.Equal("FailedPrecondition", InvokeStatusMap(418));
    }

    [Fact]
    public void HttpStatusToBowireStatus_Maps_5xx_To_Internal_Family()
    {
        Assert.Equal("Unimplemented", InvokeStatusMap(501));
        Assert.Equal("Unavailable", InvokeStatusMap(503));
        Assert.Equal("Internal", InvokeStatusMap(500));
        Assert.Equal("Internal", InvokeStatusMap(599));
    }

    [Fact]
    public void HttpStatusToBowireStatus_Sub_200_Is_Unknown()
    {
        // The status map's only "Unknown" path is sub-200 (1xx informational).
        // Codes >= 500 always land in "Internal" — see HttpStatusToBowireStatus.
        Assert.Equal("Unknown", InvokeStatusMap(100));
        Assert.Equal("Unknown", InvokeStatusMap(199));
    }

    [Fact]
    public void AwsSigV4Config_TryParse_Valid_Json()
    {
        const string json = """
            {
              "accessKey": "AKIA...",
              "secretKey": "secret",
              "region": "us-east-1",
              "service": "s3",
              "sessionToken": "tok"
            }
            """;
        var cfg = InvokeAwsSigV4TryParse(json);

        Assert.NotNull(cfg);
    }

    [Fact]
    public void AwsSigV4Config_TryParse_Missing_Field_Returns_Null()
    {
        const string json = """{ "accessKey": "AKIA", "secretKey": "s" }""";
        Assert.Null(InvokeAwsSigV4TryParse(json));
    }

    [Fact]
    public void AwsSigV4Config_TryParse_Invalid_Json_Returns_Null()
    {
        Assert.Null(InvokeAwsSigV4TryParse("not json"));
    }

    [Fact]
    public void AwsSigV4Config_TryParse_Non_Object_Returns_Null()
    {
        Assert.Null(InvokeAwsSigV4TryParse("\"a string\""));
    }

    private static string InvokeStatusMap(int code)
    {
        var type = typeof(RestInvoker);
        var method = type.GetMethod("HttpStatusToBowireStatus", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, new object[] { code });
        return Assert.IsType<string>(result);
    }

    private static object? InvokeAwsSigV4TryParse(string json)
    {
        var cfgType = typeof(RestInvoker).GetNestedType("AwsSigV4Config", BindingFlags.NonPublic);
        Assert.NotNull(cfgType);
        var method = cfgType!.GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, new object[] { json });
    }
}

/// <summary>
/// Tests for <see cref="OpenApiUploadStore"/> — round-trips raw documents
/// through the in-memory store and asserts the lifecycle (Add → GetAll →
/// HasUploads → Clear).
/// </summary>
[Collection(nameof(OpenApiUploadStoreTestGroup))]
public sealed class OpenApiUploadStoreTests : IDisposable
{
    public OpenApiUploadStoreTests()
    {
        OpenApiUploadStore.Clear();
    }

    public void Dispose()
    {
        OpenApiUploadStore.Clear();
    }

    [Fact]
    public void Add_Returns_Unique_Id_With_Upload_Prefix()
    {
        var id = OpenApiUploadStore.Add("{}", "x.json");

        Assert.StartsWith("upload_", id, StringComparison.Ordinal);
    }

    [Fact]
    public void HasUploads_Reflects_Add_And_Clear()
    {
        Assert.False(OpenApiUploadStore.HasUploads);
        OpenApiUploadStore.Add("{}", "x.json");
        Assert.True(OpenApiUploadStore.HasUploads);
        OpenApiUploadStore.Clear();
        Assert.False(OpenApiUploadStore.HasUploads);
    }

    [Fact]
    public void GetAll_Returns_Snapshot_With_Uploaded_Entry()
    {
        OpenApiUploadStore.Add("{ \"a\": 1 }", "name");

        var all = OpenApiUploadStore.GetAll();

        var doc = Assert.Single(all);
        Assert.Equal("name", doc.SourceName);
        Assert.Equal("{ \"a\": 1 }", doc.Content);
    }

    [Fact]
    public void Add_Without_SourceName_Defaults_To_Uploaded()
    {
        OpenApiUploadStore.Add("{}");

        var doc = Assert.Single(OpenApiUploadStore.GetAll());
        Assert.Equal("uploaded", doc.SourceName);
    }
}

#pragma warning disable CA1822, CA1515, CA2227, CA1034, CA1062

/// <summary>
/// Tests for <see cref="EmbeddedDiscovery"/> — drives the in-process
/// <c>IApiDescriptionGroupCollectionProvider</c> path with synthetic
/// <see cref="ApiDescription"/> instances so the discovery branches that
/// need an ASP.NET host (parameter binding, route-constraint stripping,
/// path-derived names) are exercised here without one.
/// </summary>
public sealed class EmbeddedDiscoveryTests
{
    [Fact]
    public void TryDiscover_Null_Provider_Returns_False()
    {
        var ok = EmbeddedDiscovery.TryDiscover(null, out var services);

        Assert.False(ok);
        Assert.Empty(services);
    }

    [Fact]
    public void TryDiscover_Provider_Without_ApiExplorer_Returns_False()
    {
        var sp = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();

        var ok = EmbeddedDiscovery.TryDiscover(sp, out var services);

        Assert.False(ok);
        Assert.Empty(services);
    }

    [Fact]
    public void TryDiscover_Empty_ApiExplorer_Returns_False()
    {
        var provider = new TestApiDescriptionGroupCollectionProvider(
            new ApiDescriptionGroupCollection(Array.Empty<ApiDescriptionGroup>(), 0));
        var sp = BuildProvider(provider);

        var ok = EmbeddedDiscovery.TryDiscover(sp, out var services);

        Assert.False(ok);
        Assert.Empty(services);
    }

    [Fact]
    public void TryDiscover_Single_GET_Endpoint_Produces_One_Service()
    {
        var api = BuildApiDescription("GET", "users/{id}", "Default");
        var provider = ProviderFor(api);
        var sp = BuildProvider(provider);

        var ok = EmbeddedDiscovery.TryDiscover(sp, out var services);

        Assert.True(ok);
        var svc = Assert.Single(services);
        Assert.Equal("Default", svc.Name);  // group fallback
        Assert.Equal("REST", svc.Package);
        Assert.Equal("rest", svc.Source);

        var method = Assert.Single(svc.Methods);
        Assert.Equal("GET", method.HttpMethod);
        Assert.Equal("/users/{id}", method.HttpPath);
        Assert.Equal("Unary", method.MethodType);
    }

    [Fact]
    public void TryDiscover_Strips_Route_Constraints_From_Path()
    {
        var api = BuildApiDescription("GET", "users/{id:int}/orders/{slug:regex(^[a-z]+$)}", "Users");
        var provider = ProviderFor(api);
        var sp = BuildProvider(provider);

        EmbeddedDiscovery.TryDiscover(sp, out var services);

        var method = services.SelectMany(s => s.Methods).Single();
        Assert.Equal("/users/{id}/orders/{slug}", method.HttpPath);
    }

    [Fact]
    public void TryDiscover_Strips_Default_Value_And_Optional_Constraints()
    {
        var api = BuildApiDescription("GET", "items/{name=foo}/{tag?}", "Items");
        var provider = ProviderFor(api);
        var sp = BuildProvider(provider);

        EmbeddedDiscovery.TryDiscover(sp, out var services);

        var method = services.SelectMany(s => s.Methods).Single();
        Assert.Equal("/items/{name}/{tag}", method.HttpPath);
    }

    [Fact]
    public void TryDiscover_Skips_Endpoints_Missing_HttpMethod()
    {
        var goodApi = BuildApiDescription("GET", "ok", "Default");
        var badApi = new ApiDescription { RelativePath = "broken", HttpMethod = null };
        var provider = ProviderFor(goodApi, badApi);
        var sp = BuildProvider(provider);

        EmbeddedDiscovery.TryDiscover(sp, out var services);

        var svc = Assert.Single(services);
        Assert.Single(svc.Methods);
    }

    [Fact]
    public void TryDiscover_Buckets_Parameters_By_Source()
    {
        var api = BuildApiDescription("GET", "x/{id}", "Default");
        api.ParameterDescriptions.Add(new ApiParameterDescription
        {
            Name = "id",
            Source = BindingSource.Path,
            Type = typeof(string),
            IsRequired = true
        });
        api.ParameterDescriptions.Add(new ApiParameterDescription
        {
            Name = "limit",
            Source = BindingSource.Query,
            Type = typeof(int),
            IsRequired = false
        });
        api.ParameterDescriptions.Add(new ApiParameterDescription
        {
            Name = "X-Trace",
            Source = BindingSource.Header,
            Type = typeof(string),
            IsRequired = false
        });
        api.ParameterDescriptions.Add(new ApiParameterDescription
        {
            Name = "session",
            Source = BindingSource.Custom,
            Type = typeof(string),
            IsRequired = false
        });

        var provider = ProviderFor(api);
        var sp = BuildProvider(provider);

        EmbeddedDiscovery.TryDiscover(sp, out var services);

        var method = services.SelectMany(s => s.Methods).Single();
        var sources = method.InputType.Fields.Select(f => f.Source).ToHashSet();
        Assert.Contains("path", sources);
        Assert.Contains("query", sources);
        Assert.Contains("header", sources);
        // Custom source has no mapping — the field is skipped.
        Assert.DoesNotContain("custom", sources);

        // Type mapping
        var idField = method.InputType.Fields.Single(f => f.Name == "id");
        Assert.Equal("string", idField.Type);
        Assert.True(idField.Required);

        var limitField = method.InputType.Fields.Single(f => f.Name == "limit");
        Assert.Equal("int32", limitField.Type);
    }

    [Fact]
    public void TryDiscover_Body_Param_With_Complex_Type_Is_Flattened()
    {
        var api = BuildApiDescription("POST", "users", "Users");
        api.ParameterDescriptions.Add(new ApiParameterDescription
        {
            Name = "user",
            Source = BindingSource.Body,
            Type = typeof(EmbeddedDiscoveryUserDto),
            IsRequired = true
        });
        var provider = ProviderFor(api);
        var sp = BuildProvider(provider);

        EmbeddedDiscovery.TryDiscover(sp, out var services);

        var method = services.SelectMany(s => s.Methods).Single();
        // Properties on UserDto: Name (string), Age (int), Address (complex) — flattened to body fields.
        var fieldNames = method.InputType.Fields.Select(f => f.Name).ToHashSet();
        Assert.Contains("name", fieldNames);
        Assert.Contains("age", fieldNames);
        Assert.Contains("address", fieldNames);

        // Address is complex — should be a "message" type with nested fields.
        var addressField = method.InputType.Fields.Single(f => f.Name == "address");
        Assert.Equal("message", addressField.Type);
        Assert.NotNull(addressField.MessageType);
    }

    [Fact]
    public void TryDiscover_Body_Param_With_Scalar_Type_Stays_Single_Field()
    {
        var api = BuildApiDescription("POST", "echo", "Default");
        api.ParameterDescriptions.Add(new ApiParameterDescription
        {
            Name = "value",
            Source = BindingSource.Body,
            Type = typeof(string),  // scalar — IsComplexType returns false
            IsRequired = true
        });
        var provider = ProviderFor(api);
        var sp = BuildProvider(provider);

        EmbeddedDiscovery.TryDiscover(sp, out var services);

        var method = services.SelectMany(s => s.Methods).Single();
        var bodyField = Assert.Single(method.InputType.Fields);
        Assert.Equal("value", bodyField.Name);
        Assert.Equal("body", bodyField.Source);
    }

    [Fact]
    public void TryDiscover_Form_Source_Maps_To_Body()
    {
        var api = BuildApiDescription("POST", "form", "Default");
        api.ParameterDescriptions.Add(new ApiParameterDescription
        {
            Name = "field",
            Source = BindingSource.Form,
            Type = typeof(string),
            IsRequired = false
        });
        var provider = ProviderFor(api);
        var sp = BuildProvider(provider);

        EmbeddedDiscovery.TryDiscover(sp, out var services);

        var method = services.SelectMany(s => s.Methods).Single();
        Assert.Equal("body", method.InputType.Fields[0].Source);
    }

    [Fact]
    public void TryDiscover_Derives_Method_Name_From_Path_When_No_Endpoint_Name()
    {
        var api = BuildApiDescription("GET", "weather/forecast", "Default");
        var provider = ProviderFor(api);
        var sp = BuildProvider(provider);

        EmbeddedDiscovery.TryDiscover(sp, out var services);

        var method = services.SelectMany(s => s.Methods).Single();
        Assert.Equal("GetForecast", method.Name);
    }

    [Fact]
    public void TryDiscover_Empty_Path_Becomes_GetRoot()
    {
        var api = BuildApiDescription("GET", "", "Default");
        var provider = ProviderFor(api);
        var sp = BuildProvider(provider);

        EmbeddedDiscovery.TryDiscover(sp, out var services);

        var method = services.SelectMany(s => s.Methods).Single();
        Assert.Equal("GetRoot", method.Name);
    }

    [Fact]
    public void TryDiscover_Path_Of_Only_Placeholders_Becomes_VerbRoot()
    {
        var api = BuildApiDescription("PUT", "{id}/{name}", "Default");
        var provider = ProviderFor(api);
        var sp = BuildProvider(provider);

        EmbeddedDiscovery.TryDiscover(sp, out var services);

        var method = services.SelectMany(s => s.Methods).Single();
        Assert.Equal("PutRoot", method.Name);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void TryDiscover_All_Common_Verbs_Round_Trip(string verb)
    {
        var api = BuildApiDescription(verb, "things", "Default");
        var provider = ProviderFor(api);
        var sp = BuildProvider(provider);

        EmbeddedDiscovery.TryDiscover(sp, out var services);

        var method = services.SelectMany(s => s.Methods).Single();
        Assert.Equal(verb.ToUpperInvariant(), method.HttpMethod);
    }

    private static ApiDescription BuildApiDescription(string verb, string relativePath, string groupName)
    {
        var api = new ApiDescription
        {
            HttpMethod = verb,
            RelativePath = relativePath,
            GroupName = groupName,
            ActionDescriptor = new ActionDescriptor
            {
                EndpointMetadata = Array.Empty<object>()
            }
        };
        return api;
    }

    private static TestApiDescriptionGroupCollectionProvider ProviderFor(params ApiDescription[] apis)
    {
        var groups = new Dictionary<string, List<ApiDescription>>(StringComparer.Ordinal);
        foreach (var api in apis)
        {
            var key = api.GroupName ?? "Default";
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }
            list.Add(api);
        }

        var groupList = groups.Select(kv => new ApiDescriptionGroup(kv.Key, kv.Value)).ToList();
        return new TestApiDescriptionGroupCollectionProvider(new ApiDescriptionGroupCollection(groupList, 1));
    }

    private static ServiceProvider BuildProvider(IApiDescriptionGroupCollectionProvider provider)
    {
        return new ServiceCollection()
            .AddSingleton(provider)
            .BuildServiceProvider();
    }
}

internal sealed class TestApiDescriptionGroupCollectionProvider : IApiDescriptionGroupCollectionProvider
{
    public TestApiDescriptionGroupCollectionProvider(ApiDescriptionGroupCollection groups)
    {
        ApiDescriptionGroups = groups;
    }

    public ApiDescriptionGroupCollection ApiDescriptionGroups { get; }
}

internal sealed class EmbeddedDiscoveryUserDto
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public EmbeddedDiscoveryAddressDto? Address { get; set; }
}

internal sealed class EmbeddedDiscoveryAddressDto
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
}

#pragma warning restore CA1822, CA1515, CA2227, CA1034, CA1062

