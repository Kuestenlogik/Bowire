// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Mock.Management;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Protocol.Rest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// #560 — the HTTP surface of a schema-mock start: <c>POST /api/mocks</c>
/// with the <c>{ schemaKind, schemaInline }</c> shape. Verifies the new
/// start branch's validation (400s) and the happy path (201 + list) end to
/// end, over a TestServer with the manager wired to the OpenAPI schema
/// source.
/// </summary>
[Collection("MockHostSerialised")]
public sealed class MockManagementSchemaEndpointTests
{
    private const string WeatherOpenApi = """
        openapi: 3.0.3
        info:
          title: Weather API
          version: 1.0.0
        paths:
          /weather:
            get:
              operationId: getCurrent
              tags: [Weather]
              responses:
                '200':
                  description: OK
                  content:
                    application/json:
                      schema:
                        type: object
                        properties:
                          condition:
                            type: string
                            enum: [sunny]
        """;

    static MockManagementSchemaEndpointTests()
    {
        BowireOpenApiAdapterRegistry.Register(
            new Kuestenlogik.Bowire.Protocol.Rest.OpenApi3.OpenApi3Adapter());
    }

    [Fact]
    public async Task POST_bad_schemaKind_returns_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await Post(client, """{"schemaKind":"soap","schemaInline":"x"}""");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("schemaKind", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task POST_missing_schema_content_returns_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await Post(client, """{"schemaKind":"openapi"}""");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("schemaInline", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task POST_schema_inline_starts_and_lists()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        var payload = JsonSerializer.Serialize(new { schemaKind = "openapi", schemaInline = WeatherOpenApi });
        using var resp = await Post(client, payload);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var summary = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(summary);
        var mockId = doc.RootElement.GetProperty("mockId").GetString();
        Assert.False(string.IsNullOrEmpty(mockId));

        try
        {
            // It shows up in GET /api/mocks.
            using var list = await client.GetAsync(new Uri("/api/mocks", UriKind.Relative), TestContext.Current.CancellationToken);
            var listBody = await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var listDoc = JsonDocument.Parse(listBody);
            var found = false;
            foreach (var m in listDoc.RootElement.GetProperty("mocks").EnumerateArray())
            {
                if (m.GetProperty("mockId").GetString() == mockId) { found = true; break; }
            }
            Assert.True(found, "the started schema mock should appear in GET /api/mocks");
        }
        finally
        {
            using var _ = await client.DeleteAsync(
                new Uri("/api/mocks/" + mockId, UriKind.Relative), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task POST_schema_on_recording_only_host_returns_400()
    {
        // A recording-only host (no schema sources wired) rejects a schema
        // start with a precise 400 rather than letting the manager's
        // InvalidOperationException surface as a 500.
        using var host = await BuildHost(wired: false);
        var client = host.GetTestClient();

        using var resp = await Post(client, """{"schemaKind":"openapi","schemaInline":"x"}""");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("no matching mock schema source", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task POST_config_apply_on_unknown_mock_returns_404()
    {
        // #561: the runtime config-apply endpoint 404s when the mock isn't
        // running (the happy path is covered end-to-end by the manager tests).
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(
            new Uri("/api/mocks/no-such-mock/config/apply", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri("/api/mocks", UriKind.Relative), content, TestContext.Current.CancellationToken);
    }

    private static async Task<IHost> BuildHost(bool wired = true)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireMockManagement(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       if (wired)
                       {
                           s.AddBowireMockManagement(
                               new IBowireMockSchemaSource[] { new OpenApiMockSchemaSource() },
                               Array.Empty<IBowireMockLiveSchemaHandler>(),
                               Array.Empty<IBowireMockHostingExtension>());
                       }
                       else
                       {
                           s.AddBowireMockManagement(); // recording-only
                       }
                   });
            })
            .Build();
        await host.StartAsync();
        return host;
    }
}
