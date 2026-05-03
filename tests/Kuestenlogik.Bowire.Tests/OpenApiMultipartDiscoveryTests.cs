// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Rest;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for the OpenAPI → BowireMethodInfo conversion path that
/// recognises <c>multipart/form-data</c> request bodies and flags binary
/// fields. Uses inline OpenAPI 3 JSON fixtures so the tests pin the
/// discovery shape without any HTTP fetches.
/// </summary>
public class OpenApiMultipartDiscoveryTests
{
    private const string MultipartUploadDoc = """
        {
            "openapi": "3.0.0",
            "info": { "title": "Upload API", "version": "1.0" },
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
                                            "file": {
                                                "type": "string",
                                                "format": "binary",
                                                "description": "The file to upload"
                                            },
                                            "description": {
                                                "type": "string",
                                                "description": "Caption text"
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

    [Fact]
    public async Task MultipartFormData_BinaryField_DiscoveredAsFormdataIsBinary()
    {
        var parsed = await OpenApiDiscovery.ParseRawAsync(MultipartUploadDoc, TestContext.Current.CancellationToken);
        Assert.NotNull(parsed);

        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var method = services
            .SelectMany(s => s.Methods)
            .Single(m => m.Name == "upload");

        var fileField = method.InputType.Fields.Single(f => f.Name == "file");
        Assert.Equal("formdata", fileField.Source);
        Assert.True(fileField.IsBinary);
        Assert.True(fileField.Required);
        Assert.Equal("The file to upload", fileField.Description);

        var descField = method.InputType.Fields.Single(f => f.Name == "description");
        Assert.Equal("formdata", descField.Source);
        Assert.False(descField.IsBinary);
        Assert.False(descField.Required);
    }

    [Fact]
    public async Task JsonRequestBody_StillWinsOverMultipart_WhenBothDeclared()
    {
        // When an OpenAPI operation declares both application/json and
        // multipart/form-data, the JSON shape stays canonical (Bowire's
        // happy path) — multipart kicks in only for upload-only endpoints.
        var dualDoc = """
            {
                "openapi": "3.0.0",
                "info": { "title": "Dual API", "version": "1.0" },
                "paths": {
                    "/dual": {
                        "post": {
                            "operationId": "dual",
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
        var parsed = await OpenApiDiscovery.ParseRawAsync(dualDoc, TestContext.Current.CancellationToken);
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var method = services.SelectMany(s => s.Methods).Single(m => m.Name == "dual");

        Assert.Single(method.InputType.Fields);
        Assert.Equal("name", method.InputType.Fields[0].Name);
        Assert.Equal("body", method.InputType.Fields[0].Source);
        Assert.False(method.InputType.Fields[0].IsBinary);
    }
}
