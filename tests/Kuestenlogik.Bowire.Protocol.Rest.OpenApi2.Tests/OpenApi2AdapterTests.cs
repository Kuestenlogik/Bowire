// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Protocol.Rest;
using Kuestenlogik.Bowire.Protocol.Rest.OpenApi2;

namespace Kuestenlogik.Bowire.Protocol.Rest.OpenApi2.Tests;

/// <summary>
/// Drives <see cref="OpenApi2Adapter"/>'s thin facade — version marker,
/// fetch+discover, parse+discover, and the file-based mock-recording
/// builder. Each test asserts the adapter wires the underlying
/// OpenApiDiscovery / OpenApiRecordingBuilder helpers correctly.
/// </summary>
// CA2000 / CA2025: response-message lifetime spans the stub-handler ->
// HttpClient boundary; production callers use using var resp = await
// http.GetAsync(...) to dispose, and StubHandler also tracks emitted
// responses for a belt-and-braces dispose on handler-disposal
// (HttpResponseMessage.Dispose is idempotent, so the double-call is a
// no-op). CA2000 on the handler itself is a known false positive --
// HttpClient owns and disposes its inner handler.
#pragma warning disable CA2000, CA2025
public sealed class OpenApi2AdapterTests
{
    [Fact]
    public void OpenApiLibraryMajorVersion_Is_Two()
    {
        // Used by BowireOpenApiAdapterRegistry to pick the implementation
        // matching the Microsoft.OpenApi.dll already loaded.
        var adapter = new OpenApi2Adapter();

        Assert.Equal(2, adapter.OpenApiLibraryMajorVersion);
    }

    [Fact]
    public void Adapter_Implements_IBowireOpenApiAdapter()
    {
        var adapter = new OpenApi2Adapter();

        Assert.IsAssignableFrom<IBowireOpenApiAdapter>(adapter);
    }

    [Fact]
    public async Task ParseAndDiscoverAsync_Empty_Content_Returns_Null()
    {
        var adapter = new OpenApi2Adapter();

        var result = await adapter.ParseAndDiscoverAsync(
            "", "label", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task ParseAndDiscoverAsync_Returns_Result_With_Source_Label_And_Raw_Content()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Adapter API", "version": "1" },
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/things": {
                  "get": {
                    "operationId": "listThings",
                    "tags": ["Things"],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        var adapter = new OpenApi2Adapter();

        var result = await adapter.ParseAndDiscoverAsync(
            doc, "my-uploaded-doc.json", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("my-uploaded-doc.json", result!.SourceUrl);
        Assert.Equal("https://api.example.com", result.ApiBaseUrl);
        Assert.Equal(doc, result.RawContent);
        var svc = Assert.Single(result.Services);
        Assert.Equal("Things", svc.Name);
        var method = Assert.Single(svc.Methods);
        Assert.Equal("listThings", method.Name);
        Assert.Equal("GET", method.HttpMethod);
        Assert.Equal("/things", method.HttpPath);
    }

    [Fact]
    public async Task ParseAndDiscoverAsync_Yaml_Content_Round_Trips()
    {
        // OpenAPI 2.x library requires explicit YAML registration. The
        // adapter wires AddYamlReader on the parsing settings so YAML
        // input doesn't silently fall back to null.
        const string yaml = """
            openapi: 3.0.0
            info:
              title: YAML Adapter
              version: '1'
            paths:
              /yaml:
                get:
                  operationId: getYaml
                  responses:
                    '200':
                      description: OK
            """;
        var adapter = new OpenApi2Adapter();

        var result = await adapter.ParseAndDiscoverAsync(
            yaml, "yaml-source", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("yaml-source", result!.SourceUrl);
        var method = result.Services.Single().Methods.Single();
        Assert.Equal("getYaml", method.Name);
    }

    [Fact]
    public async Task FetchAndDiscoverAsync_Url_Returns_Null_For_404()
    {
        using var http = new HttpClient(new StubHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var adapter = new OpenApi2Adapter();

        var result = await adapter.FetchAndDiscoverAsync(
            "http://example.com/missing.json", http, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAndDiscoverAsync_Empty_Url_Returns_Null()
    {
        using var http = new HttpClient();
        var adapter = new OpenApi2Adapter();

        var result = await adapter.FetchAndDiscoverAsync(
            "", http, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAndDiscoverAsync_Valid_Doc_Returns_DiscoveryResult_With_All_Fields()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Fetched", "version": "1" },
              "servers": [{ "url": "https://api.fetched.com/v3" }],
              "paths": {
                "/items": {
                  "get": {
                    "operationId": "listItems",
                    "tags": ["Items"],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        const string url = "http://example.com/openapi-fetched.json";

        using var http = new HttpClient(new StubHandler((req, ct) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(doc, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }));

        var adapter = new OpenApi2Adapter();
        var result = await adapter.FetchAndDiscoverAsync(
            url, http, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(url, result!.SourceUrl);
        Assert.Equal("https://api.fetched.com/v3", result.ApiBaseUrl);
        Assert.Equal(doc, result.RawContent);
        var svc = Assert.Single(result.Services);
        Assert.Equal("Items", svc.Name);
        Assert.Equal("Fetched", svc.Package);
    }

    [Fact]
    public async Task FetchAndDiscoverAsync_Doc_With_No_Server_Has_Null_ApiBaseUrl()
    {
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "NoServer", "version": "1" },
              "paths": {
                "/x": { "get": { "operationId": "x", "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;
        using var http = new HttpClient(new StubHandler((req, ct) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(doc, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }));

        var adapter = new OpenApi2Adapter();
        var result = await adapter.FetchAndDiscoverAsync(
            "http://example.com/spec.json", http, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Null(result!.ApiBaseUrl);
    }

    [Fact]
    public async Task BuildMockRecordingFromFileAsync_Round_Trips_Json_File()
    {
        // Drives the adapter's facade over OpenApiRecordingBuilder.LoadAsync —
        // the recording builder itself is exercised in deeper detail elsewhere.
        const string doc = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Mock Rec", "version": "1" },
              "paths": {
                "/things": {
                  "get": {
                    "operationId": "listThings",
                    "tags": ["Things"],
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": {
                          "application/json": {
                            "schema": {
                              "type": "object",
                              "properties": { "name": { "type": "string" } }
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
        var path = Path.Combine(Path.GetTempPath(), "openapi2-adapter-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, doc, TestContext.Current.CancellationToken);

        try
        {
            var adapter = new OpenApi2Adapter();
            var recording = await adapter.BuildMockRecordingFromFileAsync(
                path, TestContext.Current.CancellationToken);

            Assert.NotNull(recording);
            Assert.Equal("Mock Rec", recording.Name);
            var step = Assert.Single(recording.Steps);
            Assert.Equal("listThings", step.Method);
            Assert.Equal("Things", step.Service);
            Assert.Equal("GET", step.HttpVerb);
            Assert.Equal("/things", step.HttpPath);
            Assert.NotNull(step.Response);
            Assert.Contains("\"name\"", step.Response!, StringComparison.Ordinal);

            // SourceSchema is stamped with the verbatim text so the mock
            // host can serve /openapi.json back unchanged.
            Assert.NotNull(recording.SourceSchema);
            Assert.Equal(doc, recording.SourceSchema!.Content);
            Assert.Equal("openapi-3.0", recording.SourceSchema.Format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BuildMockRecordingFromFileAsync_Missing_File_Throws_FileNotFoundException()
    {
        var path = Path.Combine(Path.GetTempPath(), "not-here-" + Guid.NewGuid().ToString("N") + ".json");
        var adapter = new OpenApi2Adapter();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            adapter.BuildMockRecordingFromFileAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Test message handler that tracks every emitted response so they're
    /// disposed when the owning HttpClient is -- closes the
    /// cs/local-not-disposed loop the analyzer can't see across the
    /// handler -> HttpClient -> production-code boundary.
    /// HttpResponseMessage.Dispose is idempotent, so production code's
    /// own using var resp = ... doesn't conflict.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _f;
        private readonly List<HttpResponseMessage> _emitted = new();
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) => _f = f;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = await _f(request, cancellationToken).ConfigureAwait(false);
            lock (_emitted) _emitted.Add(resp);
            return resp;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_emitted)
                {
                    foreach (var r in _emitted) r.Dispose();
                    _emitted.Clear();
                }
            }
            base.Dispose(disposing);
        }
    }
}
#pragma warning restore CA2000, CA2025
