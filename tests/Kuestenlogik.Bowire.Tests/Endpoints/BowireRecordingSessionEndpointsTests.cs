// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Recording;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// Integration coverage for <see cref="BowireRecordingSessionEndpoints"/> (#285)
/// — the server-side recording-session REST + SSE surface. Drives a loopback
/// Kestrel host over the start / stop / replay / status routes plus the SSE
/// events channel, so the lifecycle transitions, the 400/409 problem branches,
/// and the recording-store flush all execute end-to-end. Joins CwdSerialised
/// because the flush target is redirected via the process-global
/// <c>RecordingStore.StorePath</c> test seam.
/// </summary>
[Collection("CwdSerialised")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope — app + client disposed by the caller.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic.")]
public sealed class BowireRecordingSessionEndpointsTests
{
    private static readonly Uri StartUri = new("/api/recording/session/start", UriKind.Relative);
    private static readonly Uri StopUri = new("/api/recording/session/stop", UriKind.Relative);
    private static readonly Uri ReplayUri = new("/api/recording/session/replay", UriKind.Relative);
    private static readonly Uri StatusUri = new("/api/recording/session/status", UriKind.Relative);
    private static readonly Uri EventsUri = new("/api/recording/session/events", UriKind.Relative);

    private sealed record Host(WebApplication App, HttpClient Http, string StoreFile, string? PrevStorePath)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync().ConfigureAwait(false);
            RecordingStore.StorePath = PrevStorePath!;
            try { if (File.Exists(StoreFile)) File.Delete(StoreFile); } catch (IOException) { /* best-effort */ }
        }
    }

    private static async Task<Host> StartHostAsync(CancellationToken ct)
    {
        var storeFile = Path.Combine(Path.GetTempPath(), "bowire-rec-" + Guid.NewGuid().ToString("N") + ".json");
        var prev = RecordingStore.StorePath;
        RecordingStore.StorePath = storeFile;

        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        b.Services.AddSingleton<BowireRecordingSession>();
        var app = b.Build();
        app.MapBowireRecordingSessionEndpoints("");
        await app.StartAsync(ct).ConfigureAwait(false);
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return new Host(app, http, storeFile, prev);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage resp, CancellationToken ct) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement.Clone();

    // ------------------------------- start -------------------------------

    [Fact]
    public async Task Start_returns_session_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using var resp = await host.Http.PostAsync(StartUri, Json("""{ "workspaceId": "ws1", "mode": "capture", "name": "demo" }"""), ct);
        resp.EnsureSuccessStatusCode();
        var body = await ReadJson(resp, ct);
        Assert.Equal("ws1", body.GetProperty("workspaceId").GetString());
        Assert.Equal("capture", body.GetProperty("mode").GetString());
        Assert.Equal("demo", body.GetProperty("name").GetString());
        Assert.Equal(0, body.GetProperty("stepCount").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("recordingId").GetString()));
    }

    [Fact]
    public async Task Start_twice_conflicts_409()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using (var first = await host.Http.PostAsync(StartUri, Json("""{ "workspaceId": "ws1", "mode": "capture" }"""), ct))
        {
            first.EnsureSuccessStatusCode();
        }
        using var second = await host.Http.PostAsync(StartUri, Json("""{ "workspaceId": "ws1", "mode": "capture" }"""), ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Start_with_malformed_json_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using var resp = await host.Http.PostAsync(StartUri, Json("{ not json"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Start_with_null_body_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        // A literal JSON null deserialises to a null request → the
        // "missing body" branch (distinct from the malformed-JSON branch).
        using var resp = await host.Http.PostAsync(StartUri, Json("null"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ------------------------------- status -------------------------------

    [Fact]
    public async Task Status_reflects_active_session()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using (var before = await host.Http.GetAsync(StatusUri, ct))
        {
            var body = await ReadJson(before, ct);
            Assert.False(body.GetProperty("active").GetBoolean());
        }

        using (var start = await host.Http.PostAsync(StartUri, Json("""{ "workspaceId": "ws1", "mode": "capture" }"""), ct))
        {
            start.EnsureSuccessStatusCode();
        }

        using var after = await host.Http.GetAsync(StatusUri, ct);
        var afterBody = await ReadJson(after, ct);
        Assert.True(afterBody.GetProperty("active").GetBoolean());
        Assert.Equal("ws1", afterBody.GetProperty("session").GetProperty("workspaceId").GetString());
    }

    // ------------------------------- replay -------------------------------

    [Fact]
    public async Task Replay_without_active_session_is_409()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using var resp = await host.Http.PostAsync(ReplayUri, content: null, ct);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Replay_switches_mode_when_active()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using (var start = await host.Http.PostAsync(StartUri, Json("""{ "workspaceId": "ws1", "mode": "capture" }"""), ct))
        {
            start.EnsureSuccessStatusCode();
        }
        using var resp = await host.Http.PostAsync(ReplayUri, content: null, ct);
        resp.EnsureSuccessStatusCode();
        var body = await ReadJson(resp, ct);
        Assert.Equal("replay", body.GetProperty("mode").GetString());
    }

    // ------------------------------- stop -------------------------------

    [Fact]
    public async Task Stop_without_active_session_reports_not_stopped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using var resp = await host.Http.PostAsync(StopUri, content: null, ct);
        resp.EnsureSuccessStatusCode();
        var body = await ReadJson(resp, ct);
        Assert.False(body.GetProperty("stopped").GetBoolean());
    }

    [Fact]
    public async Task Stop_persists_recording_and_returns_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        string recordingId;
        using (var start = await host.Http.PostAsync(StartUri, Json("""{ "workspaceId": "ws1", "mode": "capture", "name": "flush-me" }"""), ct))
        {
            recordingId = (await ReadJson(start, ct)).GetProperty("recordingId").GetString()!;
        }

        using var stop = await host.Http.PostAsync(StopUri, content: null, ct);
        stop.EnsureSuccessStatusCode();
        var body = await ReadJson(stop, ct);
        Assert.True(body.GetProperty("stopped").GetBoolean());
        Assert.Equal(recordingId, body.GetProperty("recordingId").GetString());
        Assert.Equal("flush-me", body.GetProperty("name").GetString());

        // The flush sink wrote the recording into the redirected store file.
        Assert.True(File.Exists(host.StoreFile), "stop should have flushed the recording to the store");
        var persisted = await File.ReadAllTextAsync(host.StoreFile, ct);
        Assert.Contains(recordingId, persisted, StringComparison.Ordinal);
    }

    // ------------------------------- SSE events -------------------------------

    [Fact]
    public async Task Events_stream_emits_snapshot_then_stopped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using (var start = await host.Http.PostAsync(StartUri, Json("""{ "workspaceId": "ws1", "mode": "capture" }"""), ct))
        {
            start.EnsureSuccessStatusCode();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var req = new HttpRequestMessage(HttpMethod.Get, EventsUri);
        using var resp = await host.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        resp.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        await using var stream = await resp.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);

        // On connect the handler emits the current snapshot.
        Assert.True(await ReadUntilAsync(reader, "event: snapshot", timeout.Token));

        // Stopping the session broadcasts a "stopped" event into the stream.
        using (var stop = await host.Http.PostAsync(StopUri, content: null, timeout.Token))
        {
            stop.EnsureSuccessStatusCode();
        }
        Assert.True(await ReadUntilAsync(reader, "event: stopped", timeout.Token));
    }

    private static async Task<bool> ReadUntilAsync(StreamReader reader, string needle, CancellationToken ct)
    {
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Contains(needle, StringComparison.Ordinal)) return true;
            }
        }
        catch (OperationCanceledException)
        {
            // fell through to timeout — treated as "not seen".
        }
        return false;
    }
}
