// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Kuestenlogik.Bowire.Mock.Management;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Protocol.Rest;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// #560 — the workbench Mocks rail starts a schema-only mock through
/// <see cref="BowireMockHostManager.StartFromSchemaAsync"/> (the API-level
/// equivalent of <c>bowire mock --schema</c>). Mirrors
/// <see cref="SchemaOnlyModeTests"/> for the schema source + adapter
/// registration.
/// </summary>
[Collection("MockHostSerialised")]
public sealed class MockSchemaMockStartTests
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
                        required: [condition]
                        properties:
                          condition:
                            type: string
                            enum: [sunny]
        """;

    static MockSchemaMockStartTests()
    {
        BowireOpenApiAdapterRegistry.Register(
            new Kuestenlogik.Bowire.Protocol.Rest.OpenApi3.OpenApi3Adapter());
    }

    private static BowireMockHostManager NewWiredManager() =>
        new(new IBowireMockSchemaSource[] { new OpenApiMockSchemaSource() },
            Array.Empty<IBowireMockLiveSchemaHandler>(),
            Array.Empty<IBowireMockHostingExtension>());

    [Fact]
    public async Task StartFromSchema_Inline_Openapi_Serves_And_Lists()
    {
        await using var manager = NewWiredManager();
        var handle = await manager.StartFromSchemaAsync(
            "openapi", WeatherOpenApi, schemaPath: null, "weather-schema-mock", port: 0,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(0, handle.Port);
        Assert.Equal("weather-schema-mock", handle.Label);
        Assert.Empty(handle.RecordingId); // schema mocks aren't recording-derived

        // It appears in the registry that GET /api/mocks projects.
        Assert.Contains(manager.List(), h => h.MockId == handle.MockId);

        // It serves a schema-synthesised response.
        using var http = new HttpClient();
        var resp = await http.GetAsync(new Uri($"{handle.Url}/weather"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("sunny", doc.RootElement.GetProperty("condition").GetString());

        Assert.True(await manager.StopAsync(handle.MockId, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(manager.List(), h => h.MockId == handle.MockId);
    }

    [Fact]
    public async Task StartFromSchema_From_Operator_Path_Serves_And_Keeps_File_On_Stop()
    {
        // The schemaPath branch: the mock reads an operator-owned file directly
        // (tempPath stays ""), so StopAsync must NOT delete it.
        var dir = Directory.CreateTempSubdirectory("bowire-schema-path-").FullName;
        var schemaFile = Path.Combine(dir, "weather.yaml");
        await File.WriteAllTextAsync(schemaFile, WeatherOpenApi, TestContext.Current.CancellationToken);
        try
        {
            await using var manager = NewWiredManager();
            var handle = await manager.StartFromSchemaAsync(
                "openapi", schemaInline: null, schemaFile, "path-mock", port: 0,
                TestContext.Current.CancellationToken);

            using var http = new HttpClient();
            var resp = await http.GetAsync(new Uri($"{handle.Url}/weather"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            Assert.True(await manager.StopAsync(handle.MockId, TestContext.Current.CancellationToken));
            Assert.True(File.Exists(schemaFile),
                "an operator-owned schema file must survive StopAsync (tempPath is \"\")");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task StartFromSchema_UnknownKind_Throws()
    {
        await using var manager = NewWiredManager();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.StartFromSchemaAsync("soap", "x", null, "l", 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartFromSchema_NoSourceRegistered_Throws()
    {
        // Recording-only manager (the embedded default) can't start a schema mock.
        await using var manager = new BowireMockHostManager();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.StartFromSchemaAsync("openapi", WeatherOpenApi, null, "l", 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartFromSchema_NoInlineOrPath_Throws()
    {
        await using var manager = NewWiredManager();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.StartFromSchemaAsync("openapi", null, null, "l", 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SchemaKinds_Reflects_Wired_Sources()
    {
        await using var recordingOnly = new BowireMockHostManager();
        Assert.Empty(recordingOnly.SchemaKinds);

        await using var wired = NewWiredManager();
        Assert.Contains("openapi", wired.SchemaKinds);
    }
}
