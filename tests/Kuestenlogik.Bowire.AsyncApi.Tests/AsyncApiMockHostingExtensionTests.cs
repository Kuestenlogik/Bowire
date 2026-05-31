// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    // ---- HTTP-level coverage --------------------------------------
    //
    // Sibling of the REST hosting-extension HTTP tests — spins a
    // real Kestrel listener on Port 0, calls MapEndpoints against
    // it, drives the mapped routes with a real HttpClient. Pins the
    // /asyncapi.{yaml,yml,json} surface end-to-end including JSON↔
    // YAML conversion on the wrong-format request paths.

    [Fact]
    public async Task GET_asyncapi_yaml_returns_source_content_when_format_is_yaml()
    {
        const string yaml = "asyncapi: 3.0.0\ninfo:\n  title: Sensors\n  version: 1.0.0\n";
        using var host = await TestHost.StartAsync(new RecordingSourceSchema("asyncapi-3.0", yaml));
        var body = await host.Http.GetStringAsync(
            new Uri("/asyncapi.yaml", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(yaml, body);
    }

    [Fact]
    public async Task GET_asyncapi_yml_alias_returns_source_content_with_yaml_mime_type()
    {
        const string yaml = "asyncapi: 3.0.0\n";
        using var host = await TestHost.StartAsync(new RecordingSourceSchema("asyncapi-3.0", yaml));
        using var resp = await host.Http.GetAsync(
            new Uri("/asyncapi.yml", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/yaml", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GET_asyncapi_json_converts_yaml_source_on_the_fly()
    {
        const string yaml = "asyncapi: 3.0.0\ninfo:\n  title: Converted\n";
        using var host = await TestHost.StartAsync(new RecordingSourceSchema("asyncapi-3.0", yaml));
        var body = await host.Http.GetStringAsync(
            new Uri("/asyncapi.json", UriKind.Relative),
            TestContext.Current.CancellationToken);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("3.0.0", doc.RootElement.GetProperty("asyncapi").GetString());
        Assert.Equal("Converted", doc.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public async Task GET_asyncapi_yaml_converts_json_source_on_the_fly()
    {
        const string json = """{"asyncapi":"3.0.0","info":{"title":"FromJson","version":"1.0.0"}}""";
        using var host = await TestHost.StartAsync(new RecordingSourceSchema("asyncapi-3.0", json));
        var body = await host.Http.GetStringAsync(
            new Uri("/asyncapi.yaml", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Contains("asyncapi:", body);
        Assert.Contains("FromJson", body);
    }

    [Fact]
    public async Task GET_asyncapi_yaml_yields_404_when_no_source_schema()
    {
        using var host = await TestHost.StartAsync(sourceSchema: null);
        using var resp = await host.Http.GetAsync(
            new Uri("/asyncapi.yaml", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GET_asyncapi_yaml_yields_404_when_format_is_openapi()
    {
        // Wrong-format guard — an OpenAPI source schema means the
        // AsyncAPI hosting extension no-ops; nothing is mapped, the
        // 404 middleware answers.
        using var host = await TestHost.StartAsync(
            new RecordingSourceSchema("openapi-3.0", "openapi: 3.0.0\n"));
        using var resp = await host.Http.GetAsync(
            new Uri("/asyncapi.yaml", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>
    /// Real Kestrel listener bound to a random free port (Port 0 +
    /// IServerAddressesFeature). Same pattern as REST's TestHost.
    /// </summary>
    private sealed class TestHost : IAsyncDisposable, IDisposable
    {
        public required Microsoft.AspNetCore.Builder.WebApplication App { get; init; }
        public required HttpClient Http { get; init; }

        public static async Task<TestHost> StartAsync(RecordingSourceSchema? sourceSchema)
        {
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");

            var recording = new BowireRecording
            {
                Id = "test-recording",
                Name = "test",
                SourceSchema = sourceSchema,
            };
            new AsyncApiMockHostingExtension().MapEndpoints(app, recording);

            await app.StartAsync();
            var address = app.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features
                .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                .Addresses
                .First();

            var http = new HttpClient { BaseAddress = new Uri(address) };
            return new TestHost { App = app, Http = http };
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }

        public void Dispose()
            => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
