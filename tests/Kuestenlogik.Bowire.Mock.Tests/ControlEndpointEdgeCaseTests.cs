// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Less-travelled branches of the runtime control surface
/// (<c>/__bowire/mock/*</c>) and the in-memory mounting overload
/// of <c>UseBowireMock</c>.
/// </summary>
public sealed class ControlEndpointEdgeCaseTests : IDisposable
{
    private const string Token = "secret-edge";
    private readonly string _tempDir;

    public ControlEndpointEdgeCaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-control-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static BowireRecording SimpleRecording() => new()
    {
        Id = "rec_inmem",
        Name = "in-memory",
        RecordingFormatVersion = 2,
        Steps =
        {
            new BowireRecordingStep
            {
                Id = "step_one",
                Protocol = "rest",
                Service = "S",
                Method = "M",
                MethodType = "Unary",
                HttpPath = "/probe",
                HttpVerb = "GET",
                Status = "OK",
                Response = "ok"
            }
        }
    };

    private static IHost BuildInMemoryHost(string token, BowireRecording recording)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer().Configure(app =>
                {
                    app.UseBowireMock(recording, opts =>
                    {
                        opts.Watch = false;
                        opts.ControlToken = token;
                        opts.PassThroughOnMiss = false;
                    });
                });
            })
            .Start();
    }

    [Fact]
    public async Task ControlEndpoint_UnknownPathBelowPrefix_Returns404()
    {
        using var host = BuildInMemoryHost(Token, SimpleRecording());
        var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/__bowire/mock/totally-made-up");
        req.Headers.Add("X-Bowire-Mock-Token", Token);
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Unknown mock control endpoint", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScenarioSwitch_MalformedJsonBody_Returns400()
    {
        using var host = BuildInMemoryHost(Token, SimpleRecording());
        var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/__bowire/mock/scenario")
        {
            Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Bowire-Mock-Token", Token);
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Invalid JSON", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScenarioSwitch_NoBaseFile_RejectsBecauseMountedInMemory()
    {
        // The in-memory overload of UseBowireMock doesn't track a path, so
        // the scenario endpoint can't resolve a base directory and bails
        // out cleanly with the "wasn't mounted from a file path" message.
        using var host = BuildInMemoryHost(Token, SimpleRecording());
        var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/__bowire/mock/scenario")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Bowire-Mock-Token", Token);
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("file path", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScenarioSwitch_PathButInMemoryMount_RejectsForBaseDir()
    {
        // Even when the user supplies a relative path, the in-memory mount
        // can't resolve a base directory. Different error message than the
        // missing-path case so the user can tell which guard fired.
        using var host = BuildInMemoryHost(Token, SimpleRecording());
        var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/__bowire/mock/scenario")
        {
            Content = new StringContent("{\"path\":\"sibling.json\"}", Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Bowire-Mock-Token", Token);
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("base directory", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScenarioSwitch_RelativePathToNonExistentFile_ReturnsBadRequest()
    {
        // File-not-found while loading the new recording is mapped to a
        // 400 with a message rather than letting an exception propagate.
        var initial = WriteRecording("a.json", "/probe", "happy");
        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = initial, Port = 0, Watch = false, ReplaySpeed = 0, ControlToken = Token },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/__bowire/mock/scenario")
        {
            Content = new StringContent("{\"path\":\"missing.json\"}", Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Bowire-Mock-Token", Token);
        var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Status_AfterScenarioSwitch_ReportsNewSelectName()
    {
        // Confirms the response of the scenario endpoint is the same
        // status payload as the GET /__bowire/mock/status — branching
        // through HandleScenarioSwitchAsync's tail.
        var initial = WriteRecording("a.json", "/probe", "first");
        WriteRecording("b.json", "/probe", "second");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = initial, Port = 0, Watch = false, ReplaySpeed = 0, ControlToken = Token },
            TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/__bowire/mock/scenario")
        {
            Content = new StringContent("{\"path\":\"b.json\"}", Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Bowire-Mock-Token", Token);
        var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(1, json.RootElement.GetProperty("recording").GetProperty("stepCount").GetInt32());
    }

    [Fact]
    public async Task ControlEndpoint_StatusWithWrongMethod_Returns404()
    {
        // GET is the only verb the status endpoint answers; POST falls
        // through to the unknown-endpoint reply.
        using var host = BuildInMemoryHost(Token, SimpleRecording());
        var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/__bowire/mock/status")
        {
            Content = new StringContent("", Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Bowire-Mock-Token", Token);
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private string WriteRecording(string fileName, string httpPath, string responseBody)
    {
        var recording = new
        {
            id = "rec_" + fileName,
            name = fileName,
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_" + fileName,
                    protocol = "rest",
                    service = "S",
                    method = "M",
                    methodType = "Unary",
                    httpPath,
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
}
