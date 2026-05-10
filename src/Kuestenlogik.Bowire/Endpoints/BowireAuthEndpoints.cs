// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Maps the auth-helper proxy endpoints. Browsers can't talk to most
/// OAuth token endpoints directly because the IdPs don't return CORS
/// headers — Bowire proxies the request server-side and the JS layer
/// caches the resulting access_token in memory until it nears expiry.
/// Covers the three OAuth 2.0 grants the UI knows about
/// (client_credentials, authorization_code, refresh_token), a generic
/// custom-token proxy for "Bearer with auto-refresh", and the static
/// HTML callback page IdPs redirect to after consent.
/// </summary>
internal static class BowireAuthEndpoints
{
    public static IEndpointRouteBuilder MapBowireAuthEndpoints(
        this IEndpointRouteBuilder endpoints, BowireOptions options, string basePath)
    {
        // ---- OAuth 2.0 client_credentials proxy ----
        // Browsers can't call most OAuth token endpoints directly because they
        // don't allow CORS. Bowire proxies the request server-side. The
        // browser keeps an in-memory cache so it only hits this endpoint when
        // the cached token is missing or near expiry.
        endpoints.MapPost($"{basePath}/api/auth/oauth-token", async (HttpContext ctx) =>
        {
            OauthTokenRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<OauthTokenRequest>(
                    ctx.Request.Body, BowireEndpointHelpers.JsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid request body: " + ex.Message }, BowireEndpointHelpers.JsonOptions, statusCode: 400);
            }

            if (body is null || string.IsNullOrEmpty(body.TokenUrl))
                return Results.Json(new { error = "Missing tokenUrl" }, BowireEndpointHelpers.JsonOptions, statusCode: 400);
            if (string.IsNullOrEmpty(body.ClientId))
                return Results.Json(new { error = "Missing clientId" }, BowireEndpointHelpers.JsonOptions, statusCode: 400);

            try
            {
                var form = new List<KeyValuePair<string, string>>
                {
                    new("grant_type", "client_credentials"),
                    new("client_id", body.ClientId),
                    new("client_secret", body.ClientSecret ?? "")
                };
                if (!string.IsNullOrEmpty(body.Scope)) form.Add(new("scope", body.Scope));
                if (!string.IsNullOrEmpty(body.Audience)) form.Add(new("audience", body.Audience));

                if (!Uri.TryCreate(body.TokenUrl, UriKind.Absolute, out var tokenUri))
                    return Results.Json(new { error = "tokenUrl is not a valid absolute URL" }, BowireEndpointHelpers.JsonOptions, statusCode: 400);

                var http = ctx.RequestServices
                    .GetRequiredService<IHttpClientFactory>()
                    .CreateClient("bowire-oauth");
                using var content = new FormUrlEncodedContent(form);
                using var resp = await http.PostAsync(tokenUri, content, ctx.RequestAborted);
                var responseBody = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);

                if (!resp.IsSuccessStatusCode)
                {
                    return Results.Json(new
                    {
                        error = $"Token endpoint returned HTTP {(int)resp.StatusCode}",
                        details = responseBody
                    }, BowireEndpointHelpers.JsonOptions, statusCode: 502);
                }

                // Pass the token endpoint's JSON body straight through
                return Results.Content(responseBody, "application/json");
            }
            catch (Exception ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "OAuth client_credentials proxy failed for {TokenUrl}", body.TokenUrl);
                return Results.Json(new { error = ex.Message }, BowireEndpointHelpers.JsonOptions, statusCode: 502);
            }
        }).ExcludeFromDescription();

        // OAuth 2.0 authorization_code: exchange the code returned to the
        // browser callback for an access_token + refresh_token pair. Acts
        // as a CORS-bypass proxy so the JS doesn't need to call the
        // identity provider's token endpoint directly (most providers
        // don't enable CORS on /token).
        endpoints.MapPost($"{basePath}/api/auth/oauth-code-exchange", async (HttpContext ctx) =>
        {
            OauthCodeExchangeRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<OauthCodeExchangeRequest>(
                    ctx.Request.Body, BowireEndpointHelpers.JsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid request body: " + ex.Message }, BowireEndpointHelpers.JsonOptions, statusCode: 400);
            }

            if (body is null || string.IsNullOrEmpty(body.TokenUrl) || string.IsNullOrEmpty(body.Code) ||
                string.IsNullOrEmpty(body.ClientId) || string.IsNullOrEmpty(body.RedirectUri))
            {
                return Results.Json(new { error = "Missing tokenUrl, code, clientId, or redirectUri" }, BowireEndpointHelpers.JsonOptions, statusCode: 400);
            }
            if (!Uri.TryCreate(body.TokenUrl, UriKind.Absolute, out var tokenUri))
                return Results.Json(new { error = "tokenUrl is not a valid absolute URL" }, BowireEndpointHelpers.JsonOptions, statusCode: 400);

            try
            {
                var form = new List<KeyValuePair<string, string>>
                {
                    new("grant_type", "authorization_code"),
                    new("code", body.Code),
                    new("redirect_uri", body.RedirectUri),
                    new("client_id", body.ClientId)
                };
                if (!string.IsNullOrEmpty(body.CodeVerifier)) form.Add(new("code_verifier", body.CodeVerifier));
                if (!string.IsNullOrEmpty(body.ClientSecret)) form.Add(new("client_secret", body.ClientSecret));

                var http = ctx.RequestServices
                    .GetRequiredService<IHttpClientFactory>()
                    .CreateClient("bowire-oauth");
                using var content = new FormUrlEncodedContent(form);
                using var resp = await http.PostAsync(tokenUri, content, ctx.RequestAborted);
                var responseBody = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);

                if (!resp.IsSuccessStatusCode)
                {
                    return Results.Json(new
                    {
                        error = $"Token endpoint returned HTTP {(int)resp.StatusCode}",
                        details = responseBody
                    }, BowireEndpointHelpers.JsonOptions, statusCode: 502);
                }
                return Results.Content(responseBody, "application/json");
            }
            catch (Exception ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "OAuth authorization_code proxy failed for {TokenUrl}", body.TokenUrl);
                return Results.Json(new { error = ex.Message }, BowireEndpointHelpers.JsonOptions, statusCode: 502);
            }
        }).ExcludeFromDescription();

        // OAuth 2.0 refresh_token: trade an old refresh_token for a fresh
        // access_token (and possibly a rotated refresh_token). Same CORS
        // bypass story as the code exchange above.
        endpoints.MapPost($"{basePath}/api/auth/oauth-refresh", async (HttpContext ctx) =>
        {
            OauthRefreshRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<OauthRefreshRequest>(
                    ctx.Request.Body, BowireEndpointHelpers.JsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid request body: " + ex.Message }, BowireEndpointHelpers.JsonOptions, statusCode: 400);
            }

            if (body is null || string.IsNullOrEmpty(body.TokenUrl) || string.IsNullOrEmpty(body.RefreshToken) ||
                string.IsNullOrEmpty(body.ClientId))
            {
                return Results.Json(new { error = "Missing tokenUrl, refreshToken, or clientId" }, BowireEndpointHelpers.JsonOptions, statusCode: 400);
            }
            if (!Uri.TryCreate(body.TokenUrl, UriKind.Absolute, out var tokenUri))
                return Results.Json(new { error = "tokenUrl is not a valid absolute URL" }, BowireEndpointHelpers.JsonOptions, statusCode: 400);

            try
            {
                var form = new List<KeyValuePair<string, string>>
                {
                    new("grant_type", "refresh_token"),
                    new("refresh_token", body.RefreshToken),
                    new("client_id", body.ClientId)
                };
                if (!string.IsNullOrEmpty(body.ClientSecret)) form.Add(new("client_secret", body.ClientSecret));
                if (!string.IsNullOrEmpty(body.Scope)) form.Add(new("scope", body.Scope));

                var http = ctx.RequestServices
                    .GetRequiredService<IHttpClientFactory>()
                    .CreateClient("bowire-oauth");
                using var content = new FormUrlEncodedContent(form);
                using var resp = await http.PostAsync(tokenUri, content, ctx.RequestAborted);
                var responseBody = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);

                if (!resp.IsSuccessStatusCode)
                {
                    return Results.Json(new
                    {
                        error = $"Token endpoint returned HTTP {(int)resp.StatusCode}",
                        details = responseBody
                    }, BowireEndpointHelpers.JsonOptions, statusCode: 502);
                }
                return Results.Content(responseBody, "application/json");
            }
            catch (Exception ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "OAuth refresh_token proxy failed for {TokenUrl}", body.TokenUrl);
                return Results.Json(new { error = ex.Message }, BowireEndpointHelpers.JsonOptions, statusCode: 502);
            }
        }).ExcludeFromDescription();

        // Custom token endpoint proxy — exchanges a generic { url, method,
        // body, headers } request for the response body so the JS layer can
        // pluck a token out of an arbitrary HTTP endpoint without hitting
        // CORS. Used by the "Bearer with auto-refresh" auth helper.
        endpoints.MapPost($"{basePath}/api/auth/custom-token", async (HttpContext ctx) =>
        {
            CustomTokenRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CustomTokenRequest>(
                    ctx.Request.Body, BowireEndpointHelpers.JsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid request body: " + ex.Message }, BowireEndpointHelpers.JsonOptions, statusCode: 400);
            }

            if (body is null || string.IsNullOrEmpty(body.Url))
                return Results.Json(new { error = "Missing url" }, BowireEndpointHelpers.JsonOptions, statusCode: 400);
            if (!Uri.TryCreate(body.Url, UriKind.Absolute, out var tokenUri))
                return Results.Json(new { error = "url is not a valid absolute URL" }, BowireEndpointHelpers.JsonOptions, statusCode: 400);

            try
            {
                var http = ctx.RequestServices
                    .GetRequiredService<IHttpClientFactory>()
                    .CreateClient("bowire-oauth");
                using var request = new HttpRequestMessage(
                    new HttpMethod(string.IsNullOrEmpty(body.Method) ? "POST" : body.Method.ToUpperInvariant()),
                    tokenUri);

                if (body.Headers is not null)
                {
                    foreach (var (k, v) in body.Headers)
                        request.Headers.TryAddWithoutValidation(k, v);
                }

                if (!string.IsNullOrEmpty(body.Body))
                {
                    var contentType = body.ContentType ?? "application/json";
                    request.Content = new StringContent(body.Body, Encoding.UTF8, contentType);
                }

                using var resp = await http.SendAsync(request, ctx.RequestAborted);
                var responseBody = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);

                if (!resp.IsSuccessStatusCode)
                {
                    return Results.Json(new
                    {
                        error = $"Token endpoint returned HTTP {(int)resp.StatusCode}",
                        details = responseBody
                    }, BowireEndpointHelpers.JsonOptions, statusCode: 502);
                }

                return Results.Content(responseBody, resp.Content.Headers.ContentType?.MediaType ?? "application/json");
            }
            catch (Exception ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Custom token proxy failed for {Url}", body.Url);
                return Results.Json(new { error = ex.Message }, BowireEndpointHelpers.JsonOptions, statusCode: 502);
            }
        }).ExcludeFromDescription();

        // OAuth 2.0 callback page — the IdP redirects the user's browser
        // here after they approve the consent screen. The page reads the
        // ?code=...&state=... from its own URL and postMessages them to
        // its window.opener (the Bowire UI), then closes itself.
        endpoints.MapGet($"{basePath}/oauth-callback", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return Results.Content(OauthCallbackHtml, "text/html");
        }).ExcludeFromDescription();

        // ---- Cookie jar inspect / clear ----
        // The "Persist cookies between requests" toggle on the env-auth UI
        // calls these to render the current jar contents and to clear the
        // jar when the user wants to log out / start over. The actual
        // store lives in Kuestenlogik.Bowire.Auth.CookieJar (in-memory, per envId).
        endpoints.MapGet($"{basePath}/api/auth/cookie-jar", (HttpContext ctx) =>
        {
            var envId = ctx.Request.Query["env"].ToString();
            if (string.IsNullOrEmpty(envId))
                return Results.Json(new { error = "Missing env query parameter" }, BowireEndpointHelpers.JsonOptions, statusCode: 400);
            var snapshot = CookieJar.Snapshot(envId);
            return Results.Json(new { env = envId, cookies = snapshot }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/auth/cookie-jar", (HttpContext ctx) =>
        {
            var envId = ctx.Request.Query["env"].ToString();
            if (string.IsNullOrEmpty(envId))
                return Results.Json(new { error = "Missing env query parameter" }, BowireEndpointHelpers.JsonOptions, statusCode: 400);
            var cleared = CookieJar.Clear(envId);
            return Results.Json(new { env = envId, cleared }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }

    private sealed record OauthTokenRequest(
        string TokenUrl,
        string ClientId,
        string? ClientSecret,
        string? Scope,
        string? Audience);

    private sealed record OauthCodeExchangeRequest(
        string TokenUrl,
        string Code,
        string RedirectUri,
        string ClientId,
        string? ClientSecret,
        string? CodeVerifier);

    private sealed record OauthRefreshRequest(
        string TokenUrl,
        string RefreshToken,
        string ClientId,
        string? ClientSecret,
        string? Scope);

    private sealed record CustomTokenRequest(
        string Url,
        string? Method,
        string? Body,
        string? ContentType,
        Dictionary<string, string>? Headers);

    /// <summary>
    /// Static HTML page served at <c>/{basePath}/oauth-callback</c>. The IdP
    /// redirects the browser here after the user approves consent; the
    /// page parses the <c>?code=...&amp;state=...</c> from its own URL and
    /// posts them to its <c>window.opener</c> via <c>postMessage</c>, then
    /// closes itself. The auth helper in the main UI listens for that
    /// message and trades the code for tokens via the proxy endpoint above.
    /// </summary>
    private const string OauthCallbackHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="UTF-8">
        <title>Bowire — OAuth callback</title>
        <style>
          body { font-family: system-ui, sans-serif; background: #0f0f17; color: #e8e8f0; padding: 40px; text-align: center; }
          h1 { color: #60a5fa; font-size: 16px; font-weight: 600; }
          p  { color: #94a3b8; font-size: 13px; line-height: 1.5; }
          code { background: #1c1c2e; padding: 2px 6px; border-radius: 3px; font-size: 12px; }
        </style>
        </head>
        <body>
        <h1>Authentication complete</h1>
        <p>Bowire received the authorization response. You can close this window.</p>
        <script>
        (function () {
          var params = new URLSearchParams(window.location.search);
          var payload = {
            type: 'bowire-oauth-callback',
            code: params.get('code'),
            state: params.get('state'),
            error: params.get('error'),
            errorDescription: params.get('error_description')
          };
          if (window.opener) {
            try { window.opener.postMessage(payload, window.location.origin); }
            catch (e) { console.error('postMessage failed', e); }
            setTimeout(function () { window.close(); }, 250);
          } else {
            document.body.insertAdjacentHTML('beforeend',
              '<p>No opener window — copy the response manually:<br><code>' +
              JSON.stringify(payload) + '</code></p>');
          }
        })();
        </script>
        </body>
        </html>
        """;
}
