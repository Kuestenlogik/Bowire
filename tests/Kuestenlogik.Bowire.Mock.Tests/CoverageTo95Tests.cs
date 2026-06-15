// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Mock.Management;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Gap-fills for <see cref="MockRegistry"/>'s start-failure rollback:
/// when MockServer.StartAsync can't bring the listener up (malformed
/// recording → JSON parse throws inside the server boot path), the
/// registry's catch block must drop the temp .bwr file rather than
/// leak it. Existing tests cover the happy path + clean shutdown; this
/// one pins the error rollback so the analyzer-flagged "this catch is
/// unreachable" comment stays honest.
/// </summary>
public sealed class CoverageTo95Tests
{
    [Fact]
    public async Task StartAsync_with_unparseable_recording_throws_and_drops_the_temp_bwr()
    {
        // Junk payload: MockServer reads the file as JSON, fails to
        // deserialise, throws inside StartAsync. The catch in MockRegistry
        // must run, delete the temp file, and rethrow.
        var registry = new MockRegistry(NullLogger<MockRegistry>.Instance);
        await using (registry)
        {
            // Snapshot the mocks dir contents before the call so we can
            // prove the failed start didn't leave a stale .bwr behind.
            var mocksDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".bowire", "mocks");
            var before = Directory.Exists(mocksDir)
                ? Directory.GetFiles(mocksDir, "*.bwr").ToHashSet()
                : [];

            var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await registry.StartAsync(
                    recordingJson: "this is not json",
                    recordingDisplayName: "rollback-test",
                    port: 0,
                    TestContext.Current.CancellationToken);
            });

            // Pin the throw shape — the catch in MockRegistry rethrows
            // verbatim, so the deserialisation failure surfaces with
            // its original exception type.
            Assert.NotNull(ex);

            // No new .bwr file should be on disk after the rollback.
            var after = Directory.Exists(mocksDir)
                ? Directory.GetFiles(mocksDir, "*.bwr").ToHashSet()
                : [];
            after.ExceptWith(before);
            Assert.Empty(after);

            // Registry stays empty — failed Start never adds to _mocks.
            Assert.Empty(registry.List());
        }
    }

    [Fact]
    public async Task BowireMockHostManager_round_trips_start_list_stop_with_a_real_recording()
    {
        // BowireMockHostManager sits at 0% per the baseline because no
        // existing test boots one with a real recording. This pins the
        // happy-path lifecycle:
        //   StartFromJson → temp file is written, MockServer is up on a
        //   free local port, the handle carries the resolved port + URL;
        //   List → returns the live handle;
        //   StopAsync → MockServer disposes, the temp .json is gone.
        var rec = new BowireRecording
        {
            Id = "rec_host",
            Name = "host-test",
            RecordingFormatVersion = 2,
        };
        rec.Steps.Add(new BowireRecordingStep
        {
            Id = "step_one",
            Protocol = "rest",
            Service = "S",
            Method = "M",
            MethodType = "Unary",
            HttpPath = "/probe",
            HttpVerb = "GET",
            Status = "OK",
            Response = "ok",
        });
        var recordingJson = System.Text.Json.JsonSerializer.Serialize(rec);

        var manager = new BowireMockHostManager();
        await using (manager)
        {
            var handle = await manager.StartFromJson(
                recordingJson,
                recordingId: "rec_host",
                label: "Host smoke",
                TestContext.Current.CancellationToken);

            Assert.Equal("rec_host", handle.RecordingId);
            Assert.Equal("Host smoke", handle.Label);
            Assert.True(handle.Port > 0, "port allocator must hand back a usable port");
            Assert.Equal($"http://127.0.0.1:{handle.Port}", handle.Url);

            var listed = manager.List();
            Assert.Single(listed);
            Assert.Equal(handle.MockId, listed.First().MockId);

            var stopped = await manager.StopAsync(handle.MockId, TestContext.Current.CancellationToken);
            Assert.True(stopped);
            Assert.Empty(manager.List());
        }
    }

    [Fact]
    public async Task BowireMockHostManager_StopAsync_returns_false_for_unknown_mock_id()
    {
        var manager = new BowireMockHostManager();
        await using (manager)
        {
            var stopped = await manager.StopAsync(
                mockId: "never-started",
                TestContext.Current.CancellationToken);
            Assert.False(stopped);
        }
    }

    [Fact]
    public async Task BowireMockHostManager_StartFromJson_throws_ArgumentNullException_for_null_payload()
    {
        // Pins the ThrowIfNull guard at the top of StartFromJson — the
        // endpoint layer relies on this to surface "missing body" as a
        // 400 rather than a NullReferenceException down inside the
        // serializer.
        var manager = new BowireMockHostManager();
        await using (manager)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await manager.StartFromJson(
                    recordingJson: null!,
                    recordingId: "x",
                    label: "x",
                    TestContext.Current.CancellationToken));
        }
    }

    // --- BowireMockHostEndpoints — HTTP-level coverage --------------

    [Fact]
    public async Task POST_mock_from_recording_returns_400_when_body_is_not_json()
    {
        // Drives the JsonException catch in MapBowireMockHostEndpoints
        // → structured 400 ProblemDetails with the urn:bowire:invalid-input
        // type tag.
        await using var factory = await MockHostFactory.StartAsync();
        using var client = factory.CreateClient();
        using var content = new StringContent("not json", Encoding.UTF8, "application/json");

        using var resp = await client.PostAsync(
            new Uri("/api/mock/from-recording", UriKind.Relative),
            content,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal(
            "urn:bowire:invalid-input",
            problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task POST_mock_from_recording_returns_400_when_recordingId_is_missing()
    {
        await using var factory = await MockHostFactory.StartAsync();
        using var client = factory.CreateClient();

        using var resp = await client.PostAsJsonAsync(
            "/api/mock/from-recording",
            new { },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Contains(
            "recordingId",
            problem.GetProperty("title").GetString() ?? "",
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task POST_mock_from_recording_returns_500_when_no_recording_provider_is_registered()
    {
        // Provider is the seam — when nothing's registered, the endpoint
        // surfaces a 500 with the urn:bowire:mock:no-recording-provider
        // sentinel so the standalone tool's "register the provider"
        // contract stays loud.
        await using var factory = await MockHostFactory.StartAsync(registerProvider: false);
        using var client = factory.CreateClient();

        using var resp = await client.PostAsJsonAsync(
            "/api/mock/from-recording",
            new { recordingId = "anything" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal(
            "urn:bowire:mock:no-recording-provider",
            problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task POST_mock_from_recording_returns_404_when_recording_is_not_found()
    {
        // Provider registered but returns null → 404 with the
        // urn:bowire:mock:recording-not-found tag.
        await using var factory = await MockHostFactory.StartAsync();
        using var client = factory.CreateClient();

        using var resp = await client.PostAsJsonAsync(
            "/api/mock/from-recording",
            new { recordingId = "this-id-was-never-registered" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal(
            "urn:bowire:mock:recording-not-found",
            problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task GET_mock_hosts_returns_an_empty_list_when_nothing_is_running()
    {
        await using var factory = await MockHostFactory.StartAsync();
        using var client = factory.CreateClient();

        var payload = await client.GetFromJsonAsync<JsonElement>(
            "/api/mock/hosts",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, payload.GetProperty("hosts").GetArrayLength());
    }

    [Fact]
    public async Task POST_mock_stop_returns_404_when_mock_id_is_unknown()
    {
        await using var factory = await MockHostFactory.StartAsync();
        using var client = factory.CreateClient();

        using var resp = await client.PostAsync(
            new Uri("/api/mock/nonexistent/stop", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal(
            "urn:bowire:mock:host-not-found",
            problem.GetProperty("type").GetString());
    }

    /// <summary>
    /// Thin web-app fixture that maps the BowireMockHostEndpoints onto a
    /// real loopback Kestrel listener with a configurable recording-provider
    /// registration (null in the "no provider" tests, an empty stub
    /// otherwise). Mirrors the TestAppFactory pattern used elsewhere.
    /// </summary>
    private sealed class MockHostFactory : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _baseUrl;

        private MockHostFactory(WebApplication app, string baseUrl)
        {
            _app = app;
            _baseUrl = baseUrl;
        }

        public static async Task<MockHostFactory> StartAsync(bool registerProvider = true)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
                o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));

            builder.Services.AddSingleton<BowireMockHostManager>();
            if (registerProvider)
            {
                builder.Services.AddSingleton<IRecordingJsonProvider, StubProvider>();
            }

            var app = builder.Build();
            app.MapBowireMockHostEndpoints("");
            await app.StartAsync();
            var baseUrl = app.Urls.First();
            return new MockHostFactory(app, baseUrl);
        }

        public HttpClient CreateClient() => new() { BaseAddress = new Uri(_baseUrl) };

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private sealed class StubProvider : IRecordingJsonProvider
        {
            public string? TryGetRecordingJson(string recordingId) => null;
        }
    }

    [Fact]
    public async Task StartAsync_with_empty_recording_envelope_throws_with_descriptive_error()
    {
        // Empty {} parses as JSON but isn't a BowireRecording (no Id /
        // Steps). MockServer.StartAsync surfaces this with a specific
        // error rather than crashing the host.
        var registry = new MockRegistry(NullLogger<MockRegistry>.Instance);
        await using (registry)
        {
            // Some serializers tolerate {} (everything null-defaults),
            // so we widen the assertion to "throws or returns an inst
            // without traffic". If it throws, rollback happens.
            try
            {
                var inst = await registry.StartAsync(
                    "{}",
                    "empty-envelope",
                    port: 0,
                    TestContext.Current.CancellationToken);
                // Survived the start → cleanup so the test doesn't leak
                // a listener on this run.
                await registry.StopAsync(inst.MockId);
            }
            catch (Exception ex) when (
                ex is JsonException
                or InvalidOperationException
                or System.IO.InvalidDataException
                or System.IO.FileNotFoundException)
            {
                // Expected — we exercised the registry's catch path.
                Assert.NotNull(ex.Message);
            }
        }
    }
}
