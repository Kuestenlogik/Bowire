// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.AsyncApi;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Protocol.Rest;

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
}
