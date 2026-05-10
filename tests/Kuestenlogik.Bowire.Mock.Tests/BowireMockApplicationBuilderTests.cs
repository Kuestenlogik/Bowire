// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Path-based <c>UseBowireMock</c> overload — exercises the file-watcher
/// branch (Watch=true) and verifies disk-edits propagate through the
/// <see cref="MockHandler"/> via the watcher's onReload callback.
/// </summary>
public sealed class BowireMockApplicationBuilderTests : IDisposable
{
    private readonly string _tempDir;

    public BowireMockApplicationBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-applib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private string WriteRecording(string fileName, string responseBody)
    {
        var recording = new
        {
            id = "rec",
            name = "rec",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step",
                    protocol = "rest",
                    service = "S",
                    method = "M",
                    methodType = "Unary",
                    httpPath = "/probe",
                    httpVerb = "GET",
                    status = "OK",
                    response = responseBody
                }
            }
        };
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(recording));
        return path;
    }

    private static IHost StartHost(string recordingPath, bool watch, bool passThroughOnMiss)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer().Configure(app =>
                {
                    app.UseBowireMock(recordingPath, opts =>
                    {
                        opts.Watch = watch;
                        opts.PassThroughOnMiss = passThroughOnMiss;
                        opts.Logger = NullLogger.Instance;
                    });
                });
            })
            .Start();
    }

    [Fact]
    public async Task PathOverload_WatchTrue_ReloadsRecordingOnDiskEdit()
    {
        var path = WriteRecording("rec.json", "first");

        using var host = StartHost(path, watch: true, passThroughOnMiss: false);

        var client = host.GetTestClient();
        var first = await client.GetStringAsync(new Uri("/probe", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal("first", first);

        // Rewrite the file and wait for the watcher to publish the new
        // recording. The watcher debounces internally; poll the
        // endpoint up to 5 s for the swap.
        WriteRecording("rec.json", "second");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        string body = first;
        while (DateTime.UtcNow < deadline)
        {
            body = await client.GetStringAsync(new Uri("/probe", UriKind.Relative), TestContext.Current.CancellationToken);
            if (body == "second") break;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.Equal("second", body);
    }

    [Fact]
    public async Task PathOverload_WatchFalse_StillServesInitialRecording()
    {
        var path = WriteRecording("static.json", "static-payload");

        using var host = StartHost(path, watch: false, passThroughOnMiss: false);

        var client = host.GetTestClient();
        var resp = await client.GetAsync(new Uri("/probe", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("static-payload", body);
    }

    [Fact]
    public async Task PathOverload_RecordingMiss_FallsBackTo404WhenPassThroughOff()
    {
        var path = WriteRecording("only-probe.json", "ok");
        using var host = StartHost(path, watch: false, passThroughOnMiss: false);

        var client = host.GetTestClient();
        var resp = await client.GetAsync(new Uri("/missing", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
