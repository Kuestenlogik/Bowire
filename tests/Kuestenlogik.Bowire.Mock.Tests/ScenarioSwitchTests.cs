// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Phase 3e: runtime scenario switching via the control endpoint
/// <c>POST /__bowire/mock/scenario</c>. Exercises the auth gate, the
/// path-traversal guard, and the happy-path swap from a disk file to
/// a sibling file under the same directory.
/// </summary>
public sealed class ScenarioSwitchTests : IDisposable
{
    private const string Token = "test-secret-123";
    private readonly string _tempDir;

    public ScenarioSwitchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-mock-scenario-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Control_WhenTokenNotConfigured_Returns404()
    {
        var initial = WriteRecording("happy.json", "happy", "/ping", "pong-happy");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = initial, Port = 0, Watch = false, ReplaySpeed = 0 /* no token */ },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        var resp = await http.GetAsync(new Uri("/__bowire/mock/status", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Control_MissingToken_Returns401()
    {
        var initial = WriteRecording("happy.json", "happy", "/ping", "pong-happy");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = initial, Port = 0, Watch = false, ReplaySpeed = 0, ControlToken = Token },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        var resp = await http.GetAsync(new Uri("/__bowire/mock/status", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Status_WithValidToken_ReportsCurrentRecording()
    {
        var initial = WriteRecording("happy.json", "happy", "/ping", "pong-happy");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = initial, Port = 0, Watch = false, ReplaySpeed = 0, ControlToken = Token },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        using var req = new HttpRequestMessage(HttpMethod.Get, "/__bowire/mock/status");
        req.Headers.Add("X-Bowire-Mock-Token", Token);
        var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("happy", json.RootElement.GetProperty("recording").GetProperty("name").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("recording").GetProperty("stepCount").GetInt32());
    }

    [Fact]
    public async Task Scenario_Switch_LoadsSiblingFile_AndReplaysNewResponse()
    {
        var initial = WriteRecording("happy.json", "happy", "/ping", "pong-happy");
        WriteRecording("error.json", "error", "/ping", "pong-error");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = initial, Port = 0, Watch = false, ReplaySpeed = 0, ControlToken = Token },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };

        // Before: happy.json is active.
        var before = await http.GetStringAsync(new Uri("/ping", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Contains("pong-happy", before, StringComparison.Ordinal);

        // Swap to error.json via the control endpoint.
        using var req = new HttpRequestMessage(HttpMethod.Post, "/__bowire/mock/scenario")
        {
            Content = JsonContent.Create(new { path = "error.json" })
        };
        req.Headers.Add("X-Bowire-Mock-Token", Token);
        var swap = await http.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, swap.StatusCode);

        // After: error.json serves the same path with a different body.
        var after = await http.GetStringAsync(new Uri("/ping", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Contains("pong-error", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scenario_Switch_AbsolutePath_Rejected()
    {
        var initial = WriteRecording("happy.json", "happy", "/ping", "pong-happy");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = initial, Port = 0, Watch = false, ReplaySpeed = 0, ControlToken = Token },
            TestContext.Current.CancellationToken);

        // Path.IsPathRooted is platform-aware — `C:\…` is rooted on Windows
        // only, `/etc/…` is rooted on Unix only. Pick whichever the host OS
        // recognises so the guard actually fires.
        var absolutePath = OperatingSystem.IsWindows()
            ? "C:\\Windows\\System32\\drivers\\etc\\hosts"
            : "/etc/hosts";

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/__bowire/mock/scenario")
        {
            Content = JsonContent.Create(new { path = absolutePath })
        };
        req.Headers.Add("X-Bowire-Mock-Token", Token);

        var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("absolute", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scenario_Switch_PathTraversal_Rejected()
    {
        var initial = WriteRecording("happy.json", "happy", "/ping", "pong-happy");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = initial, Port = 0, Watch = false, ReplaySpeed = 0, ControlToken = Token },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/__bowire/mock/scenario")
        {
            Content = JsonContent.Create(new { path = "../../../some-other-file.json" })
        };
        req.Headers.Add("X-Bowire-Mock-Token", Token);

        var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("escape", body, StringComparison.OrdinalIgnoreCase);
    }

    // Writes a one-step recording that serves <responseBody> on a GET of <path>.
    private string WriteRecording(string fileName, string recordingName, string path, string responseBody)
    {
        var recording = new
        {
            id = "rec_" + recordingName,
            name = recordingName,
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_" + recordingName,
                    protocol = "rest",
                    service = "Ping",
                    method = "Ping",
                    methodType = "Unary",
                    httpPath = path,
                    httpVerb = "GET",
                    status = "OK",
                    response = responseBody
                }
            }
        };
        var filePath = Path.Combine(_tempDir, fileName);
        File.WriteAllText(filePath, JsonSerializer.Serialize(recording));
        return filePath;
    }
}
