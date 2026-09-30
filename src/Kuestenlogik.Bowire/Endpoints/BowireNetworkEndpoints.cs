// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Authentication;
using System.Text.Json;
using Kuestenlogik.Bowire.Net;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Network settings — proxy, bypass list, CA bundle (#680). <c>workspaceId</c>
/// on the query picks the workspace layer; without it the global layer.
/// </summary>
internal static class BowireNetworkEndpoints
{
    public static IEndpointRouteBuilder MapBowireNetworkEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath, BowireOptions? options = null)
    {
        endpoints.MapGet($"{basePath}/api/network", (HttpContext ctx) =>
        {
            var workspaceId = WorkspaceIdOf(ctx);
            return Results.Json(Describe(workspaceId), BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapPut($"{basePath}/api/network", async (HttpContext ctx) =>
        {
            BowireNetworkSettings? body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<BowireNetworkSettings>(
                    BowireEndpointHelpers.JsonOptions, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
            {
                return Error("Malformed network settings: " + ex.Message);
            }
            if (body is null) return Error("Request body required.");

            var workspaceId = WorkspaceIdOf(ctx);
            try
            {
                BowireNetworkSettingsStore.Save(body, workspaceId);
            }
            catch (ArgumentException ex)
            {
                return Error(ex.Message);
            }
            return Results.Json(Describe(workspaceId), BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/network", (HttpContext ctx) =>
        {
            var workspaceId = WorkspaceIdOf(ctx);
            BowireNetworkSettingsStore.Save(new BowireNetworkSettings(), workspaceId);
            return Results.Json(Describe(workspaceId), BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        // "Does this URL get out, and how?" — the one check a user behind a
        // corporate proxy needs before blaming the API.
        endpoints.MapPost($"{basePath}/api/network/test", async (HttpContext ctx) =>
        {
            TestRequest? body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<TestRequest>(
                    BowireEndpointHelpers.JsonOptions, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
            {
                return Error("Malformed test request: " + ex.Message);
            }
            if (body?.Url is not { } raw || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var url)
                || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                return Error("url must be an absolute http:// or https:// URL.");

            // The test is a real request from this host — a locked host only
            // tests the targets it would dial anyway, and follows no redirect.
            var policy = BowireTargetPolicy.For(options, ctx.Request);
            if (policy.Refuse(ctx, raw) is { } refused) return refused;

            var result = await TestAsync(url, followRedirects: !policy.IsEnforced, ctx.RequestAborted).ConfigureAwait(false);
            return Results.Json(result, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>What the settings page and the MCP tool show; secrets are named, never shown.</summary>
    internal static object Describe(string? workspaceId)
    {
        var effective = BowireNetworkPolicy.For(workspaceId);
        var (global, workspace, host) = BowireNetworkPolicy.Layers(workspaceId);
        return new
        {
            workspaceId,
            mode = BowireNetworkSettings.ModeName(effective.Mode),
            proxyUrl = effective.ProxyUri?.ToString(),
            noProxy = effective.Bypass.Entries,
            proxyUser = effective.Settings.ProxyUser,
            proxyPasswordRef = effective.Settings.ProxyPasswordRef,
            hasCredential = effective.HasCredential,
            caBundle = DescribeBundle(effective.Settings.CaBundle),
            caCertificates = effective.CaCertificates?.Select(c => new { subject = c.Subject, notAfter = c.NotAfter }),
            sources = effective.Sources,
            problems = effective.Problems,
            layers = new { global, workspace, host = host.IsEmpty ? null : host },
            protocols = ProtocolSupport(),
        };
    }

    internal static IEnumerable<object> ProtocolSupport()
    {
        BowireProtocolRegistry registry;
        try
        {
            registry = BowireEndpointHelpers.GetRegistry();
        }
#pragma warning disable CA1031 // A settings view without the plugin list is still useful.
        catch
        {
            return [];
        }
#pragma warning restore CA1031
        return registry.Protocols.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            support = (p is IBowireProxySupport s ? s.ProxySupport : BowireProxySupport.Unknown).ToString(),
            note = p is IBowireProxySupport n ? n.ProxyNote : null,
            noteKey = p is IBowireProxySupport k ? k.ProxyNoteKey : null,
        }).ToArray();
    }

    private static string? DescribeBundle(string? bundle) =>
        bundle is null ? null
        : bundle.Contains("-----BEGIN", StringComparison.Ordinal) ? "(inline PEM)"
        : bundle;

    private static async Task<object> TestAsync(Uri url, bool followRedirects, CancellationToken ct)
    {
        var effective = BowireNetworkPolicy.Current;
        var proxy = effective.ProxyFor(url);
        using var client = followRedirects
            ? BowireHttpClientFactory.Create(null, "network-test", TimeSpan.FromSeconds(15))
            : CreateNoRedirectClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return new { ok = true, status = (int)response.StatusCode, via = proxy?.ToString() ?? "direct" };
        }
        catch (HttpRequestException ex)
        {
            return new { ok = false, via = proxy?.ToString() ?? "direct", error = Explain(ex, effective) };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new { ok = false, via = proxy?.ToString() ?? "direct", error = "No answer within 15 seconds." };
        }
    }

    /// <summary>The same proxy/CA setup as <see cref="BowireHttpClientFactory.Create"/>, without following redirects.</summary>
    private static HttpClient CreateNoRedirectClient()
    {
#pragma warning disable CA2000, CA5400 // Ownership of the handler moves into the HttpClient; same TLS settings as BowireHttpClientFactory.Create.
        var handler = BowireHttpClientFactory.CreateHandler(null, "network-test");
        handler.AllowAutoRedirect = false;
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
#pragma warning restore CA2000, CA5400
    }

    private static string Explain(HttpRequestException ex, BowireEffectiveNetwork effective)
    {
        var messages = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (!messages.Contains(e.Message)) messages.Add(e.Message);
        }
        var text = string.Join(" — ", messages);
        if (ex.InnerException is AuthenticationException)
        {
            text += effective.CaCertificates is null
                ? " The certificate chain is not trusted. Behind a TLS-inspecting proxy, add its CA certificate as the CA bundle."
                : " The configured CA bundle does not vouch for this certificate either.";
        }
        if (ex.StatusCode == System.Net.HttpStatusCode.ProxyAuthenticationRequired)
            text += " The proxy wants credentials: set a proxy user and a password reference.";
        return text;
    }

    private static string? WorkspaceIdOf(HttpContext ctx)
    {
        var scope = WorkspaceScopeQuery.From(ctx);
        if (scope.IsInvalid) throw new BadHttpRequestException(scope.Error!, StatusCodes.Status400BadRequest);
        return scope.WorkspaceId;
    }

    private static IResult Error(string message) =>
        Results.Json(new { error = message }, BowireEndpointHelpers.JsonOptions, statusCode: 400);

    private sealed class TestRequest
    {
        public string? Url { get; set; }
    }
}
