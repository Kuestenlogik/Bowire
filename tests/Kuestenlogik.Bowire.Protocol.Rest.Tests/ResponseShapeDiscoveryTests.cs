// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Linting;
using Kuestenlogik.Bowire.Protocol.Rest.OpenApi3;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// #663 — REST discovery used to populate request parameters and no
/// response fields, so four of the five lint rules had nothing to inspect
/// on the most common protocol and a clean result meant "nothing to look
/// at". Both REST paths now carry the response shape: the OpenAPI adapter
/// reads the 2xx JSON schema (a top-level array as one repeated field),
/// the embedded path reads the endpoint's declared response type.
/// </summary>
public sealed class ResponseShapeDiscoveryTests
{
    private const string PetsDoc = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Pets", "version": "1.0" },
          "paths": {
            "/pets": {
              "get": {
                "operationId": "listPets",
                "tags": ["Pets"],
                "responses": {
                  "200": {
                    "description": "ok",
                    "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Pet" } } } }
                  }
                }
              }
            },
            "/pets/{id}": {
              "get": {
                "operationId": "getPet",
                "tags": ["Pets"],
                "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } } ],
                "responses": {
                  "200": {
                    "description": "ok",
                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } }
                  }
                }
              },
              "delete": {
                "operationId": "deletePet",
                "tags": ["Pets"],
                "responses": { "204": { "description": "gone" } }
              }
            }
          },
          "components": {
            "schemas": {
              "Pet": {
                "type": "object",
                "properties": {
                  "id": { "type": "integer" },
                  "name": { "type": "string" },
                  "email": { "type": "string" },
                  "createdAt": { "type": "string" }
                }
              }
            }
          }
        }
        """;

    [Fact]
    public async Task OpenApi_Response_Schema_Populates_Output_Fields_And_A_List_Is_A_Repeated_Field()
    {
        var parsed = await OpenApiDiscovery.ParseRawAsync(PetsDoc, TestContext.Current.CancellationToken);
        Assert.NotNull(parsed);
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);
        var pets = Assert.Single(services);

        var getPet = pets.Methods.Single(m => m.Name == "getPet");
        Assert.Equal(["id", "name", "email", "createdAt"], getPet.OutputType!.Fields.Select(f => f.Name));

        // A list endpoint: one repeated field of the element type, so the
        // pagination rule sees the list and the field rules see its shape.
        var listPets = pets.Methods.Single(m => m.Name == "listPets");
        var items = Assert.Single(listPets.OutputType!.Fields);
        Assert.Equal("items", items.Name);
        Assert.True(items.IsRepeated);
        Assert.Equal("message", items.Type);
        Assert.NotNull(items.MessageType);
        Assert.Contains(items.MessageType!.Fields, f => f.Name == "email");

        // No response body declared: nothing is invented.
        var deletePet = pets.Methods.Single(m => m.Name == "deletePet");
        Assert.Empty(deletePet.OutputType!.Fields);
    }

    [Fact]
    public async Task Lint_Sees_The_REST_Response_Shape()
    {
        // The point of the exercise: a REST surface with a personal field in
        // its responses and an unpaginated list must produce the findings a
        // gRPC surface with the same shape produces.
        var parsed = await OpenApiDiscovery.ParseRawAsync(PetsDoc, TestContext.Current.CancellationToken);
        var services = OpenApiDiscovery.BuildServices(parsed!.Document);

        var findings = BowireSchemaLinter.CreateDefault().Lint(services);

        Assert.Contains(findings, f => f.RuleId == "BWR-LINT-MISSING-PAGINATION" && f.Method == "listPets");
        Assert.Contains(findings, f => f.RuleId == "BWR-LINT-PII-RESPONSE" && f.Field == "email");
        Assert.Contains(findings, f => f.RuleId == "BWR-LINT-STRING-TIMESTAMP" && f.Field == "createdAt");
    }

    // ---- embedded (ApiExplorer) path ----

    private sealed record Pet(int Id, string Name, string Email);

    [Fact]
    public void Embedded_Discovery_Reads_The_Declared_Response_Type()
    {
        var single = Describe("GET", "pets/{id}", typeof(Pet));
        var list = Describe("GET", "pets", typeof(List<Pet>));
        var untyped = Describe("DELETE", "pets/{id}", null);
        var provider = new TestApiDescriptionGroupCollectionProvider(
            new ApiDescriptionGroupCollection([new ApiDescriptionGroup("Pets", [single, list, untyped])], 1));
        var sp = new ServiceCollection()
            .AddSingleton<IApiDescriptionGroupCollectionProvider>(provider)
            .BuildServiceProvider();

        Assert.True(EmbeddedDiscovery.TryDiscover(sp, out var services));
        var pets = Assert.Single(services);

        var getPet = pets.Methods.Single(m => m.HttpPath == "/pets/{id}" && m.HttpMethod == "GET");
        Assert.Equal(["id", "name", "email"], getPet.OutputType!.Fields.Select(f => f.Name));

        var listPets = pets.Methods.Single(m => m.HttpPath == "/pets");
        var items = Assert.Single(listPets.OutputType!.Fields);
        Assert.True(items.IsRepeated);
        Assert.Equal("message", items.Type);
        Assert.Contains(items.MessageType!.Fields, f => f.Name == "email");

        var deletePet = pets.Methods.Single(m => m.HttpMethod == "DELETE");
        Assert.Empty(deletePet.OutputType!.Fields);
    }

    private static ApiDescription Describe(string verb, string path, Type? responseType)
    {
        var api = new ApiDescription
        {
            HttpMethod = verb,
            RelativePath = path,
            GroupName = "Pets",
            ActionDescriptor = new ActionDescriptor { EndpointMetadata = Array.Empty<object>() },
        };
        if (responseType is not null)
        {
            api.SupportedResponseTypes.Add(new ApiResponseType { StatusCode = 200, Type = responseType });
        }
        return api;
    }

    private sealed class TestApiDescriptionGroupCollectionProvider(ApiDescriptionGroupCollection groups)
        : IApiDescriptionGroupCollectionProvider
    {
        public ApiDescriptionGroupCollection ApiDescriptionGroups { get; } = groups;
    }
}
