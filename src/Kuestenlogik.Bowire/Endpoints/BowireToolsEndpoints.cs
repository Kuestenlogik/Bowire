// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Interceptor;
using Kuestenlogik.Bowire.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Workbench Tools surface — endpoints that drive the UI affordances
/// under the topbar's "Tools" menu and Settings → Tools list.
/// Currently the only entry is the reverse-proxy launcher (#153 UI
/// phase), which lets an operator spin up an in-process
/// <see cref="BowireReverseProxyHost"/> from the workbench without
/// dropping to the standalone <c>bowire proxy</c> CLI.
/// </summary>
/// <remarks>
/// <para>
/// Endpoints live inside the same auth-gated route group as the rest
/// of the workbench API (see <see cref="BowireApiEndpoints"/>):
/// when the host runs with <c>--token</c> / a registered
/// <c>IBowireAuthProvider</c>, these admin endpoints inherit the
/// same posture. The data-plane traffic going through the started
/// proxy is NOT auth-gated — the reverse-proxy host forwards every
/// inbound request verbatim, just like the standalone proxy does.
/// </para>
/// <para>
/// Lifetime: every host the operator starts here is registered in
/// the singleton <see cref="ReverseProxyRegistry"/>, which hooks
/// <c>IHostApplicationLifetime.ApplicationStopping</c> and stops
/// every entry when the parent Bowire process exits. The UI strings
/// (and this XML comment) call this out so operators don't expect a
/// background daemon — for that they should use
/// <c>bowire proxy</c>.
/// </para>
/// </remarks>
internal static class BowireToolsEndpoints
{
    public static IEndpointRouteBuilder MapBowireToolsEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet($"{basePath}/api/tools/reverse-proxy", (HttpContext ctx) =>
        {
            var registry = ctx.RequestServices.GetService<ReverseProxyRegistry>();
            if (registry is null) return Results.Json(new { proxies = Array.Empty<object>() }, BowireEndpointHelpers.JsonOptions);
            var entries = registry.Snapshot()
                .Select(e => new ReverseProxyStatusDto(e.Port, e.Upstream.ToString(), e.StartedAt))
                .ToArray();
            return Results.Json(new { proxies = entries }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/tools/reverse-proxy/start", async (HttpContext ctx) =>
        {
            var registry = ctx.RequestServices.GetService<ReverseProxyRegistry>();
            if (registry is null)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:tools:reverse-proxy:no-registry",
                    title: "Reverse-proxy registry not wired",
                    status: 503,
                    detail: "ReverseProxyRegistry is not registered on this host.",
                    instance: ctx.Request.Path);
            }

            ReverseProxyStartRequest? payload;
            try
            {
                payload = await ctx.Request.ReadFromJsonAsync<ReverseProxyStartRequest>(
                    cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:tools:reverse-proxy:bad-request",
                    title: "Invalid reverse-proxy start payload",
                    status: 400,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }

            if (payload is null
                || string.IsNullOrWhiteSpace(payload.Upstream)
                || !Uri.TryCreate(payload.Upstream, UriKind.Absolute, out var upstreamUri))
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:tools:reverse-proxy:bad-request",
                    title: "Upstream URL required",
                    status: 400,
                    detail: "POST /api/tools/reverse-proxy/start requires an absolute upstream URL.",
                    instance: ctx.Request.Path);
            }
            if (payload.Port <= 0 || payload.Port > 65535)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:tools:reverse-proxy:bad-request",
                    title: "Invalid port",
                    status: 400,
                    detail: "Port must be in the 1..65535 range.",
                    instance: ctx.Request.Path);
            }

            // Pre-check the registry slot before binding Kestrel — saves
            // the bind/unbind churn when the operator clicks Start a
            // second time on the same port.
            if (registry.Get(payload.Port) is not null)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:tools:reverse-proxy:port-in-use",
                    title: "Port already in use",
                    status: 409,
                    detail: $"A reverse-proxy is already running on port {payload.Port}. Stop it first or pick another port.",
                    instance: ctx.Request.Path);
            }

            var hostOpts = new BowireReverseProxyHostOptions
            {
                Upstream = upstreamUri,
                ListenAddress = IPAddress.Loopback,
                ListenPort = payload.Port,
            };

            BowireReverseProxyHost host;
            try
            {
                host = BowireReverseProxyHost.Create(hostOpts);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:tools:reverse-proxy:create-failed",
                    title: "Could not create reverse-proxy host",
                    status: 500,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }

            try
            {
                await host.StartAsync(ctx.RequestAborted).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // Bind failures land here — usually "Address already in
                // use" when a non-Bowire process owns the port. Dispose
                // the half-built host so its HttpClient + handler don't
                // leak.
                await host.DisposeAsync().ConfigureAwait(false);
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:tools:reverse-proxy:bind-failed",
                    title: "Could not bind reverse-proxy listener",
                    status: 409,
                    detail: $"Port {payload.Port} could not be bound: {ex.Message}",
                    instance: ctx.Request.Path);
            }

            var entry = new ReverseProxyRegistryEntry(host.EdgePort, upstreamUri, host);
            if (!registry.TryAdd(entry))
            {
                // Lost the race with another concurrent Start request
                // that bound the same port. Stop + dispose so we don't
                // leak a Kestrel listener; the operator retries.
                await host.StopAsync(ctx.RequestAborted).ConfigureAwait(false);
                await host.DisposeAsync().ConfigureAwait(false);
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:tools:reverse-proxy:port-in-use",
                    title: "Port already in use",
                    status: 409,
                    detail: $"Another reverse-proxy started on port {entry.Port} just before this request.",
                    instance: ctx.Request.Path);
            }

            return Results.Json(new ReverseProxyStatusDto(entry.Port, entry.Upstream.ToString(), entry.StartedAt),
                BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/tools/reverse-proxy/stop", async (HttpContext ctx) =>
        {
            var registry = ctx.RequestServices.GetService<ReverseProxyRegistry>();
            if (registry is null) return Results.Json(new { stopped = false }, BowireEndpointHelpers.JsonOptions);

            ReverseProxyStopRequest? payload;
            try
            {
                payload = await ctx.Request.ReadFromJsonAsync<ReverseProxyStopRequest>(
                    cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:tools:reverse-proxy:bad-request",
                    title: "Invalid reverse-proxy stop payload",
                    status: 400,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }

            if (payload is null || payload.Port <= 0)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:tools:reverse-proxy:bad-request",
                    title: "Port required",
                    status: 400,
                    detail: "POST /api/tools/reverse-proxy/stop requires a port.",
                    instance: ctx.Request.Path);
            }

            var stopped = await registry.StopAsync(payload.Port, ctx.RequestAborted).ConfigureAwait(false);
            return Results.Json(new { stopped, port = payload.Port }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }

    private sealed record ReverseProxyStartRequest(
        [property: JsonPropertyName("upstream")] string Upstream,
        [property: JsonPropertyName("port")] int Port);

    private sealed record ReverseProxyStopRequest(
        [property: JsonPropertyName("port")] int Port);

    private sealed record ReverseProxyStatusDto(
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("upstream")] string Upstream,
        [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt);
}
