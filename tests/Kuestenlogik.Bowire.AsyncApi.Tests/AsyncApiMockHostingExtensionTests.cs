// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// Coverage for the AsyncAPI plugin's mock-host extension — pins the
/// pure helpers (format detection, JSON↔YAML conversion) and the
/// silent-no-op shape when there's no source schema or the format
/// doesn't match. Live wiring against a hosted mock-server is left
/// to the integration suite; here we lock down the unit contract.
/// </summary>
public sealed class AsyncApiMockHostingExtensionTests
{
    [Fact]
    public void Id_is_asyncapi() => Assert.Equal("asyncapi", new AsyncApiMockHostingExtension().Id);

    [Theory]
    [InlineData("asyncapi-3.0", true)]
    [InlineData("asyncapi-2.6", true)]
    [InlineData("ASYNCAPI-3.0", true)]
    [InlineData("openapi-3.0", false)]
    [InlineData("graphql-sdl", false)]
    [InlineData("", false)]
    public void IsAsyncApi_recognises_asyncapi_format_tags(string format, bool expected)
        => Assert.Equal(expected, AsyncApiMockHostingExtension.IsAsyncApi(format));

    [Theory]
    [InlineData("{\"asyncapi\":\"3.0.0\"}", true)]
    [InlineData("asyncapi: 3.0.0", false)]
    [InlineData("   {\"a\":1}", true)]
    [InlineData("", false)]
    public void LooksLikeJson_distinguishes_json_from_yaml(string content, bool expected)
        => Assert.Equal(expected, AsyncApiMockHostingExtension.LooksLikeJson(content));

    [Fact]
    public void YamlToJson_roundtrips_simple_asyncapi_doc()
    {
        const string yaml = """
            asyncapi: 3.0.0
            info:
              title: Sensor bus
              version: 1.0.0
            channels:
              temperature:
                address: sensors/temperature
            """;
        var json = AsyncApiMockHostingExtension.YamlToJson(yaml);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("3.0.0", doc.RootElement.GetProperty("asyncapi").GetString());
        Assert.Equal("Sensor bus", doc.RootElement.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("sensors/temperature",
            doc.RootElement.GetProperty("channels").GetProperty("temperature").GetProperty("address").GetString());
    }

    [Fact]
    public void JsonToYaml_roundtrips_simple_asyncapi_doc()
    {
        const string json = """
            {"asyncapi":"3.0.0","info":{"title":"Sensor bus","version":"1.0.0"}}
            """;
        var yaml = AsyncApiMockHostingExtension.JsonToYaml(json);
        Assert.Contains("asyncapi:", yaml);
        Assert.Contains("title:", yaml);
        Assert.Contains("Sensor bus", yaml);
    }

    [Fact]
    public void MapEndpoints_noop_when_recording_has_no_source_schema()
    {
        var ext = new AsyncApiMockHostingExtension();
        var recording = new BowireRecording { Id = "r", Name = "n" };
        var app = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder().Build();
        ext.MapEndpoints(app, recording); // must not throw
    }

    [Fact]
    public void MapEndpoints_noop_when_format_is_openapi()
    {
        var ext = new AsyncApiMockHostingExtension();
        var recording = new BowireRecording
        {
            Id = "r", Name = "n",
            SourceSchema = new RecordingSourceSchema("openapi-3.0", "openapi: 3.0.0\n")
        };
        var app = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder().Build();
        ext.MapEndpoints(app, recording); // OpenAPI is RestMockHostingExtension's job; this one no-ops
    }
}
