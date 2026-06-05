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
            (BowireAiRuntime runtime, IServiceProvider sp) =>
        {
            // hasClient reflects the live runtime state so Settings-UI
            // edits show up immediately. hostManaged tells the UI that
            // an embedded host registered its own IChatClient before
            // AddBowireAi — in that case the save endpoint persists the
            // user's pick but doesn't swap the active client.
            var registered = sp.GetService<IChatClient>();
            var hostManaged = registered is not null and not MutableChatClient;
            var opts = runtime.Options;
            return Results.Json(new
            {
                hasClient = hostManaged || runtime.Current is not null,
                hostManaged,
                opts.ProviderId,
                opts.Endpoint,
                opts.Model,
                opts.AutoDetectLocal,
            }, JsonOpts);
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/ai/config",
            async (HttpContext ctx, BowireAiRuntime runtime, IServiceProvider sp) =>
        {
            // Host-managed mode: save the pick to disk for the next
            // start so the workbench stays a UI surface for AI config,
            // but don't swap the live IChatClient out from under the
            // host that owns the lifetime.
            var registered = sp.GetService<IChatClient>();
            var hostManaged = registered is not null and not MutableChatClient;

            BowireAiConfigUpdate? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<BowireAiConfigUpdate>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid JSON: " + ex.Message },
                    JsonOpts, statusCode: 400);
            }

            if (body is null)
            {
                return Results.Json(new { error = "Request body required." },
                    JsonOpts, statusCode: 400);
            }

            var current = runtime.Options;
            var next = new BowireAiOptions
            {
                ProviderId = string.IsNullOrWhiteSpace(body.ProviderId) ? current.ProviderId : body.ProviderId!.Trim(),
                Endpoint = string.IsNullOrWhiteSpace(body.Endpoint) ? current.Endpoint : body.Endpoint!.Trim(),
                Model = string.IsNullOrWhiteSpace(body.Model) ? current.Model : body.Model!.Trim(),
                AutoDetectLocal = body.AutoDetectLocal ?? current.AutoDetectLocal,
            };

            // Endpoint sanity check — anything outside http/https is a
            // configuration mistake (file://, javascript:, &c.). We
            // catch it here so the user gets a clear 400 instead of a
            // confusing OllamaApiClient construction failure.
            if (!Uri.TryCreate(next.Endpoint, UriKind.Absolute, out var u)
                || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            {
                return Results.Json(new { error = "Endpoint must be an absolute http(s) URL." },
                    JsonOpts, statusCode: 400);
            }

            try
            {
                BowireAiUserConfigStore.Save(next);
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { error = "Failed to persist ai-config.json: " + ex.Message },
                    JsonOpts, statusCode: 500);
            }

            BowireAiOptions applied;
            if (hostManaged)
            {
                // Persisted for next start; live client owned by the host.
                applied = next;
            }
            else
            {
                applied = runtime.Update(next);
            }

            return Results.Json(new
            {
                saved = true,
                hostManaged,
                applied.ProviderId,
                applied.Endpoint,
                applied.Model,
                applied.AutoDetectLocal,
            }, JsonOpts);
        }).ExcludeFromDescription();

        // #61 — findings triage. Takes a scan / fuzz finding plus the
        // matched evidence and returns the model's "is this real" verdict
        // + a fix suggestion. The shape is finding-agnostic so both the
        // existing fuzz panel (semantics-menu.js) and a future Nuclei
        // findings panel can call the same endpoint.
        endpoints.MapPost($"{basePath}/api/ai/triage",
            async (HttpContext ctx, [Microsoft.AspNetCore.Mvc.FromServices] IChatClient? client) =>
        {
            if (client is null)
            {
                return Results.Json(
                    new { error = "No IChatClient registered. Connect a model via Settings → AI first." },
                    JsonOpts, statusCode: 503);
            }

            TriageRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<TriageRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid JSON: " + ex.Message },
                    JsonOpts, statusCode: 400);
            }

            if (req is null || string.IsNullOrWhiteSpace(req.Title))
            {
                return Results.Json(new { error = "title required" }, JsonOpts, statusCode: 400);
            }

            var system = """
                You are a senior application-security engineer triaging a vulnerability finding.
                Given the finding metadata + matched evidence, respond ONLY with a single JSON object:
                {
                  "realScore": <integer 0-100>,    // 0 = clearly false positive, 100 = clearly real
                  "reasoning": "<one sentence>",   // why you scored it that way
                  "fix": "<one or two sentences>"  // minimal code or config change that would close the gap
                }
                Be conservative. When the evidence is thin, score below 50 and say what would confirm it.
                Never invent CVEs or product names that aren't in the finding.
                """;

            var user = BuildTriagePrompt(req);

            try
            {
                var response = await client.GetResponseAsync(
                    [
                        new ChatMessage(ChatRole.System, system),
                        new ChatMessage(ChatRole.User, user),
                    ],
                    cancellationToken: ctx.RequestAborted);
                var verdict = TryParseVerdict(response.Text);
                return Results.Json(new
                {
                    verdict.RealScore,
                    verdict.Reasoning,
                    verdict.Fix,
                    raw = response.Text,
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

        endpoints.MapPost($"{basePath}/api/ai/chat",
            async (HttpContext ctx, [Microsoft.AspNetCore.Mvc.FromServices] IChatClient? client) =>
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

    /// <summary>
    /// Patch shape for <c>POST /api/ai/config</c>. All fields nullable so
    /// the Settings UI can submit partial updates (e.g. swap the model
    /// without re-stating the endpoint).
    /// </summary>
    private sealed record BowireAiConfigUpdate(
        string? ProviderId,
        string? Endpoint,
        string? Model,
        bool? AutoDetectLocal);

    /// <summary>
    /// Request shape for <c>POST /api/ai/triage</c>. All fields except
    /// <see cref="Title"/> are optional so the same endpoint serves
    /// scan findings, fuzz panel rows, and manual triage from the UI.
    /// </summary>
    private sealed record TriageRequest(
        string Title,
        string? Category,        // e.g. "auth-bypass", "idor", "injection-sqli"
        string? Evidence,        // the raw matched text the scanner / fuzzer pulled
        string? Method,          // HTTP verb / gRPC method etc.
        string? Endpoint,        // URL / service.method
        string? StatusCode,
        string? Protocol,        // "rest", "grpc", "graphql", &c.
        string? Notes);          // freeform context the UI wants to pass through

    private sealed record TriageVerdict(int RealScore, string Reasoning, string Fix);

    private static string BuildTriagePrompt(TriageRequest req)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Finding title: ").AppendLine(req.Title);
        if (!string.IsNullOrWhiteSpace(req.Category)) sb.Append("Category: ").AppendLine(req.Category);
        if (!string.IsNullOrWhiteSpace(req.Protocol)) sb.Append("Protocol: ").AppendLine(req.Protocol);
        if (!string.IsNullOrWhiteSpace(req.Method)) sb.Append("Method: ").AppendLine(req.Method);
        if (!string.IsNullOrWhiteSpace(req.Endpoint)) sb.Append("Endpoint: ").AppendLine(req.Endpoint);
        if (!string.IsNullOrWhiteSpace(req.StatusCode)) sb.Append("Status code: ").AppendLine(req.StatusCode);
        if (!string.IsNullOrWhiteSpace(req.Notes)) sb.Append("Notes: ").AppendLine(req.Notes);
        if (!string.IsNullOrWhiteSpace(req.Evidence))
        {
            sb.AppendLine("Matched evidence:");
            sb.AppendLine("---");
            // Cap evidence at 4k so a 100k-line response body can't blow
            // the local model's context window in one prompt.
            var ev = req.Evidence!.Length > 4000 ? req.Evidence[..4000] + "\n…[truncated]" : req.Evidence;
            sb.AppendLine(ev);
            sb.AppendLine("---");
        }
        sb.AppendLine("Respond with the JSON verdict only.");
        return sb.ToString();
    }

    /// <summary>
    /// Best-effort parse of the model's JSON verdict. Local models
    /// sometimes wrap the JSON in prose / markdown fences; we
    /// extract the first <c>{...}</c> block and parse that. Falls
    /// back to a 50-score "couldn't parse" verdict so the UI always
    /// renders something useful instead of an empty row.
    /// </summary>
    private static TriageVerdict TryParseVerdict(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new TriageVerdict(50, "Model returned no output — manual review required.", "");

        var text = rawText.Trim();
        var firstBrace = text.IndexOf('{', StringComparison.Ordinal);
        var lastBrace = text.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
            return new TriageVerdict(50, rawText.Length > 240 ? rawText[..240] + "…" : rawText, "");

        var slice = text[firstBrace..(lastBrace + 1)];
        try
        {
            using var doc = JsonDocument.Parse(slice);
            var root = doc.RootElement;
            var score = root.TryGetProperty("realScore", out var s) && s.ValueKind == JsonValueKind.Number
                ? Math.Clamp(s.GetInt32(), 0, 100)
                : 50;
            var reasoning = root.TryGetProperty("reasoning", out var r) ? r.GetString() ?? "" : "";
            var fix = root.TryGetProperty("fix", out var f) ? f.GetString() ?? "" : "";
            return new TriageVerdict(score, reasoning, fix);
        }
        catch (JsonException)
        {
            return new TriageVerdict(50, rawText.Length > 240 ? rawText[..240] + "…" : rawText, "");
        }
    }
}
