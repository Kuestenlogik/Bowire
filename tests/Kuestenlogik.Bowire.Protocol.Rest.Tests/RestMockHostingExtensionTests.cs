// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Protocol.Rest.Mock;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    // ---- HTTP-level coverage --------------------------------------
    //
    // The next set spins the extension up against a real Kestrel
    // listener (Port 0 = OS picks a free port, then read it back from
    // IServerAddressesFeature). Verifies that:
    //   * Each mapped endpoint actually responds 200 with the right
    //     content + content-type.
    //   * JSON↔YAML on-the-fly conversion happens for the wrong-
    //     format request paths.
    //   * Recordings without an OpenAPI source schema produce 404
    //     because nothing was mapped.

    [Fact]
    public async Task GET_openapi_yaml_returns_source_content_when_format_is_yaml()
    {
        const string yaml = "openapi: 3.0.0\ninfo:\n  title: Live\n  version: 1.0.0\n";
        using var host = await TestHost.StartAsync(new RecordingSourceSchema("openapi-3.0", yaml));
        var body = await host.Http.GetStringAsync(new Uri("/openapi.yaml", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(yaml, body);
    }

    [Fact]
    public async Task GET_openapi_yml_alias_returns_source_content()
    {
        const string yaml = "openapi: 3.0.0\n";
        using var host = await TestHost.StartAsync(new RecordingSourceSchema("openapi-3.0", yaml));
        using var resp = await host.Http.GetAsync(new Uri("/openapi.yml", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/yaml", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GET_openapi_json_converts_yaml_source_on_the_fly()
    {
        const string yaml = "openapi: 3.0.0\ninfo:\n  title: Converted\n";
        using var host = await TestHost.StartAsync(new RecordingSourceSchema("openapi-3.0", yaml));
        var body = await host.Http.GetStringAsync(new Uri("/openapi.json", UriKind.Relative), TestContext.Current.CancellationToken);
        // Validates the YamlToJson path: result must parse as JSON
        // and carry the same openapi version + title fields.
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("3.0.0", doc.RootElement.GetProperty("openapi").GetString());
        Assert.Equal("Converted", doc.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public async Task GET_openapi_yaml_converts_json_source_on_the_fly()
    {
        const string json = """{"openapi":"3.0.0","info":{"title":"FromJson","version":"1.0.0"}}""";
        using var host = await TestHost.StartAsync(new RecordingSourceSchema("openapi-3.0", json));
        var body = await host.Http.GetStringAsync(new Uri("/openapi.yaml", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Contains("openapi:", body);
        Assert.Contains("FromJson", body);
    }

    [Fact]
    public async Task GET_swagger_json_serves_the_same_content_as_openapi_json()
    {
        const string json = """{"openapi":"3.0.0","info":{"title":"X"}}""";
        using var host = await TestHost.StartAsync(new RecordingSourceSchema("openapi-3.0", json));
        var a = await host.Http.GetStringAsync(new Uri("/openapi.json", UriKind.Relative), TestContext.Current.CancellationToken);
        var b = await host.Http.GetStringAsync(new Uri("/swagger.json", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task GET_openapi_json_yields_404_when_no_source_schema()
    {
        // No SourceSchema on the recording → MapEndpoints skips every
        // route → ASP.NET's terminal 404 middleware answers.
        using var host = await TestHost.StartAsync(sourceSchema: null);
        using var resp = await host.Http.GetAsync(new Uri("/openapi.json", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GET_openapi_json_yields_404_when_format_is_asyncapi()
    {
        // Wrong-format guard inside MapEndpoints — AsyncAPI source
        // schema means the REST hosting extension no-ops; same 404 as
        // the no-schema case.
        using var host = await TestHost.StartAsync(
            new RecordingSourceSchema("asyncapi-3.0", "asyncapi: 3.0.0\n"));
        using var resp = await host.Http.GetAsync(new Uri("/openapi.json", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>
    /// Spins a real Kestrel listener bound to a random free port so
    /// the hosting extension's mapped endpoints can be hit over real
    /// HTTP. Port 0 + IServerAddressesFeature is the standard trick
    /// for parallel-safe in-process tests — no port-collision races.
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
            // Port 0 → Kestrel asks the OS for a free port, we read
            // the actual bound address back after StartAsync. Parallel-
            // safe; no port-collision races.
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");

            var recording = new BowireRecording
            {
                Id = "test-recording",
                Name = "test",
                SourceSchema = sourceSchema,
            };
            new RestMockHostingExtension().MapEndpoints(app, recording);

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
        {
            // xunit calls Dispose() on `using var` instances; route to
            // the async version so the WebApplication shuts down cleanly.
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
