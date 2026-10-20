// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using Kuestenlogik.Bowire.Protocol.Rest.OpenApi3;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// Spec shapes the OpenApi3 discovery must accept: YAML documents,
/// self-referencing schemas, and JSON media types carrying parameters.
/// </summary>
public sealed class OpenApiDiscoveryInputShapeTests
{
    private const string YamlDoc = """
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

    [Fact]
    public async Task ParseRawAsync_Yaml_Document_Is_Parsed()
    {
        var parsed = await OpenApiDiscovery.ParseRawAsync(YamlDoc, TestContext.Current.CancellationToken);

        Assert.NotNull(parsed);
        Assert.Equal("YAML Doc", parsed!.Document.Info!.Title);
    }

    [Fact]
    public async Task FetchAndParseAsync_Yaml_Document_Is_Parsed()
    {
        using var handler = new FixedResponseHandler(YamlDoc, "application/yaml");
        using var http = new HttpClient(handler);

        var parsed = await OpenApiDiscovery.FetchAndParseAsync(
            "http://api.example/openapi.yaml", http, TestContext.Current.CancellationToken);

        Assert.NotNull(parsed);
        Assert.Equal("YAML Doc", parsed!.Document.Info!.Title);
    }

    [Fact]
    public async Task Self_Referencing_Schema_Terminates_With_A_Truncated_Shape()
    {
        // Node { name, child: Node } — a linked list / tree. Before the
        // cycle guard this recursed until the reader's stack guard threw and
        // the whole document failed to discover.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Tree", "version": "1" },
              "paths": {
                "/nodes": {
                  "post": {
                    "operationId": "createNode",
                    "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Node" } } } },
                    "responses": {
                      "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Node" } } } }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "Node": {
                    "type": "object",
                    "properties": {
                      "name": { "type": "string" },
                      "child": { "$ref": "#/components/schemas/Node" }
                    }
                  }
                }
              }
            }
            """;

        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        Assert.NotNull(parsed);

        var method = Assert.Single(Assert.Single(OpenApiDiscovery.BuildServices(parsed!.Document)).Methods);

        // Response: Node is on the path at the root, so its own `child`
        // closes the cycle and comes back as a truncated stub.
        var responseChild = method.OutputType.Fields.Single(f => f.Name == "child");
        Assert.Equal("message", responseChild.Type);
        Assert.NotNull(responseChild.MessageType);
        Assert.True(responseChild.MessageType!.Truncated);
        Assert.Empty(responseChild.MessageType.Fields);

        // Request body: the body's properties are flattened into fields, so
        // `child` expands Node once and its own `child` is the truncated stub.
        var requestChild = method.InputType.Fields.Single(f => f.Name == "child");
        Assert.Equal("message", requestChild.Type);
        Assert.False(requestChild.MessageType!.Truncated);
        var grandChild = requestChild.MessageType.Fields.Single(f => f.Name == "child");
        Assert.True(grandChild.MessageType!.Truncated);
    }

    [Fact]
    public async Task Shared_Type_Used_Twice_Expands_At_Both_Sites()
    {
        // The cycle guard tracks the current path only: a type that merely
        // appears twice (origin + destination) is not a cycle and must keep
        // its fields at both sites.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Legs", "version": "1" },
              "paths": {
                "/legs": {
                  "get": {
                    "operationId": "getLeg",
                    "responses": {
                      "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Leg" } } } }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "Port": { "type": "object", "properties": { "code": { "type": "string" } } },
                  "Leg": {
                    "type": "object",
                    "properties": {
                      "origin": { "$ref": "#/components/schemas/Port" },
                      "destination": { "$ref": "#/components/schemas/Port" }
                    }
                  }
                }
              }
            }
            """;

        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        var method = Assert.Single(Assert.Single(OpenApiDiscovery.BuildServices(parsed!.Document)).Methods);

        foreach (var name in new[] { "origin", "destination" })
        {
            var port = method.OutputType.Fields.Single(f => f.Name == name).MessageType;
            Assert.NotNull(port);
            Assert.False(port!.Truncated);
            Assert.Equal("code", Assert.Single(port.Fields).Name);
        }
    }

    private sealed class FixedResponseHandler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType)
            });
    }
}
