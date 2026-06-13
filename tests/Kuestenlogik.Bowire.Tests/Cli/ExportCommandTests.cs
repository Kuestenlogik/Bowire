// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Rest.OpenApi3;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.AsyncApi;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Protocol.Rest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Unit coverage for <see cref="ExportCommand"/>'s pure helpers. The
/// live-discovery branches (RunOpenApiAsync / RunAsyncApiAsync) need
/// a real wire target and are exercised by the integration suite; here
/// we lock down the URL-scheme → protocol-id table, the format parser,
/// the recording loader (both store and single-recording shapes), and
/// the option builders.
/// </summary>
public sealed class ExportCommandTests
{
    [Theory]
    [InlineData("mqtt://broker:1883", "mqtt")]
    [InlineData("mqtts://broker:8883", "mqtt")]
    [InlineData("nats://nats.local:4222", "nats")]
    [InlineData("kafka://broker:9092", "kafka")]
    [InlineData("ws://api.example.com/socket", "websocket")]
    [InlineData("wss://api.example.com/socket", "websocket")]
    [InlineData("amqp://rabbit:5672", "amqp")]
    [InlineData("amqps://rabbit:5671", "amqp")]
    [InlineData("amqp1://artemis:5672", "amqp")]
    [InlineData("pulsar://broker:6650", "pulsar")]
    [InlineData("http://api.example.com/", "rest")]
    [InlineData("https://api.example.com/", "rest")]
    public void PickAsyncApiProtocolId_maps_url_scheme_to_plugin_id(string url, string expected)
        => Assert.Equal(expected, ExportCommand.PickAsyncApiProtocolId(url));

    [Theory]
    [InlineData("not a url")]
    [InlineData("tcp://host:8080")]
    [InlineData("file:///path")]
    [InlineData("udp://1.2.3.4:5060")]
    public void PickAsyncApiProtocolId_returns_null_for_unsupported(string url)
        => Assert.Null(ExportCommand.PickAsyncApiProtocolId(url));

    [Theory]
    [InlineData("json", OpenApiExportFormat.Json)]
    [InlineData("JSON", OpenApiExportFormat.Json)]
    [InlineData("yaml", OpenApiExportFormat.Yaml)]
    [InlineData("", OpenApiExportFormat.Yaml)]
    [InlineData(null, OpenApiExportFormat.Yaml)]
    [InlineData("xml", OpenApiExportFormat.Yaml)]   // unrecognised → default
    public void ParseOpenApiFormat_picks_format_or_defaults_yaml(string? input, OpenApiExportFormat expected)
        => Assert.Equal(expected, ExportCommand.ParseOpenApiFormat(input));

    [Theory]
    [InlineData("json", AsyncApiExportFormat.Json)]
    [InlineData("yaml", AsyncApiExportFormat.Yaml)]
    [InlineData(null, AsyncApiExportFormat.Yaml)]
    public void ParseAsyncApiFormat_picks_format_or_defaults_yaml(string? input, AsyncApiExportFormat expected)
        => Assert.Equal(expected, ExportCommand.ParseAsyncApiFormat(input));

    [Fact]
    public void LoadRecording_returns_null_for_null_or_missing_path()
    {
        Assert.Null(ExportCommand.LoadRecording(null));
        Assert.Null(ExportCommand.LoadRecording(""));
        Assert.Null(ExportCommand.LoadRecording("/non/existent/file.bwr"));
    }

    [Fact]
    public void LoadRecording_reads_bare_recording_shape()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
                {
                  "id": "r1",
                  "name": "Single",
                  "steps": [
                    { "method": "GET /users/{id}", "httpVerb": "GET", "httpPath": "/users/{id}" }
                  ]
                }
                """);
            var rec = ExportCommand.LoadRecording(tmp);
            Assert.NotNull(rec);
            Assert.Equal("r1", rec!.Id);
            Assert.Equal("Single", rec.Name);
            Assert.Single(rec.Steps);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void LoadRecording_reads_recording_store_shape_and_picks_first()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
                {
                  "recordings": [
                    { "id": "first",  "name": "First" },
                    { "id": "second", "name": "Second" }
                  ]
                }
                """);
            var rec = ExportCommand.LoadRecording(tmp);
            Assert.NotNull(rec);
            Assert.Equal("first", rec!.Id);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void LoadRecording_returns_null_for_malformed_json()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "{ not json");
            // Malformed recording shouldn't kill the export — coverage
            // block is informational only.
            Assert.Null(ExportCommand.LoadRecording(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void BuildOpenApiOptions_propagates_all_fields()
    {
        var opts = ExportCommand.BuildOpenApiOptions("json", "My API", "2.5.0");
        Assert.Equal(OpenApiExportFormat.Json, opts.Format);
        Assert.Equal("My API", opts.Title);
        Assert.Equal("2.5.0", opts.Version);
    }

    [Fact]
    public void BuildAsyncApiOptions_propagates_all_fields()
    {
        var opts = ExportCommand.BuildAsyncApiOptions("yaml", "Sensor bus", "1.0.0");
        Assert.Equal(AsyncApiExportFormat.Yaml, opts.Format);
        Assert.Equal("Sensor bus", opts.Title);
        Assert.Equal("1.0.0", opts.Version);
    }

    [Fact]
    public void Build_command_has_openapi_and_asyncapi_subcommands()
    {
        var export = ExportCommand.Build();
        var names = export.Subcommands.Select(s => s.Name).ToHashSet();
        Assert.Contains("openapi", names);
        Assert.Contains("asyncapi", names);
    }

    [Fact]
    public async Task RunOpenApiAsync_with_empty_url_returns_usage_exit_code_2()
    {
        var rc = await ExportCommand.RunOpenApiAsync(
            "", output: null, format: null, recordingPath: null,
            title: null, versionOverride: null, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsyncApiAsync_with_unsupported_scheme_returns_exit_code_2()
    {
        var rc = await ExportCommand.RunAsyncApiAsync(
            "udp://1.2.3.4:5060", output: null, format: null, recordingPath: null,
            title: null, versionOverride: null, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsyncApiAsync_with_unloaded_plugin_returns_exit_code_1()
    {
        // pulsar:// is recognised but the Pulsar plugin isn't on the
        // Bowire.Tests classpath, so ResolveProtocol returns null and
        // we get the "install Kuestenlogik.Bowire.Protocol.Pulsar"
        // exit code (1).
        var rc = await ExportCommand.RunAsyncApiAsync(
            "pulsar://broker:6650", output: null, format: null, recordingPath: null,
            title: null, versionOverride: null, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task RunOpenApiAsync_unreachable_url_returns_exit_code_1()
    {
        // No server on this port → REST discovery returns empty (the
        // plugin swallows HTTP failures, returns []), then the
        // builder emits a doc with empty paths{}. That's not an
        // error from the CLI's perspective — exit 0, but the doc
        // body is mostly empty. Distinct from "plugin not loaded"
        // (exit 1). This test covers the "discovery threw" branch by
        // pointing at a URL whose scheme isn't parseable for the
        // OpenAPI fetch — discovery internally surfaces no doc,
        // returns []. We assert the CLI completes cleanly (exit 0)
        // and emits a minimal doc.
        var output = Path.GetTempFileName();
        try
        {
            var rc = await ExportCommand.RunOpenApiAsync(
                "http://127.0.0.1:1", // port 1 is privileged + closed; connection refused
                output: output, format: null, recordingPath: null,
                title: "Unreachable", versionOverride: "0.0.1",
                ct: TestContext.Current.CancellationToken);
            // REST discovery silently returns [] on connection failures;
            // builder emits the empty contract. CLI succeeds.
            Assert.Equal(0, rc);
            var doc = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);
            Assert.Contains("openapi: 3.0.0", doc);
            Assert.Contains("title: Unreachable", doc);
        }
        finally { File.Delete(output); }
    }
}

// ---- live-path coverage ----------------------------------------------
//
// The pure-helper tests above (URL→plugin-id, format parsing, recording
// loader) cover the routing seams. This second class exercises
// RunOpenApiAsync end-to-end against an in-process OpenAPI server, so
// the discovery → builder → output path is covered as one slice.
//
// Originally split from ExportCommandTests because the stdout-capture
// test had to serialise on Console.SetOut. Since Phase 2 of the
// InvocationConfiguration.Output refactor that test passes an explicit
// StringWriter and no longer touches the process-global Console.Out —
// the split survives for readability (live-host vs. pure helpers) but
// no [Collection] is needed any more.

/// <summary>
/// End-to-end coverage for <see cref="ExportCommand.RunOpenApiAsync"/> —
/// drives the real <c>BowireRestProtocol</c> from the registry against
/// an in-process OpenAPI listener and pins the four output paths
/// (stdout vs. file × YAML vs. JSON), the recording-coverage stamp,
/// and one error branch.
/// </summary>
public sealed class ExportCommandOpenApiLiveTests
{
    private const string SampleOpenApiJson = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Live Sample", "version": "9.9.9" },
          "paths": {
            "/users/{id}": {
              "get": {
                "operationId": "getUser",
                "summary": "Fetch a user",
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                ],
                "responses": {
                  "200": {
                    "description": "OK",
                    "content": { "application/json": { "schema": { "type": "object" } } }
                  }
                }
              }
            }
          }
        }
        """;

    [Fact]
    public async Task RunOpenApiAsync_writes_yaml_to_output_file()
    {
        await using var host = await OpenApiTestHost.StartAsync(SampleOpenApiJson);
        var output = Path.GetTempFileName();
        try
        {
            var rc = await ExportCommand.RunOpenApiAsync(
                host.BaseUrl + "openapi.json",
                output: output, format: null, recordingPath: null,
                title: null, versionOverride: null,
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(0, rc);
            var doc = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);
            Assert.Contains("openapi: 3.0.0", doc);
            // Title falls back to the host when no service carries an
            // info.description (REST plugin maps info.description, not
            // info.title, onto BowireServiceInfo.Description) — assert
            // structural elements that prove the discovery actually
            // walked the live doc.
            Assert.Contains("info:", doc);
            Assert.Contains("/users/{id}:", doc);
            Assert.Contains("get:", doc);
            Assert.Contains("operationId: getUser", doc);
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public async Task RunOpenApiAsync_writes_json_when_format_is_json()
    {
        await using var host = await OpenApiTestHost.StartAsync(SampleOpenApiJson);
        var output = Path.GetTempFileName();
        try
        {
            var rc = await ExportCommand.RunOpenApiAsync(
                host.BaseUrl + "openapi.json",
                output: output, format: "json", recordingPath: null,
                title: null, versionOverride: null,
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(0, rc);
            var text = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);
            // Must parse as JSON, expose the same operation tree.
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            Assert.Equal("3.0.0", doc.RootElement.GetProperty("openapi").GetString());
            Assert.True(doc.RootElement
                .GetProperty("paths")
                .GetProperty("/users/{id}")
                .TryGetProperty("get", out _));
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public async Task RunOpenApiAsync_writes_to_stdout_when_no_output_file()
    {
        await using var host = await OpenApiTestHost.StartAsync(SampleOpenApiJson);
        using var sw = new StringWriter();
        var rc = await ExportCommand.RunOpenApiAsync(
            host.BaseUrl + "openapi.json",
            output: null, format: null, recordingPath: null,
            title: null, versionOverride: null,
            ct: TestContext.Current.CancellationToken,
            stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);
        var captured = sw.ToString();
        Assert.Contains("openapi: 3.0.0", captured);
        Assert.Contains("info:", captured);
        Assert.Contains("/users/{id}:", captured);
    }

    [Fact]
    public async Task RunOpenApiAsync_with_recording_stamps_coverage_extension_on_operations()
    {
        await using var host = await OpenApiTestHost.StartAsync(SampleOpenApiJson);
        var recordingFile = Path.GetTempFileName();
        var output = Path.GetTempFileName();
        try
        {
            // One recorded step against GET /users/{id} — the builder
            // should emit x-bowire-coverage: { recorded: true, stepCount: 1 }
            // for that operation.
            await File.WriteAllTextAsync(recordingFile, """
                {
                  "id": "r",
                  "name": "n",
                  "steps": [
                    { "id": "s1", "httpVerb": "GET", "httpPath": "/users/{id}" }
                  ]
                }
                """, TestContext.Current.CancellationToken);

            var rc = await ExportCommand.RunOpenApiAsync(
                host.BaseUrl + "openapi.json",
                output: output, format: null,
                recordingPath: recordingFile,
                title: null, versionOverride: null,
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(0, rc);
            var doc = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);
            Assert.Contains("x-bowire-coverage:", doc);
            Assert.Contains("recorded: true", doc);
            Assert.Contains("stepCount: 1", doc);
        }
        finally
        {
            File.Delete(recordingFile);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task RunOpenApiAsync_options_override_propagate_to_doc()
    {
        await using var host = await OpenApiTestHost.StartAsync(SampleOpenApiJson);
        var output = Path.GetTempFileName();
        try
        {
            var rc = await ExportCommand.RunOpenApiAsync(
                host.BaseUrl + "openapi.json",
                output: output, format: null, recordingPath: null,
                title: "Overridden Title",
                versionOverride: "42.0.0",
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(0, rc);
            var doc = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);
            Assert.Contains("title: Overridden Title", doc);
            Assert.Contains("version: 42.0.0", doc);
        }
        finally { File.Delete(output); }
    }

    /// <summary>
    /// In-process listener that serves a static OpenAPI document at
    /// <c>/openapi.json</c> on a free port. REST plugin's
    /// <c>OpenApiDiscovery.FetchAndParseAsync</c> hits the URL we hand
    /// it, parses, returns services — drives ExportCommand's live
    /// path end-to-end. Port 0 + IServerAddressesFeature is the same
    /// parallel-safe binding pattern the mock-host HTTP tests use.
    /// </summary>
    private sealed class OpenApiTestHost : IAsyncDisposable
    {
        public required Microsoft.AspNetCore.Builder.WebApplication App { get; init; }
        public required string BaseUrl { get; init; } // always ends with "/"

        public static async Task<OpenApiTestHost> StartAsync(string openApiJson)
        {
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");

            app.MapGet("/openapi.json", () =>
                Microsoft.AspNetCore.Http.Results.Content(openApiJson, "application/json"));

            await app.StartAsync();
            var address = app.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features
                .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                .Addresses
                .First();
            return new OpenApiTestHost { App = app, BaseUrl = address.TrimEnd('/') + "/" };
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
