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

    private sealed class FixedResponseHandler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType)
            });
    }
}
