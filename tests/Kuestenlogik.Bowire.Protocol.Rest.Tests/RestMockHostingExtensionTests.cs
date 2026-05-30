// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Protocol.Rest.Mock;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// Coverage for the REST plugin's mock-host extension — verifies the
/// pure helpers (format detection, JSON↔YAML conversion) and the
/// endpoint-mapping shape. Live mock-server wiring is exercised by the
/// integration suite; here we pin the unit-level contract.
/// </summary>
public sealed class RestMockHostingExtensionTests
{
    [Fact]
    public void Id_is_rest() => Assert.Equal("rest", new RestMockHostingExtension().Id);

    [Theory]
    [InlineData("openapi-3.0", true)]
    [InlineData("openapi-2.0", true)]
    [InlineData("OPENAPI-3.0", true)]   // case-insensitive
    [InlineData("asyncapi-3.0", false)]
    [InlineData("graphql-sdl", false)]
    [InlineData("", false)]
    public void IsOpenApi_recognises_openapi_format_tags(string format, bool expected)
        => Assert.Equal(expected, RestMockHostingExtension.IsOpenApi(format));

    [Theory]
    [InlineData("{\"openapi\":\"3.0.0\"}", true)]
    [InlineData("[1,2,3]", true)]
    [InlineData("openapi: 3.0.0", false)]
    [InlineData("   {\"a\":1}", true)]    // leading whitespace OK
    [InlineData("", false)]
    public void LooksLikeJson_distinguishes_json_from_yaml(string content, bool expected)
        => Assert.Equal(expected, RestMockHostingExtension.LooksLikeJson(content));

    [Fact]
    public void YamlToJson_roundtrips_simple_doc()
    {
        const string yaml = "openapi: 3.0.0\ninfo:\n  title: Test\n  version: 1.0.0\n";
        var json = RestMockHostingExtension.YamlToJson(yaml);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("3.0.0", doc.RootElement.GetProperty("openapi").GetString());
        Assert.Equal("Test", doc.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public void JsonToYaml_roundtrips_simple_doc()
    {
        const string json = "{\"openapi\":\"3.0.0\",\"info\":{\"title\":\"Test\",\"version\":\"1.0.0\"}}";
        var yaml = RestMockHostingExtension.JsonToYaml(json);
        Assert.Contains("openapi:", yaml);
        Assert.Contains("title:", yaml);
        Assert.Contains("Test", yaml);
    }

    [Fact]
    public void MapEndpoints_noop_when_recording_has_no_source_schema()
    {
        // No SourceSchema set → MapEndpoints should be a silent no-op.
        // We construct a dummy IEndpointRouteBuilder via a fake; if the
        // extension tried to call MapGet on a null builder it would
        // throw, so the test passes by not throwing.
        var ext = new RestMockHostingExtension();
        var recording = new BowireRecording { Id = "r", Name = "n" };
        // Use a real endpoints builder via a minimal WebApplication —
        // overkill for this assertion but it's the cheapest way to
        // get a real IEndpointRouteBuilder.
        var app = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder().Build();
        ext.MapEndpoints(app, recording); // should not throw
    }

    [Fact]
    public void MapEndpoints_noop_when_format_is_not_openapi()
    {
        var ext = new RestMockHostingExtension();
        var recording = new BowireRecording
        {
            Id = "r", Name = "n",
            SourceSchema = new RecordingSourceSchema("asyncapi-3.0", "asyncapi: 3.0.0\n")
        };
        var app = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder().Build();
        ext.MapEndpoints(app, recording); // should not throw, but also should not map anything
    }
}
