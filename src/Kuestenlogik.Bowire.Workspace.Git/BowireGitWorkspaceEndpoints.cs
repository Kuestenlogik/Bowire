// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Workspace.Git;

/// <summary>
/// Endpoint surface for the git-backed workspace runtime — currently
/// just the FS-watch SSE stream (#150). Hosts that called
/// <see cref="BowireGitWorkspaceServiceCollectionExtensions.AddBowireGitWorkspace"/>
/// at DI build call <see cref="MapBowireGitWorkspaceEvents"/> at
/// endpoint-routing setup to mount the route.
/// </summary>
/// <remarks>
/// Kept in a dedicated extension class so callers can mount the
/// runtime selectively — embedded hosts that want the watcher behind
/// their own auth gate can wrap the route group themselves.
/// </remarks>
public static class BowireGitWorkspaceEndpoints
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Map <c>GET {basePath}/api/workspace/events?storageRoot=&lt;path&gt;</c>
    /// — server-sent events for FileSystemWatcher activity under the
    /// workspace's storageRoot. One subscription per connected client;
    /// each gets its own debounced event queue. The route streams
    /// indefinitely; clients close by disconnecting (the browser's
    /// EventSource does this naturally on tab close).
    /// </summary>
    public static IEndpointRouteBuilder MapBowireGitWorkspaceEvents(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(basePath);

        endpoints.MapGet($"{basePath}/api/workspace/events", async (HttpContext ctx) =>
        {
            var watcher = ctx.RequestServices.GetService<WorkspaceWatcher>();
            if (watcher is null)
            {
                // Marker resolved but watcher singleton missing — the
                // host registered the activation marker without the
                // full AddBowireGitWorkspace() call. Surface a 500 so
                // the misconfiguration is obvious rather than silent.
                if (ctx.RequestServices.GetService<BowireGitWorkspaceExtension>() is not null)
                {
                    throw new InvalidOperationException(
                        "BowireGitWorkspaceExtension is registered but WorkspaceWatcher is not. " +
                        "Call AddBowireGitWorkspace() on the IServiceCollection.");
                }
                ctx.Response.StatusCode = 501;
                await ctx.Response.WriteAsync(
                    "Workspace.Git runtime not registered. Call AddBowireGitWorkspace() to enable FS-watch events.",
                    ctx.RequestAborted);
                return;
            }

            var storageRoot = ctx.Request.Query["storageRoot"].ToString();
            if (string.IsNullOrWhiteSpace(storageRoot))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync(
                    "Missing required query parameter 'storageRoot'.",
                    ctx.RequestAborted);
                return;
            }

            (System.Threading.Channels.ChannelReader<WorkspaceFileEvent> reader, IDisposable subscription) sub;
            try
            {
                sub = watcher.Subscribe(storageRoot);
            }
            catch (ArgumentException ex)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync(ex.Message, ctx.RequestAborted);
                return;
            }

            using var _ = sub.subscription;

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            // Flush headers immediately so the browser's EventSource
            // transitions from CONNECTING to OPEN before the first
            // event lands — matches the SSE pattern in
            // BowireChannelEndpoints.
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            try
            {
                await foreach (var evt in sub.reader.ReadAllAsync(ctx.RequestAborted))
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        kind = evt.Kind,
                        path = evt.RelativePath,
                        timestampMs = evt.TimestampMs,
                    }, s_jsonOpts);
                    await ctx.Response.WriteAsync(
                        $"event: {evt.Kind}\ndata: {payload}\n\n",
                        ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — normal SSE close path.
            }
        }).ExcludeFromDescription();

        return endpoints;
    }
}
