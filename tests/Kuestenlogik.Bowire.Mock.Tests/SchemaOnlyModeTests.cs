// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Phase 3d: when started with a schema instead of a recording, the mock
/// generates plausible responses straight from the OpenAPI document's
/// response schemas — no recorded traffic needed. The standard mock
/// pipeline (matcher, chaos, stateful mode, …) runs unchanged on the
/// synthesised recording.
/// </summary>
public sealed class SchemaOnlyModeTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "weather.openapi.yaml");

    [Fact]
    public async Task SchemaOnly_ServesObjectResponseFromResolvedRef()
    {
        await using var server = await MockServer.StartAsync(
            new MockServerOptions { SchemaPath = FixturePath, Port = 0, Watch = false, ReplaySpeed = 0, SchemaSources = new IBowireMockSchemaSource[] { new OpenApiMockSchemaSource() } },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.GetAsync(
            new Uri($"http://127.0.0.1:{server.Port}/weather?location=hamburg"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        // Required fields all populated by the generator.
        Assert.Equal(JsonValueKind.Number, root.GetProperty("temperature").ValueKind);
        Assert.Equal("sunny", root.GetProperty("condition").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("recordedAt").ValueKind);
    }

    [Fact]
    public async Task SchemaOnly_ArrayResponseEmitsThreeItems()
    {
        await using var server = await MockServer.StartAsync(
            new MockServerOptions { SchemaPath = FixturePath, Port = 0, Watch = false, ReplaySpeed = 0, SchemaSources = new IBowireMockSchemaSource[] { new OpenApiMockSchemaSource() } },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.GetAsync(
            new Uri($"http://127.0.0.1:{server.Port}/weather/forecast"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);

        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.Equal(3, json.RootElement.GetArrayLength());
        // Each array item is a full Weather object.
        Assert.Equal("sunny", json.RootElement[0].GetProperty("condition").GetString());
    }

    [Fact]
    public async Task SchemaOnly_NoSuccessBody_RespondsWithCodeAndEmptyPayload()
    {
        // /admin/ping only declares a 204 response with no content schema;
        // the mock should still respond 204 with "null" as a placeholder
        // body (the synthesised step has null schema → generator emits "null").
        await using var server = await MockServer.StartAsync(
            new MockServerOptions { SchemaPath = FixturePath, Port = 0, Watch = false, ReplaySpeed = 0, SchemaSources = new IBowireMockSchemaSource[] { new OpenApiMockSchemaSource() } },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.GetAsync(
            new Uri($"http://127.0.0.1:{server.Port}/admin/ping"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task SchemaOnly_ComposesWithChaos_FailRate1Returns503()
    {
        // Chaos still wires through schema-only mode — the synthesised
        // recording is just a recording as far as the middleware is
        // concerned.
        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                SchemaPath = FixturePath,
                Port = 0,
                Watch = false,
                ReplaySpeed = 0,
                Chaos = new Kuestenlogik.Bowire.Mock.Chaos.ChaosOptions { FailRate = 1.0 },
                SchemaSources = new IBowireMockSchemaSource[] { new OpenApiMockSchemaSource() }
            },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.GetAsync(
            new Uri($"http://127.0.0.1:{server.Port}/weather?location=berlin"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task StartAsync_RejectsBothRecordingAndSchema()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MockServer.StartAsync(
                new MockServerOptions
                {
                    RecordingPath = "some-path.json",
                    SchemaPath = "some-other-path.yaml",
                    Port = 0,
                    Watch = false
                },
                TestContext.Current.CancellationToken));
        Assert.Contains("exactly one", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_RejectsNeitherRecordingNorSchema()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MockServer.StartAsync(
                new MockServerOptions { Port = 0, Watch = false },
                TestContext.Current.CancellationToken));
    }
}
