// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Protocol.Rest;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// #558: end-to-end proof that a <see cref="MockConfiguration"/>'s per-field
/// overrides are applied onto the schema-synthesised recording and are
/// visible in the served response — the "override-applies-to-running-schema-
/// mock" acceptance. Mirrors <see cref="SchemaOnlyModeTests"/>, adding a
/// <see cref="MockServerOptions.MockConfig"/>.
/// </summary>
public sealed class MockConfigApplyModeTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "weather.openapi.yaml");

    static MockConfigApplyModeTests()
    {
        BowireOpenApiAdapterRegistry.Register(
            new Kuestenlogik.Bowire.Protocol.Rest.OpenApi3.OpenApi3Adapter());
    }

    private static MockConfiguration OverrideConfig()
    {
        var config = new MockConfiguration();
        // Wildcard scope: apply to every synthesised step. The object-shaped
        // /weather response carries these fields; the array + no-content
        // responses simply don't resolve the path (a safe no-op).
        config.FieldOverrides.Add(new MockFieldOverride
        {
            JsonPath = "$.condition",
            Value = JsonSerializer.SerializeToElement("stormy-override"),
        });
        config.FieldOverrides.Add(new MockFieldOverride
        {
            JsonPath = "$.temperature",
            Value = JsonSerializer.SerializeToElement(42),
        });
        return config;
    }

    [Fact]
    public async Task SchemaMock_With_MockConfig_Serves_Overridden_Fields()
    {
        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                SchemaPath = FixturePath,
                Port = 0,
                Watch = false,
                ReplaySpeed = 0,
                MockConfig = OverrideConfig(),
                SchemaSources = new IBowireMockSchemaSource[] { new OpenApiMockSchemaSource() },
            },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.GetAsync(
            new Uri($"http://127.0.0.1:{server.Port}/weather?location=hamburg"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        // The override wins over the schema-typed default ("sunny").
        Assert.Equal("stormy-override", root.GetProperty("condition").GetString());
        Assert.Equal(42, root.GetProperty("temperature").GetInt32());
        // A field the config didn't touch is still schema-generated.
        Assert.Equal(JsonValueKind.String, root.GetProperty("recordedAt").ValueKind);
    }

    [Fact]
    public async Task SchemaMock_Without_MockConfig_Serves_Schema_Default()
    {
        // Control: same fixture, no config → the schema-typed default stands.
        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                SchemaPath = FixturePath,
                Port = 0,
                Watch = false,
                ReplaySpeed = 0,
                SchemaSources = new IBowireMockSchemaSource[] { new OpenApiMockSchemaSource() },
            },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient();
        var resp = await http.GetAsync(
            new Uri($"http://127.0.0.1:{server.Port}/weather?location=hamburg"), TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("sunny", json.RootElement.GetProperty("condition").GetString());
    }
}
