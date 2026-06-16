// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Kuestenlogik.Bowire.Workspace.Git;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Integration coverage for #196 Phase 2.4 — the
/// <c>GET /api/workspace/events</c> SSE producer that streams
/// <see cref="WorkspaceFileEvent"/>s from <see cref="WorkspaceWatcher"/>.
/// Two scenarios drive the route:
/// <list type="bullet">
/// <item>Standalone host that called <c>AddBowireGitWorkspace()</c> —
/// the route streams events for the requested storageRoot.</item>
/// <item>Embedded host that did NOT call <c>AddBowireGitWorkspace()</c>
/// (the regression case in the #196 acceptance list) — the route
/// surfaces 501 + an "AddBowireGitWorkspace not called" hint so a
/// misconfigured host fails loudly instead of silently never firing.</item>
/// </list>
/// </summary>
public sealed class WorkspaceGitEventsEndpointTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceGitEventsEndpointTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("bowire-events-integration-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task GET_workspace_events_without_storageRoot_query_returns_400()
    {
        using var host = await BuildHostWithWatcher();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/workspace/events", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("storageRoot", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GET_workspace_events_with_missing_storageRoot_returns_400()
    {
        using var host = await BuildHostWithWatcher();
        var client = host.GetTestClient();

        var bogus = Path.Combine(_tempRoot, "does-not-exist");
        using var resp = await client.GetAsync(
            new Uri($"/api/workspace/events?storageRoot={Uri.EscapeDataString(bogus)}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("does not exist", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GET_workspace_events_streams_file_change_events()
    {
        using var host = await BuildHostWithWatcher();
        var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Get,
            new Uri($"/api/workspace/events?storageRoot={Uri.EscapeDataString(_tempRoot)}", UriKind.Relative));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // Trigger a file change after the subscription opens. SSE
        // requires reading the response body as a stream — without
        // HttpCompletionOption.ResponseHeadersRead the test would
        // block here forever waiting for the body to "complete".
        using var resp = await client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        using var stream = await resp.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);

        // Schedule the file write after a short delay so the subscriber
        // is definitely live before the event fires.
        var path = Path.Combine(_tempRoot, "stream-test.json");
        var ct = TestContext.Current.CancellationToken;
        _ = Task.Run(async () =>
        {
            await Task.Delay(150, ct);
            await File.WriteAllTextAsync(path, "{}", ct);
        }, ct);

        // Read the SSE frames until we see the file-changed/created
        // event for our path. Each frame is "event: <kind>\ndata: …\n\n".
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, TestContext.Current.CancellationToken);

        var saw = false;
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(linkedCts.Token)) is not null)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal)
                    && line.Contains("stream-test.json", StringComparison.Ordinal))
                {
                    saw = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* timeout */ }

        Assert.True(saw, "Expected an SSE frame for stream-test.json within 5s.");
    }

    [Fact]
    public async Task GET_workspace_events_without_AddBowireGitWorkspace_returns_501()
    {
        // The #196 embedded-mode acceptance: a host that mounts the
        // endpoint but never called AddBowireGitWorkspace must surface
        // 501 with a "register first" hint — NOT start a background
        // watcher or silently swallow events. The TestServer here
        // mirrors a stock embedded ASP.NET app: routing + the events
        // endpoint, nothing else from the Workspace.Git package wired
        // into DI.
        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireGitWorkspaceEvents(basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var client = host.GetTestClient();
        using var resp = await client.GetAsync(
            new Uri($"/api/workspace/events?storageRoot={Uri.EscapeDataString(_tempRoot)}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("AddBowireGitWorkspace", body, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------

    private static async Task<IHost> BuildHostWithWatcher()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireGitWorkspaceEvents(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       s.AddBowireGitWorkspace();
                   });
            })
            .Build();
        await host.StartAsync();
        return host;
    }
}
