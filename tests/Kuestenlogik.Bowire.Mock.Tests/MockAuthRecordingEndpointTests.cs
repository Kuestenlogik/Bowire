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
/// #563: the HTTP wiring of auth-recording resolution in
/// <c>POST /api/mocks/{id}/config/apply</c> — the outcome→status mapping
/// (Resolved→200 / NotFound→404 / NoResolver→500) that the helper unit tests
/// and the manager-level gate e2e do not exercise over HTTP.
/// </summary>
[Collection("MockHostSerialised")]
public sealed class MockAuthRecordingEndpointTests
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
        """;

    static MockAuthRecordingEndpointTests()
    {
        BowireOpenApiAdapterRegistry.Register(
            new Kuestenlogik.Bowire.Protocol.Rest.OpenApi3.OpenApi3Adapter());
    }

    private sealed class FakeResolver(MockAuthResolution? result) : IAuthRecordingResolver
    {
        public MockAuthResolution? TryResolve(string authRecordingId, string? workspaceId) => result;
    }

    [Fact]
    public async Task Apply_With_Resolvable_AuthRecordingId_Returns_200()
    {
        using var host = await BuildHost(new FakeResolver(new MockAuthResolution("captured-tok", "bearer", null)));
        var client = host.GetTestClient();
        var mockId = await StartMock(client);

        using var resp = await ApplyAuth(client, mockId, """{"auth":{"required":true,"authRecordingId":"rec-1"}}""");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await Stop(client, mockId);
    }

    [Fact]
    public async Task Apply_With_Unresolvable_AuthRecordingId_Returns_404()
    {
        using var host = await BuildHost(new FakeResolver(result: null));
        var client = host.GetTestClient();
        var mockId = await StartMock(client);

        using var resp = await ApplyAuth(client, mockId, """{"auth":{"required":true,"authRecordingId":"rec-1"}}""");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        await Stop(client, mockId);
    }

    [Fact]
    public async Task Apply_With_AuthRecordingId_But_No_Resolver_Returns_500()
    {
        using var host = await BuildHost(resolver: null);
        var client = host.GetTestClient();
        var mockId = await StartMock(client);

        using var resp = await ApplyAuth(client, mockId, """{"auth":{"required":true,"authRecordingId":"rec-1"}}""");

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        await Stop(client, mockId);
    }

    [Fact]
    public async Task Apply_With_Direct_Credential_Needs_No_Resolver()
    {
        // A config that sets auth.credential directly (no authRecordingId) must
        // still apply on a host without any resolver registered.
        using var host = await BuildHost(resolver: null);
        var client = host.GetTestClient();
        var mockId = await StartMock(client);

        using var resp = await ApplyAuth(client, mockId, """{"auth":{"required":true,"credential":"direct-tok"}}""");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await Stop(client, mockId);
    }

    private static async Task<string> StartMock(HttpClient client)
    {
        var payload = JsonSerializer.Serialize(new { schemaKind = "openapi", schemaInline = WeatherOpenApi });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(new Uri("/api/mocks", UriKind.Relative), content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("mockId").GetString()!;
    }

    private static async Task<HttpResponseMessage> ApplyAuth(HttpClient client, string mockId, string configJson)
    {
        using var content = new StringContent(configJson, Encoding.UTF8, "application/json");
        return await client.PostAsync(
            new Uri($"/api/mocks/{mockId}/config/apply", UriKind.Relative), content, TestContext.Current.CancellationToken);
    }

    private static async Task Stop(HttpClient client, string mockId)
    {
        using var _ = await client.DeleteAsync(new Uri("/api/mocks/" + mockId, UriKind.Relative), TestContext.Current.CancellationToken);
    }

    private static async Task<IHost> BuildHost(IAuthRecordingResolver? resolver)
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
                       s.AddBowireMockManagement(
                           new IBowireMockSchemaSource[] { new OpenApiMockSchemaSource() },
                           Array.Empty<IBowireMockLiveSchemaHandler>(),
                           Array.Empty<IBowireMockHostingExtension>());
                       if (resolver is not null) s.AddSingleton(resolver);
                   });
            })
            .Build();
        await host.StartAsync();
        return host;
    }
}
