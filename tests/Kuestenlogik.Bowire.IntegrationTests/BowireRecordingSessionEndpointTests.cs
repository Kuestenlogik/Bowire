// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Recording;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Integration coverage for #285 — the
/// <c>POST/GET /api/recording/session/*</c> REST + SSE surface and the
/// reconciliation between an MCP tool that mutates the session and the
/// SSE channel that the workbench listens to. Each test owns its own
/// TestServer host and <see cref="BowireRecordingSession"/> singleton.
/// </summary>
public sealed class BowireRecordingSessionEndpointTests
{
    [Fact]
    public async Task POST_start_then_GET_status_returns_active_session()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var startReq = JsonContent.Create(new
        {
            workspaceId = "ws-1",
            mode = "capture",
            name = "scenario A",
        });
        using var startResp = await client.PostAsync(
            new Uri("/api/recording/session/start", UriKind.Relative),
            startReq, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);

        using var statusResp = await client.GetAsync(
            new Uri("/api/recording/session/status", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);

        var body = await statusResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("active").GetBoolean());
        var session = doc.RootElement.GetProperty("session");
        Assert.Equal("ws-1", session.GetProperty("workspaceId").GetString());
        Assert.Equal("scenario A", session.GetProperty("name").GetString());
        Assert.Equal("capture", session.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task POST_start_when_already_active_returns_409()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var body = JsonContent.Create(new { workspaceId = "ws", mode = "capture" });
        using var first = await client.PostAsync(
            new Uri("/api/recording/session/start", UriKind.Relative),
            body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The second POST has to send its own JsonContent — JsonContent
        // is single-use, the first call already consumed its stream.
        using var body2 = JsonContent.Create(new { workspaceId = "ws", mode = "capture" });
        using var second = await client.PostAsync(
            new Uri("/api/recording/session/start", UriKind.Relative),
            body2, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("already active", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task POST_replay_without_active_session_returns_409()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var emptyBody = new StringContent(string.Empty);
        using var resp = await client.PostAsync(
            new Uri("/api/recording/session/replay", UriKind.Relative),
            emptyBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task POST_stop_without_active_session_returns_stopped_false()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var emptyBody = new StringContent(string.Empty);
        using var resp = await client.PostAsync(
            new Uri("/api/recording/session/stop", UriKind.Relative),
            emptyBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("stopped").GetBoolean());
        Assert.Equal("no-active-session", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task POST_start_invalid_json_returns_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var content = new StringContent("{not json", Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(
            new Uri("/api/recording/session/start", UriKind.Relative),
            content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SSE_events_emits_started_step_stopped_for_workbench_reconciliation()
    {
        // Reconciliation scenario: an MCP-style server-side actor
        // (here just the session singleton) mutates state; the
        // workbench-side SSE listener observes the transitions. This is
        // the exact channel the workbench's recorder badge will hook
        // into post-#285.
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var session = host.Services.GetRequiredService<BowireRecordingSession>();

        using var req = new HttpRequestMessage(HttpMethod.Get,
            new Uri("/api/recording/session/events", UriKind.Relative));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        using var stream = await resp.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);

        var ct = TestContext.Current.CancellationToken;
        _ = Task.Run(async () =>
        {
            // Tiny delay so the subscriber is definitely live before
            // we start firing events.
            await Task.Delay(100, ct);
            session.Start("ws", BowireRecordingMode.Capture, name: "via-mcp");
            session.AppendStep(new Mocking.BowireRecordingStep { Id = "s1" });
            session.Stop();
        }, ct);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);

        var sawStarted = false;
        var sawStep = false;
        var sawStopped = false;
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(linked.Token)) is not null)
            {
                if (line.StartsWith("event: started", StringComparison.Ordinal)) sawStarted = true;
                if (line.StartsWith("event: step", StringComparison.Ordinal)) sawStep = true;
                if (line.StartsWith("event: stopped", StringComparison.Ordinal))
                {
                    sawStopped = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* timeout */ }

        Assert.True(sawStarted, "SSE should have emitted a 'started' event after session.Start().");
        Assert.True(sawStep, "SSE should have emitted a 'step' event after session.AppendStep().");
        Assert.True(sawStopped, "SSE should have emitted a 'stopped' event after session.Stop().");
    }

    [Fact]
    public async Task SSE_events_emits_snapshot_for_late_subscribers()
    {
        // Workbench reconnect scenario — a browser tab reconnects mid-
        // session and needs to know the current state without polling
        // /status separately. The endpoint emits an event: snapshot
        // frame on connect when a session is already open.
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var session = host.Services.GetRequiredService<BowireRecordingSession>();

        session.Start("ws", BowireRecordingMode.Capture, name: "pre-existing");

        using var req = new HttpRequestMessage(HttpMethod.Get,
            new Uri("/api/recording/session/events", UriKind.Relative));
        using var resp = await client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        using var stream = await resp.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = TestContext.Current.CancellationToken;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);

        var sawSnapshot = false;
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(linked.Token)) is not null)
            {
                if (line.StartsWith("event: snapshot", StringComparison.Ordinal))
                {
                    sawSnapshot = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }

        Assert.True(sawSnapshot, "SSE should emit an initial 'snapshot' event for reconnecting clients.");
    }

    // ----------------------------------------------------------------

    private static async Task<IHost> BuildHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireRecordingSessionEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       s.AddSingleton<BowireRecordingSession>();
                   });
            })
            .Build();
        await host.StartAsync();
        return host;
    }
}
