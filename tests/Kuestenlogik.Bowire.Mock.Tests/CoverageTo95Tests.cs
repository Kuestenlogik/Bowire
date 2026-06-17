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

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Gap-fills for the consolidated mock-management surface
/// (<see cref="BowireMockHostManager"/> + <c>POST /api/mocks</c>). The
/// happy path lives in <see cref="BowireMockHostManagerTests"/>; this
/// file pins the error / rollback / endpoint edge cases.
/// </summary>
public sealed class CoverageTo95Tests
{
    // ---- Manager-level rollback ------------------------------------

    [Fact]
    public async Task StartAsync_with_unparseable_recording_throws_and_leaves_manager_empty()
    {
        // Junk payload — MockServer reads the file as JSON, fails to
        // deserialise, throws inside StartAsync. The catch block in
        // BowireMockHostManager.StartAsync must delete the temp file
        // and rethrow, leaving the manager state untouched.
        //
        // We can't snapshot the shared bowire-mock-hosts temp dir for
        // a before/after diff — parallel xUnit workers would race on
        // it. The manager-state assertion is the stable signal.
        await using var manager = new BowireMockHostManager();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await manager.StartAsync(
                recordingJson: "this is not json",
                recordingId: "x",
                label: "rollback-test",
                port: 0,
                TestContext.Current.CancellationToken);
        });

        Assert.NotNull(ex);
        Assert.Empty(manager.List());
    }

    [Fact]
    public async Task StartAsync_throws_ArgumentNullException_for_null_payload()
    {
        await using var manager = new BowireMockHostManager();
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await manager.StartAsync(
                recordingJson: null!,
                recordingId: "x",
                label: "x",
                port: 0,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_with_empty_recording_envelope_throws_or_starts_then_stops()
    {
        // Empty {} parses as JSON but isn't a BowireRecording. Some
        // serializers tolerate {} (everything null-defaults), so we
        // widen the assertion to "throws or returns a handle".
        await using var manager = new BowireMockHostManager();
        try
        {
            var handle = await manager.StartAsync(
                "{}",
                "empty",
                "empty-envelope",
                port: 0,
                TestContext.Current.CancellationToken);
            await manager.StopAsync(handle.MockId, TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (
            ex is JsonException
            or InvalidOperationException
            or InvalidDataException
            or FileNotFoundException)
        {
            Assert.NotNull(ex.Message);
        }
    }

    // ---- POST /api/mocks — endpoint-level error paths --------------

    [Fact]
    public async Task POST_mocks_returns_400_when_body_is_not_json()
    {
        await using var factory = await MockApiFactory.StartAsync();
        using var client = factory.CreateClient();
        using var content = new StringContent("not json", Encoding.UTF8, "application/json");

        using var resp = await client.PostAsync(
            new Uri("/api/mocks", UriKind.Relative),
            content,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task POST_mocks_returns_400_when_neither_recording_nor_recordingId_is_set()
    {
        await using var factory = await MockApiFactory.StartAsync();
        using var client = factory.CreateClient();

        using var resp = await client.PostAsJsonAsync(
            "/api/mocks",
            new { },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task POST_mocks_with_recordingId_returns_500_when_no_provider_is_registered()
    {
        await using var factory = await MockApiFactory.StartAsync(registerProvider: false);
        using var client = factory.CreateClient();

        using var resp = await client.PostAsJsonAsync(
            "/api/mocks",
            new { recordingId = "anything" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
    }

    [Fact]
    public async Task POST_mocks_with_recordingId_returns_404_when_recording_is_not_found()
    {
        await using var factory = await MockApiFactory.StartAsync();
        using var client = factory.CreateClient();

        using var resp = await client.PostAsJsonAsync(
            "/api/mocks",
            new { recordingId = "this-id-was-never-registered" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- GET / DELETE /api/mocks/<id> ------------------------------

    [Fact]
    public async Task GET_mocks_returns_empty_list_when_nothing_is_running()
    {
        await using var factory = await MockApiFactory.StartAsync();
        using var client = factory.CreateClient();

        var payload = await client.GetFromJsonAsync<JsonElement>(
            "/api/mocks",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, payload.GetProperty("mocks").GetArrayLength());
    }

    [Fact]
    public async Task DELETE_mocks_returns_404_when_mock_id_is_unknown()
    {
        await using var factory = await MockApiFactory.StartAsync();
        using var client = factory.CreateClient();

        using var resp = await client.DeleteAsync(
            new Uri("/api/mocks/nonexistent", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GET_mocks_id_requests_returns_404_when_mock_id_is_unknown()
    {
        await using var factory = await MockApiFactory.StartAsync();
        using var client = factory.CreateClient();

        using var resp = await client.GetAsync(
            new Uri("/api/mocks/nonexistent/requests", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>
    /// Thin web-app fixture that maps the consolidated mock-management
    /// endpoints onto a real loopback Kestrel listener with a
    /// configurable recording-provider registration.
    /// </summary>
    private sealed class MockApiFactory : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _baseUrl;

        private MockApiFactory(WebApplication app, string baseUrl)
        {
            _app = app;
            _baseUrl = baseUrl;
        }

        public static async Task<MockApiFactory> StartAsync(bool registerProvider = true)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
                o.Listen(System.Net.IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));

            builder.Services.AddSingleton<BowireMockHostManager>();
            if (registerProvider)
            {
                builder.Services.AddSingleton<IRecordingJsonProvider, StubProvider>();
            }

            var app = builder.Build();
            app.MapBowireMockManagement("");
            await app.StartAsync();
            var baseUrl = app.Urls.First();
            return new MockApiFactory(app, baseUrl);
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
}
