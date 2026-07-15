// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Oast.Server;

/// <summary>
/// The HTTP half of <c>bowire oast serve</c> (#35 Phase 2f): the
/// register / poll / deregister API plus the catch-all that records HTTP
/// callbacks.
/// </summary>
/// <remarks>
/// Wire-compatible with the interactsh protocol, so <c>--oast-server</c> can be
/// pointed at this server or at any other instance without a client change —
/// and so an existing deployment can be swapped for this one on the same DNS
/// delegation.
/// </remarks>
public static class BowireOastEndpoints
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Map the OAST API + HTTP catcher.
    /// </summary>
    /// <param name="endpoints">Route builder.</param>
    /// <param name="store">Session + interaction registry.</param>
    /// <param name="token">
    /// When set, register calls must present it as <c>Authorization</c>. Gates
    /// an instance that would otherwise be an open callback catcher for anyone
    /// on the internet.
    /// </param>
    /// <param name="log">Optional line sink for the console.</param>
    public static IEndpointRouteBuilder MapBowireOast(
        this IEndpointRouteBuilder endpoints,
        OastInteractionStore store,
        string? token = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(store);

        endpoints.MapPost("/register", async (HttpContext ctx) =>
        {
            if (!IsAuthorised(ctx, token)) return Results.Unauthorized();

            var body = await JsonSerializer.DeserializeAsync<RegisterRequest>(ctx.Request.Body, s_json).ConfigureAwait(false);
            if (body is null
                || string.IsNullOrWhiteSpace(body.CorrelationID)
                || string.IsNullOrWhiteSpace(body.SecretKey)
                || string.IsNullOrWhiteSpace(body.PublicKey))
            {
                return Results.BadRequest(new { error = "CorrelationID, SecretKey and PublicKey are required." });
            }

            // The client base64s the PEM text (not raw DER) — decode before import.
            string pem;
            try
            {
                pem = Encoding.UTF8.GetString(Convert.FromBase64String(body.PublicKey));
            }
            catch (FormatException)
            {
                return Results.BadRequest(new { error = "PublicKey must be base64 of a PEM public key." });
            }

            if (!store.TryRegister(body.CorrelationID, body.SecretKey, pem))
            {
                return Results.BadRequest(new { error = "PublicKey is not a usable RSA public key." });
            }

            log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"  [register] {body.CorrelationID}"));
            return Results.Ok(new { });
        }).ExcludeFromDescription();

        endpoints.MapGet("/poll", (HttpContext ctx, string? id, string? secret) =>
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
            {
                return Results.BadRequest(new { error = "id and secret are required." });
            }

            var polled = store.Poll(id, secret);
            // Unknown id and wrong secret return the same 401 on purpose: a
            // different answer would let a caller enumerate live sessions.
            if (polled is null) return Results.Unauthorized();

            var (aesKey, publicKey, interactions) = polled.Value;
            if (interactions.Count == 0)
            {
                // Nothing to hand back — and no key either, so an idle poll
                // never ships key material.
                return Results.Json(new PollResponse { Data = [] }, s_json);
            }

            var data = new List<string>(interactions.Count);
            foreach (var interaction in interactions)
            {
                var plain = JsonSerializer.SerializeToUtf8Bytes(interaction, s_json);
                var iv = RandomNumberGenerator.GetBytes(16);
                var cipher = InteractshClient.AesCtrTransform(aesKey, iv, plain);
                data.Add(Convert.ToBase64String([.. iv, .. cipher]));
            }

            return Results.Json(new PollResponse
            {
                // Wrapped to the caller's public key, so a shared instance
                // cannot read back what it stored.
                AesKey = Convert.ToBase64String(publicKey.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256)),
                Data = data,
            }, s_json);
        }).ExcludeFromDescription();

        endpoints.MapPost("/deregister", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<DeregisterRequest>(ctx.Request.Body, s_json).ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.CorrelationID) || string.IsNullOrWhiteSpace(body.SecretKey))
            {
                return Results.BadRequest(new { error = "CorrelationID and SecretKey are required." });
            }
            return store.Remove(body.CorrelationID, body.SecretKey)
                ? Results.Ok(new { })
                : Results.Unauthorized();
        }).ExcludeFromDescription();

        // Liveness / config probe — also what an operator hits to confirm the
        // delegation landed on the right box.
        endpoints.MapGet("/status", () => Results.Json(new
        {
            service = "bowire-oast",
            sessions = store.SessionCount,
        })).ExcludeFromDescription();

        // The HTTP catcher. Anything not matched above is a callback: a target
        // that resolved a planted host and then actually connected. Registered
        // last so it can't shadow the API.
        endpoints.MapFallback(async (HttpContext ctx) =>
        {
            var host = ctx.Request.Host.Host;
            var interaction = new OastInteraction
            {
                Protocol = "http",
                FullId = host,
                UniqueId = OastInteractionStore.CorrelationIdOf(host),
                RemoteAddress = ctx.Connection.RemoteIpAddress?.ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                RawRequest = await RenderRawRequestAsync(ctx).ConfigureAwait(false),
            };

            if (store.Record(host, interaction))
            {
                log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                    $"  [http] {ctx.Request.Method} {host}{ctx.Request.Path} from {interaction.RemoteAddress}"));
            }

            // Answer plainly. A catcher should look boring: an error page or a
            // redirect can change how the target behaves and muddy the signal.
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.WriteAsync("ok").ConfigureAwait(false);
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>The callback as the target sent it — quoted into the finding as evidence.</summary>
    private static async Task<string> RenderRawRequestAsync(HttpContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append(ctx.Request.Method).Append(' ')
          .Append(ctx.Request.Path).Append(ctx.Request.QueryString)
          .Append(' ').Append(ctx.Request.Protocol).Append('\n');
        foreach (var (name, values) in ctx.Request.Headers)
        {
            sb.Append(name).Append(": ").Append(values.ToString()).Append('\n');
        }

        // Bodies are bounded: a catcher must not be turned into a memory sink
        // by whatever is pointed at it.
        if (ctx.Request.ContentLength is > 0)
        {
            var buffer = new byte[Math.Min(4096, ctx.Request.ContentLength.Value)];
            var read = await ctx.Request.Body.ReadAsync(buffer).ConfigureAwait(false);
            if (read > 0) sb.Append('\n').Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        return sb.ToString();
    }

    private static bool IsAuthorised(HttpContext ctx, string? token)
    {
        if (string.IsNullOrEmpty(token)) return true;
        var presented = ctx.Request.Headers.Authorization.ToString();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(token));
    }

    private sealed class RegisterRequest
    {
        public string PublicKey { get; set; } = "";
        public string SecretKey { get; set; } = "";
        public string CorrelationID { get; set; } = "";
    }

    private sealed class DeregisterRequest
    {
        public string CorrelationID { get; set; } = "";
        public string SecretKey { get; set; } = "";
    }

    private sealed class PollResponse
    {
        [JsonPropertyName("aes_key")] public string? AesKey { get; set; }
        [JsonPropertyName("data")] public List<string> Data { get; set; } = [];
    }
}
