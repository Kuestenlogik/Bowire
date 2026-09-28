// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Rest.OpenApi2;

namespace Kuestenlogik.Bowire.Protocol.Rest.OpenApi2.Tests;

/// <summary>Swagger 2.0 declares error responses too, and they reach the model the same way (#583).</summary>
public sealed class OpenApiErrorTypesTests
{
    [Fact]
    public async Task A_Swagger_2_Error_Response_Becomes_An_Error_Type()
    {
        const string doc = """
            {
              "swagger": "2.0",
              "info": { "title": "Customers", "version": "1" },
              "produces": ["application/json"],
              "paths": {
                "/customers/{id}": {
                  "get": {
                    "operationId": "getCustomer",
                    "parameters": [{ "name": "id", "in": "path", "required": true, "type": "string" }],
                    "responses": {
                      "200": { "description": "ok", "schema": { "type": "object", "properties": { "id": { "type": "string" } } } },
                      "404": { "description": "not found", "schema": { "type": "object", "properties": { "phone": { "type": "string" } } } }
                    }
                  }
                }
              }
            }
            """;
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var method = OpenApiDiscovery.BuildServices(parsed!.Document).SelectMany(s => s.Methods).Single();

        var error = Assert.Single(method.ErrorTypes!);
        Assert.Contains(error.Fields, f => f.Name == "phone");
    }
}
