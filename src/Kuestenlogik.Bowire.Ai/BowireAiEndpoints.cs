// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Ai;

/// <summary>
/// HTTP API surface for the optional <c>Kuestenlogik.Bowire.Ai</c>
/// package (#25 Phase 2). Three endpoints:
/// <list type="bullet">
///   <item><c>GET /api/ai/probe-local</c> -- 300ms probe against
///     Ollama (<c>127.0.0.1:11434</c>) and LM Studio
///     (<c>127.0.0.1:1234</c>). Returns the model list each one
///     advertises so the AI settings UI can offer a 'detected' pick.</item>
///   <item><c>GET /api/ai/status</c> -- returns the active
///     <see cref="BowireAiOptions"/> plus whether an
///     <see cref="IChatClient"/> is registered.</item>
///   <item><c>POST /api/ai/chat</c> -- proxy a chat completion via
///     the registered <see cref="IChatClient"/>. Body shape mirrors
///     a minimal subset of the OpenAI chat-completions request so
///     the workbench's JS can keep one client shape across providers.</item>
/// </list>
/// </summary>
public static class BowireAiEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    // Single HttpClient -- the probe paths are local + always cheap;
    // shared lifetime keeps the socket pool small.
    private static readonly HttpClient ProbeHttp = new()
    {
        Timeout = TimeSpan.FromMilliseconds(300),
    };

    public static IEndpointRouteBuilder MapBowireAiEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath = "/bowire")
    {
        endpoints.MapGet($"{basePath}/api/ai/probe-local",
            async (HttpContext ctx, BowireAiOptions opts) =>
        {
            if (!opts.AutoDetectLocal)
            {
                return Results.Json(new { skipped = "auto-detect disabled" }, JsonOpts);
            }

            var ollama = await ProbeOllamaAsync(ctx.RequestAborted);
            var lmstudio = await ProbeLmStudioAsync(ctx.RequestAborted);
            return Results.Json(new { ollama, lmstudio }, JsonOpts);
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/ai/status",
            (BowireAiOptions opts, IServiceProvider sp) =>
        {
            var hasClient = sp.GetService<IChatClient>() is not null;
            return Results.Json(new
            {
                hasClient,
                opts.ProviderId,
                opts.Endpoint,
                opts.Model,
                opts.AutoDetectLocal,
            }, JsonOpts);
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/ai/chat",
            async (HttpContext ctx, IChatClient? client) =>
        {
            if (client is null)
            {
                return Results.Json(
                    new { error = "No IChatClient registered. Add Kuestenlogik.Bowire.Ai and call AddBowireAi(), or register your own client." },
                    JsonOpts, statusCode: 503);
            }

            ChatRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<ChatRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid JSON: " + ex.Message },
                    JsonOpts, statusCode: 400);
            }

            if (req is null || req.Messages is null || req.Messages.Length == 0)
            {
                return Results.Json(new { error = "messages[] required" },
                    JsonOpts, statusCode: 400);
            }

            var messages = req.Messages.Select(m =>
                new ChatMessage(MapRole(m.Role), m.Content ?? string.Empty)).ToList();

            try
            {
                var response = await client.GetResponseAsync(messages, cancellationToken: ctx.RequestAborted);
                return Results.Json(new
                {
                    content = response.Text,
                    finishReason = response.FinishReason?.Value,
                    modelId = response.ModelId,
                }, JsonOpts);
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new { error = "canceled" }, JsonOpts, statusCode: 499);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message, type = ex.GetType().Name },
                    JsonOpts, statusCode: 502);
            }
        }).ExcludeFromDescription();

        return endpoints;
    }

    private static ChatRole MapRole(string? role)
    {
        if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)) return ChatRole.System;
        if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)) return ChatRole.Assistant;
        if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase)) return ChatRole.Tool;
        return ChatRole.User;
    }

    private static async Task<object?> ProbeOllamaAsync(CancellationToken ct)
    {
        // Ollama exposes /api/tags listing available models. A 200 +
        // valid JSON is the canonical 'I am here' signal.
        try
        {
            using var resp = await ProbeHttp.GetAsync(
                new Uri("http://127.0.0.1:11434/api/tags"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var models = doc.RootElement.TryGetProperty("models", out var arr)
                && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray()
                    .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToArray()
                : Array.Empty<string?>();
            return new
            {
                endpoint = "http://127.0.0.1:11434",
                provider = "ollama",
                models,
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<object?> ProbeLmStudioAsync(CancellationToken ct)
    {
        // LM Studio speaks the OpenAI shape -- /v1/models lists loaded
        // models. Same probe pattern, different shape.
        try
        {
            using var resp = await ProbeHttp.GetAsync(
                new Uri("http://127.0.0.1:1234/v1/models"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var models = doc.RootElement.TryGetProperty("data", out var arr)
                && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray()
                    .Select(m => m.TryGetProperty("id", out var n) ? n.GetString() : null)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToArray()
                : Array.Empty<string?>();
            return new
            {
                endpoint = "http://127.0.0.1:1234",
                provider = "lmstudio",
                models,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Minimal-subset chat request shape -- one role + content pair per message.</summary>
    private sealed record ChatRequest(ChatRequestMessage[] Messages, string? Model);

    private sealed record ChatRequestMessage(string Role, string Content);
}
