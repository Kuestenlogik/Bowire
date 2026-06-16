// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Auth;
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
            (BowireAiRuntime runtime, IServiceProvider sp, HttpContext ctx) =>
        {
            // hasClient reflects the live runtime state so Settings-UI
            // edits show up immediately. hostManaged tells the UI that
            // an embedded host registered its own IChatClient before
            // AddBowireAi — in that case the save endpoint persists the
            // user's pick but doesn't swap the active client.
            var registered = sp.GetService<IChatClient>();
            var hostManaged = registered is not null and not MutableChatClient;
            // #116 Phase 3 — per-workspace override resolution. When the
            // UI passes ?workspaceId=X we prefer that workspace's
            // override file; falls back to the global file; falls back
            // to the runtime defaults. hasOverride tells the UI to
            // render the 'override' state in the Settings tab.
            var workspaceId = ctx.Request.Query["workspaceId"].ToString();
            var hasOverride = BowireAiUserConfigStore.HasOverride(workspaceId);
            var resolved = (!string.IsNullOrEmpty(workspaceId)
                            ? BowireAiUserConfigStore.TryLoad(workspaceId)
                            : null)
                          ?? runtime.Options;
            return Results.Json(new
            {
                hasClient = hostManaged || runtime.Current is not null,
                hostManaged,
                resolved.ProviderId,
                resolved.Endpoint,
                resolved.Model,
                // Never echo the API key back over the wire — surface a
                // boolean so the UI can show "key is set" without exposing
                // the value. The save path treats an empty ApiKey as
                // "leave existing in place", so users editing the model
                // don't have to re-type the key.
                hasApiKey = !string.IsNullOrEmpty(resolved.ApiKey),
                resolved.AutoDetectLocal,
                hasOverride,
                workspaceId = string.IsNullOrEmpty(workspaceId) ? null : workspaceId,
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
                // Empty/whitespace ApiKey means "leave existing in place"
                // so the user can swap models without re-typing their key.
                // Explicit clear is signalled by sending the sentinel
                // string "__bowire_clear__" instead of an empty value.
                ApiKey = string.IsNullOrWhiteSpace(body.ApiKey)
                    ? current.ApiKey
                    : (body.ApiKey == "__bowire_clear__" ? null : body.ApiKey!.Trim()),
                AutoDetectLocal = body.AutoDetectLocal ?? current.AutoDetectLocal,
            };

            // Endpoint sanity check. http(s) URLs accepted everywhere;
            // the MCP-client-reversal provider also accepts the
            // "stdio:command" form (Phase 4) since that's the
            // documented MCP-host-as-gateway shape. Anything else
            // (file://, javascript:, &c.) is a config mistake.
            var isMcp = string.Equals(next.ProviderId, "mcp", StringComparison.OrdinalIgnoreCase);
            var isStdioMcp = isMcp && next.Endpoint.StartsWith("stdio:", StringComparison.OrdinalIgnoreCase);
            if (!isStdioMcp)
            {
                if (!Uri.TryCreate(next.Endpoint, UriKind.Absolute, out var u)
                    || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                {
                    return Results.Json(new
                    {
                        error = isMcp
                            ? "MCP endpoint must be an absolute http(s) URL or a 'stdio:<command>' string."
                            : "Endpoint must be an absolute http(s) URL.",
                    }, JsonOpts, statusCode: 400);
                }
            }

            try
            {
                // #116 Phase 3 — when the UI passes workspaceId, write
                // the per-workspace override file. Without it the
                // global file is written and every workspace inherits
                // the new value.
                var workspaceId = ctx.Request.Query["workspaceId"].ToString();
                BowireAiUserConfigStore.Save(
                    next,
                    string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId);
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { error = "Failed to persist ai-config.json: " + ex.Message },
                    JsonOpts, statusCode: 500);
            }

            // Host-managed: persisted for next start; live client owned by the host.
            var applied = hostManaged ? next : runtime.Update(next);

            return Results.Json(new
            {
                saved = true,
                hostManaged,
                applied.ProviderId,
                applied.Endpoint,
                applied.Model,
                hasApiKey = !string.IsNullOrEmpty(applied.ApiKey),
                applied.AutoDetectLocal,
            }, JsonOpts);
        }).ExcludeFromDescription();

        // #116 Phase 3 — DELETE /api/ai/config?workspaceId=X drops a
        // workspace's per-workspace override so subsequent loads fall
        // back to the global config. No-op when no override exists.
        endpoints.MapDelete($"{basePath}/api/ai/config",
            (HttpContext ctx, BowireAiRuntime runtime, IServiceProvider sp) =>
        {
            var workspaceId = ctx.Request.Query["workspaceId"].ToString();
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return Results.Json(
                    new { error = "workspaceId query parameter required." },
                    JsonOpts, statusCode: 400);
            }
            BowireAiUserConfigStore.RemoveOverride(workspaceId);
            // Hot-swap the runtime back to the global config when not
            // host-managed so the active client reflects what's now
            // resolved for this workspace.
            var registered = sp.GetService<IChatClient>();
            var hostManaged = registered is not null and not MutableChatClient;
            BowireAiOptions applied;
            if (hostManaged)
            {
                applied = BowireAiUserConfigStore.TryLoad() ?? runtime.Options;
            }
            else
            {
                var globalOpts = BowireAiUserConfigStore.TryLoad() ?? new BowireAiOptions
                {
                    ProviderId = runtime.Options.ProviderId,
                    Endpoint = runtime.Options.Endpoint,
                    Model = runtime.Options.Model,
                    AutoDetectLocal = runtime.Options.AutoDetectLocal,
                };
                applied = runtime.Update(globalOpts);
            }
            return Results.Json(new
            {
                deleted = true,
                hostManaged,
                applied.ProviderId,
                applied.Endpoint,
                applied.Model,
                hasApiKey = !string.IsNullOrEmpty(applied.ApiKey),
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

        // #59 — threat-model. Takes the discovered service surface and
        // returns a ranked list of "where to scan first" entries. The
        // model rates each endpoint 0-10 on likely attack-surface risk
        // and suggests Nuclei template families for the top entries.
        // Driven from the AI side-panel: one-click "rank these N
        // endpoints" → ranked list with per-row "scan with Nuclei"
        // shortcut into the existing bowire-scan flow.
        endpoints.MapPost($"{basePath}/api/ai/threat-model",
            async (HttpContext ctx, [Microsoft.AspNetCore.Mvc.FromServices] IChatClient? client) =>
        {
            if (client is null)
            {
                return Results.Json(
                    new { error = "No IChatClient registered. Connect a model via Settings → AI first." },
                    JsonOpts, statusCode: 503);
            }

            ThreatModelRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<ThreatModelRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid JSON: " + ex.Message },
                    JsonOpts, statusCode: 400);
            }

            if (req is null || req.Endpoints is null || req.Endpoints.Length == 0)
            {
                return Results.Json(new { error = "endpoints[] required" },
                    JsonOpts, statusCode: 400);
            }

            // Cap at 200 endpoints so a 5 k-service host doesn't blow
            // the model's context. The "ranked top N" framing means the
            // model would discard the long tail anyway.
            var endpointSlice = req.Endpoints.Length > 200
                ? req.Endpoints[..200]
                : req.Endpoints;
            var topN = req.TopN is int n && n is > 0 and <= 50 ? n : 10;

            var system = """
                You are a senior application-security engineer doing a threat-model pass.
                Given a list of API endpoints, rank the TOP candidates by attack-surface risk.
                Consider: IDOR / mass-assignment for resource-id paths, SSRF for url-receiving params, auth-bypass for protected resources, injection (SQL / command / template) for string-in / string-out shapes, parameter tampering for state-changing verbs, sensitive data exposure for response shapes.
                Respond ONLY with a single JSON object of the shape:
                {
                  "ranked": [
                    {
                      "endpointId": "<id from input>",
                      "risk": <integer 0-10>,
                      "why": "<one sentence>",
                      "suggestedTemplates": ["<nuclei-template-family>", ...]
                    },
                    ...
                  ]
                }
                Be conservative — score below 5 when the endpoint is read-only / well-scoped / lacks identifiable risk. Suggested templates use family names like "auth-bypass", "idor", "mass-assignment", "injection-sqli", "injection-cmdi", "ssrf", "path-traversal", "open-redirect".
                """;
            var user = BuildThreatModelPrompt(endpointSlice, topN);

            try
            {
                var response = await client.GetResponseAsync(
                    [
                        new ChatMessage(ChatRole.System, system),
                        new ChatMessage(ChatRole.User, user),
                    ],
                    cancellationToken: ctx.RequestAborted);
                var ranking = TryParseThreatModel(response.Text, topN);
                return Results.Json(new
                {
                    ranking.Ranked,
                    raw = response.Text,
                    modelId = response.ModelId,
                    inputCount = endpointSlice.Length,
                    truncated = req.Endpoints.Length > endpointSlice.Length,
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

        // #60 — Nuclei template suggestion. Takes an endpoint's shape
        // + a target vulnerability class and asks the model to emit a
        // Nuclei v3 YAML template. Drives the "generate me a scan
        // probe for this endpoint" flow from the threat-model ranked
        // list (per-row) plus a standalone per-method path later.
        endpoints.MapPost($"{basePath}/api/ai/template-suggest",
            async (HttpContext ctx, [Microsoft.AspNetCore.Mvc.FromServices] IChatClient? client) =>
        {
            if (client is null)
            {
                return Results.Json(
                    new { error = "No IChatClient registered. Connect a model via Settings → AI first." },
                    JsonOpts, statusCode: 503);
            }

            TemplateSuggestRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<TemplateSuggestRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid JSON: " + ex.Message },
                    JsonOpts, statusCode: 400);
            }

            if (req is null || string.IsNullOrWhiteSpace(req.Path) || string.IsNullOrWhiteSpace(req.Class))
            {
                return Results.Json(new { error = "path + class required" }, JsonOpts, statusCode: 400);
            }
            if (!IsKnownTemplateClass(req.Class))
            {
                return Results.Json(new { error = "unknown class — supported: " + string.Join(", ", KnownTemplateClasses) },
                    JsonOpts, statusCode: 400);
            }

            var system = """
                You are a security engineer writing a Nuclei v3 YAML template that probes a single endpoint for a specific vulnerability class.
                Output ONLY the YAML — no fences, no preamble, no commentary.
                Required structure:
                  id: <kebab-case-id>
                  info:
                    name: <short title>
                    author: bowire-ai
                    severity: <info|low|medium|high|critical>
                    description: <one sentence>
                    tags: [<class>, ai-suggested, bowire]
                  http:
                    - method: <verb>
                      path:
                        - "{{BaseURL}}<path>"
                      headers: { ... }     # only when meaningful
                      body: '...'          # only for POST/PUT/PATCH
                      matchers-condition: and
                      matchers:
                        - type: status
                          status: [<expected-status-int>]
                        - type: word
                          part: body
                          words: [<distinctive-substring>]
                Constraints: keep paths exactly as given (do not invent new endpoints), use {{BaseURL}} as the host placeholder, use {{rand_text_alphanumeric(8)}} when you need a random token, never inject real secrets. Match conservatively — false positives are worse than false negatives here.
                """;
            var user = BuildTemplateSuggestPrompt(req);

            try
            {
                var response = await client.GetResponseAsync(
                    [
                        new ChatMessage(ChatRole.System, system),
                        new ChatMessage(ChatRole.User, user),
                    ],
                    cancellationToken: ctx.RequestAborted);
                var yaml = ExtractYaml(response.Text);
                return Results.Json(new
                {
                    yaml,
                    raw = response.Text,
                    modelId = response.ModelId,
                    suggestedFilename = BuildFilename(req),
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

        // #60 — save a generated template to the user-scoped template
        // store. Path resolves through IBowireUserStore so single-user
        // installs land at ~/.bowire/templates/, multi-tenant slot
        // per-identity. The scanner reads --nuclei <dir>; documenting
        // that ~/.bowire/templates is the default Bowire-AI templates
        // home means `bowire scan --nuclei ~/.bowire/templates ...`
        // just works.
        endpoints.MapPost($"{basePath}/api/ai/template-save",
            async (HttpContext ctx) =>
        {
            TemplateSaveRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<TemplateSaveRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid JSON: " + ex.Message },
                    JsonOpts, statusCode: 400);
            }

            if (req is null || string.IsNullOrWhiteSpace(req.Filename) || string.IsNullOrWhiteSpace(req.Yaml))
            {
                return Results.Json(new { error = "filename + yaml required" },
                    JsonOpts, statusCode: 400);
            }

            // Guard against path traversal and non-YAML extensions. The
            // filename must be a single segment ending in .yaml/.yml so
            // a malicious payload can't write to /etc/passwd or similar.
            var safe = Path.GetFileName(req.Filename.Trim());
            if (string.IsNullOrEmpty(safe)
                || safe != req.Filename.Trim()
                || (!safe.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                    && !safe.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Json(
                    new { error = "filename must be a single segment ending in .yaml or .yml" },
                    JsonOpts, statusCode: 400);
            }

            try
            {
                var dir = BowireUserContext.GetUserPath("templates");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, safe);
                await File.WriteAllTextAsync(path, req.Yaml, ctx.RequestAborted);
                return Results.Json(new { saved = true, path }, JsonOpts);
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { error = "Failed to save template: " + ex.Message },
                    JsonOpts, statusCode: 500);
            }
        }).ExcludeFromDescription();

        // #62 — schema-aware fuzz values. Takes a field's type +
        // schema + surrounding method context and asks the model for
        // 20 boundary values (overflow, format-coercion, encoding,
        // injection-adjacent). The user picks ≤ 5 from the response
        // and the existing /api/security/fuzz pipeline replays each
        // one. Severity hint is advisory only — the model never
        // auto-classifies, that path stays with Nuclei.
        endpoints.MapPost($"{basePath}/api/ai/fuzz-values",
            async (HttpContext ctx, [Microsoft.AspNetCore.Mvc.FromServices] IChatClient? client) =>
        {
            if (client is null)
            {
                return Results.Json(
                    new { error = "No IChatClient registered. Connect a model via Settings → AI first." },
                    JsonOpts, statusCode: 503);
            }

            FuzzValuesRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<FuzzValuesRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid JSON: " + ex.Message },
                    JsonOpts, statusCode: 400);
            }

            if (req is null
                || string.IsNullOrWhiteSpace(req.FieldName)
                || string.IsNullOrWhiteSpace(req.FieldType))
            {
                return Results.Json(new { error = "fieldName + fieldType required" },
                    JsonOpts, statusCode: 400);
            }

            var system = """
                You are a security tester generating boundary fuzz inputs for a single field of an API request.
                Produce values that exercise edge cases — boundary (min/max/zero/negative/overflow), format-coercion (string in numeric, numeric in string, locale-dependent), encoding (Unicode normalisation forms, surrogate pairs, BOM, RTL override, percent-encoding tricks), and injection-adjacent (looks-malicious-but-isn't, e.g. quote-only, comment-only, single-byte tag fragments).
                Respond ONLY with a single JSON object:
                {
                  "values": [
                    {"value": <string|number|bool|null>, "why": "<short reason>", "severity": "<info|low|medium>"},
                    ...
                  ]
                }
                Constraints:
                - Output up to 20 distinct values.
                - Never include real PII or secrets (no real names, emails, addresses, tokens, credentials).
                - The "severity" hint is advisory — the workbench treats it as cosmetic; the user, not you, classifies findings.
                - For string fields, JSON-stringify the value (escape internal quotes / backslashes).
                - For numeric fields, emit numbers when possible; fall back to JSON strings for overflow / scientific-notation edge cases that don't fit a JS number.
                """;
            var user = BuildFuzzValuesPrompt(req);

            try
            {
                var response = await client.GetResponseAsync(
                    [
                        new ChatMessage(ChatRole.System, system),
                        new ChatMessage(ChatRole.User, user),
                    ],
                    cancellationToken: ctx.RequestAborted);
                var values = TryParseFuzzValues(response.Text);
                return Results.Json(new
                {
                    values,
                    raw = response.Text,
                    modelId = response.ModelId,
                    fieldName = req.FieldName,
                    fieldType = req.FieldType,
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
            async (HttpContext ctx,
                [Microsoft.AspNetCore.Mvc.FromServices] IChatClient? client,
                [Microsoft.AspNetCore.Mvc.FromServices] BowireAiRuntime runtime) =>
        {
            const string Instance = "/api/ai/chat";

            if (client is null)
            {
                return Endpoints.BowireEndpointHelpers.Problem(
                    type: "urn:bowire:ai:no-chat-client",
                    title: "No AI chat client registered",
                    status: 503,
                    detail: "No IChatClient was registered on the host. Install Kuestenlogik.Bowire.Ai and call AddBowireAi(), or register your own IChatClient.",
                    instance: Instance,
                    extensions: new Dictionary<string, object?> {
                        ["links"] = new[] { new { rel = "configure", href = "/#settings/ai" } }
                    });
            }

            ChatRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<ChatRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Endpoints.BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Request body isn't valid JSON",
                    status: 400,
                    detail: ex.Message,
                    instance: Instance);
            }

            if (req is null || req.Messages is null || req.Messages.Length == 0)
            {
                return Endpoints.BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Request is missing a 'messages' array",
                    status: 400,
                    detail: "The chat endpoint requires { \"messages\": [{ role, content }, ...] } in the request body.",
                    instance: Instance);
            }

            var messages = req.Messages.Select(m =>
                new ChatMessage(MapRole(m.Role), m.Content ?? string.Empty)).ToList();

            // #108 + #109 — MCP-style tools. Phase 2 (read-only) always
            // available when context is provided; Phase 3+4 (invoke +
            // open_method) gated on the per-session AllowInvoke flag the
            // frontend sets via a toggle in the AI drawer.
            var workbenchContext = req.Context;
            var sp = ctx.RequestServices;
            var tools = BuildWorkbenchTools(workbenchContext, sp);
            var chatOptions = tools.Count > 0 ? new ChatOptions { Tools = tools } : null;

            try
            {
                var response = await client.GetResponseAsync(messages, chatOptions, ctx.RequestAborted);
                // Surface every tool call the model made so the frontend
                // can render them as visible steps in the transcript
                // ("Looked up pet.getPetById"). Helps the user trust
                // what the AI did rather than guessing from the prose.
                var toolCalls = response.Messages
                    .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                    .Select(f => new {
                        name = f.Name,
                        arguments = f.Arguments,
                    })
                    .ToArray();
                return Results.Json(new
                {
                    content = response.Text,
                    finishReason = response.FinishReason?.Value,
                    modelId = response.ModelId,
                    toolCalls = toolCalls.Length > 0 ? toolCalls : null,
                }, JsonOpts);
            }
            catch (OperationCanceledException)
            {
                return Endpoints.BowireEndpointHelpers.Problem(
                    type: "urn:bowire:canceled",
                    title: "Request was canceled",
                    status: 499,
                    instance: Instance);
            }
            catch (HttpRequestException hre)
                when (hre.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Ollama (and most OpenAI-compatible servers) return 404
                // when the configured model isn't installed on the
                // endpoint. The raw HttpRequestException.Message is
                // "Response status code does not indicate success: 404
                // (Not Found)." which reads like a Bowire-side bug — surface
                // a structured error the UI can render with the model
                // name + actionable fix (#87, #88).
                var modelName = runtime.Options.Model ?? "(unknown)";
                var endpoint = runtime.Options.Endpoint ?? "(unknown)";
                return Endpoints.BowireEndpointHelpers.Problem(
                    type: "urn:bowire:ai:model-not-found",
                    title: $"Model '{modelName}' is not installed on the AI server",
                    status: 502,
                    detail: $"The AI server at {endpoint} returned 404 for the configured model '{modelName}'. "
                          + $"Pull it with `ollama pull {modelName}` (or your provider's equivalent), "
                          + "or pick a different model in Settings → AI.",
                    instance: Instance,
                    extensions: new Dictionary<string, object?> {
                        ["model"] = modelName,
                        ["endpoint"] = endpoint,
                        ["links"] = new[] { new { rel = "configure", href = "/#settings/ai" } }
                    });
            }
            catch (Exception ex)
            {
                return Endpoints.BowireEndpointHelpers.Problem(
                    type: "urn:bowire:ai:provider-error",
                    title: "The AI provider returned an error",
                    status: 502,
                    detail: ex.Message,
                    instance: Instance,
                    extensions: new Dictionary<string, object?> {
                        ["exceptionType"] = ex.GetType().Name
                    });
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

    /// <summary>
    /// Build the read-only AIFunction tool list for a chat call
    /// (#108 — Phase 2 of #89). Tools close over the workbench-state
    /// snapshot the frontend shipped in the request body. Empty list
    /// when there's no context — the chat still works, just without
    /// tools, falling back to system-prompt-only grounding.
    /// </summary>
    private static List<AITool> BuildWorkbenchTools(WorkbenchContext? wbCtx, IServiceProvider sp)
    {
        var tools = new List<AITool>();
        if (wbCtx is null) return tools;

        var services = wbCtx.Services ?? [];

        tools.Add(AIFunctionFactory.Create(
            () => services.Select(s => new
            {
                name = s.Name,
                protocol = s.Protocol,
                originUrl = s.OriginUrl,
                methodCount = s.Methods?.Length ?? 0,
                methods = s.Methods?.Select(m => m.Name).ToArray() ?? [],
            }).ToArray(),
            name: "bowire_list_services",
            description: "List every API service currently discovered in the workbench. Returns each service's name, protocol, origin URL, and the names of its methods. Call this first to see what's available before drilling into a specific method."));

        tools.Add(AIFunctionFactory.Create(
            (string service, string method) =>
            {
                var svc = services.FirstOrDefault(s => string.Equals(s.Name, service, StringComparison.OrdinalIgnoreCase));
                if (svc is null) return (object)new { error = $"Service '{service}' is not in the workbench. Call bowire_list_services to see the available services." };
                var m = svc.Methods?.FirstOrDefault(x => string.Equals(x.Name, method, StringComparison.OrdinalIgnoreCase));
                if (m is null) return new { error = $"Method '{method}' is not on service '{service}'. Methods available: " + string.Join(", ", svc.Methods?.Select(x => x.Name) ?? []) };
                return new
                {
                    service = svc.Name,
                    method = m.Name,
                    protocol = svc.Protocol,
                    originUrl = svc.OriginUrl,
                    description = m.Description,
                    methodType = m.MethodType,
                    inputType = m.InputTypeName,
                    inputFields = m.InputFields,
                    outputType = m.OutputTypeName,
                };
            },
            name: "bowire_describe_method",
            description: "Get the full input/output schema for one method on one service, plus its origin URL, protocol, and method type. Use after bowire_list_services to drill into a specific method the user is asking about."));

        tools.Add(AIFunctionFactory.Create(
            (int? limit) =>
            {
                var recent = wbCtx.Recent ?? [];
                var take = limit is > 0 ? Math.Min(limit.Value, recent.Length) : Math.Min(5, recent.Length);
                return recent.Take(take).ToArray();
            },
            name: "bowire_recent_history",
            description: "Return the last N recent calls (default 5) with service, method, and status. Use when the user references prior calls (\"why did that 500?\") or to spot patterns across recent invocations."));

        // #109 — Phase 4: navigation tool. Always available (no side
        // effects beyond UI state); the frontend reads the tool-call
        // name out of the response's toolCalls array and dispatches
        // selectMethod() locally. The tool body itself just records
        // the request — the actual navigation happens client-side.
        tools.Add(AIFunctionFactory.Create(
            (string service, string method) =>
            {
                var svc = services.FirstOrDefault(s => string.Equals(s.Name, service, StringComparison.OrdinalIgnoreCase));
                if (svc is null) return (object)new { ok = false, error = $"Service '{service}' is not in the workbench." };
                var m = svc.Methods?.FirstOrDefault(x => string.Equals(x.Name, method, StringComparison.OrdinalIgnoreCase));
                if (m is null) return new { ok = false, error = $"Method '{method}' is not on service '{service}'." };
                return new { ok = true, service = svc.Name, method = m.Name, hint = "The workbench will navigate to this method when the user confirms." };
            },
            name: "bowire_open_method",
            description: "Ask the workbench to select a method, so the user lands on its request pane and can hit Send themselves. Use when the user asks \"open X\" or when you want to point the user at the right next step. Returns ok=true on success; the frontend handles the actual navigation."));

        // #109 — Phase 3: write-side invoke tool. ONLY registered when
        // the user explicitly opted in via the drawer toggle
        // (AllowInvoke). When off, the model literally cannot try —
        // the tool doesn't appear in its tool list and the chat is
        // safe by default against accidental production mutations.
        if (wbCtx.AllowInvoke == true)
        {
            tools.Add(AIFunctionFactory.Create(
                async (string service, string method, string? messageJson) =>
                {
                    var svc = services.FirstOrDefault(s => string.Equals(s.Name, service, StringComparison.OrdinalIgnoreCase));
                    if (svc is null) return (object)new { ok = false, error = $"Service '{service}' is not in the workbench." };
                    var m = svc.Methods?.FirstOrDefault(x => string.Equals(x.Name, method, StringComparison.OrdinalIgnoreCase));
                    if (m is null) return new { ok = false, error = $"Method '{method}' is not on service '{service}'." };

                    var registry = sp.GetService<BowireProtocolRegistry>();
                    if (registry is null) return new { ok = false, error = "Protocol registry not available — Bowire core isn't wired." };

                    var protocol = svc.Protocol is not null
                        ? registry.GetById(svc.Protocol)
                        : (registry.Protocols.Count > 0 ? registry.Protocols[0] : null);
                    if (protocol is null) return new { ok = false, error = $"No protocol plugin loaded for '{svc.Protocol ?? "(default)"}'." };

                    var serverUrl = svc.OriginUrl ?? wbCtx.ServerUrls?.FirstOrDefault() ?? string.Empty;
                    var body = string.IsNullOrEmpty(messageJson) ? "{}" : messageJson;

                    try
                    {
                        var result = await protocol.InvokeAsync(
                            serverUrl, service, method,
                            [body], showInternalServices: false, metadata: null, CancellationToken.None);

                        AppendAuditLog(new
                        {
                            ts = DateTimeOffset.UtcNow,
                            service,
                            method,
                            protocol = protocol.Id,
                            serverUrl,
                            status = result.Status,
                            durationMs = result.DurationMs,
                        });

                        return new
                        {
                            ok = true,
                            status = result.Status,
                            durationMs = result.DurationMs,
                            response = result.Response,
                        };
                    }
                    catch (Exception ex)
                    {
                        AppendAuditLog(new
                        {
                            ts = DateTimeOffset.UtcNow,
                            service,
                            method,
                            protocol = protocol.Id,
                            serverUrl,
                            error = ex.Message,
                        });
                        return new { ok = false, error = ex.Message };
                    }
                },
                name: "bowire_invoke",
                description: "Invoke a service method against its server. ONLY available when the user has explicitly enabled AI invokes in the drawer. Returns the actual response payload + status + duration. Use to test a hypothesis or to act on the user's request when they ask you to run something."));
        }

        return tools;
    }

    /// <summary>
    /// Append a single JSON line to the per-user AI-actions audit log
    /// (#109 Phase 3). Every invoke the model runs lands here with
    /// timestamp + endpoint + outcome — auditable trail for users
    /// who turned auto-invoke on. Best-effort: failures don't bubble
    /// because the audit shouldn't be able to break the chat itself.
    /// </summary>
    private static void AppendAuditLog(object entry)
    {
        try
        {
            var store = BowireUserContext.Current;
            var path = store.GetUserPath(".ai-actions.jsonl");
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(entry, JsonOpts) + "\n";
            File.AppendAllText(path, line);
        }
        catch { /* audit must not break the chat */ }
    }

    /// <summary>Minimal-subset chat request shape -- one role + content pair per message.</summary>
    private sealed record ChatRequest(
        ChatRequestMessage[] Messages,
        string? Model,
        WorkbenchContext? Context);

    private sealed record ChatRequestMessage(string Role, string Content);

    /// <summary>
    /// Workbench-state snapshot the frontend ships with every chat
    /// request (#89 Phase 2). Closes over what the AIFunction tools
    /// see: list_services / describe_method / recent_history all read
    /// from this. The frontend already builds this in ai.js'
    /// collectWorkbenchContext() — we just plumb it from system-prompt
    /// text into structured data the tools can query.
    /// </summary>
    private sealed record WorkbenchContext(
        string[]? ServerUrls,
        WorkbenchService[]? Services,
        WorkbenchHistoryEntry[]? Recent,
        // #109 Phase 3 — opt-in gate for the write-side invoke tool.
        // Off by default: bowire_invoke isn't even registered, the
        // model literally cannot call it. The user flips it on via a
        // toggle in the AI drawer when they want the AI to act
        // (per-session, never persisted to localStorage).
        bool? AllowInvoke);

    private sealed record WorkbenchService(
        string Name,
        string? Protocol,
        string? OriginUrl,
        WorkbenchMethod[]? Methods);

    private sealed record WorkbenchMethod(
        string Name,
        string? Description,
        string? MethodType,
        string? InputTypeName,
        WorkbenchField[]? InputFields,
        string? OutputTypeName);

    private sealed record WorkbenchField(
        string Name,
        string? Type,
        bool Optional);

    private sealed record WorkbenchHistoryEntry(
        string Service,
        string Method,
        string? Status);

    /// <summary>
    /// Patch shape for <c>POST /api/ai/config</c>. All fields nullable so
    /// the Settings UI can submit partial updates (e.g. swap the model
    /// without re-stating the endpoint).
    /// </summary>
    private sealed record BowireAiConfigUpdate(
        string? ProviderId,
        string? Endpoint,
        string? Model,
        string? ApiKey,
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

    /// <summary>
    /// Request shape for <c>POST /api/ai/threat-model</c>. <see cref="Endpoints"/>
    /// is the discovered service surface flattened into a per-endpoint
    /// list; <see cref="TopN"/> caps the ranked output (default 10).
    /// </summary>
    private sealed record ThreatModelRequest(
        ThreatModelEndpoint[] Endpoints,
        int? TopN);

    private sealed record ThreatModelEndpoint(
        string EndpointId,    // stable id the frontend uses to look the row back up
        string Path,          // URL path / gRPC method / topic / &c.
        string? Verb,         // HTTP verb, "publish", "subscribe", "rpc", null
        string? Protocol,     // "rest", "grpc", "graphql", "mqtt", &c.
        string? Service,      // for protocols with service grouping (gRPC, GraphQL)
        string? InputShape,   // optional JSON or proto shape summary
        string? AuthState);   // optional: "anonymous" / "authenticated" / "unknown"

    private sealed record ThreatModelRanking(ThreatModelRow[] Ranked);

    private sealed record ThreatModelRow(
        string EndpointId,
        int Risk,
        string Why,
        string[] SuggestedTemplates);

    private static string BuildThreatModelPrompt(ThreatModelEndpoint[] endpoints, int topN)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Endpoints (").Append(endpoints.Length).AppendLine("):");
        foreach (var e in endpoints)
        {
            sb.Append("- id=").Append(e.EndpointId);
            sb.Append(" path=").Append(e.Path);
            if (!string.IsNullOrEmpty(e.Verb)) sb.Append(" verb=").Append(e.Verb);
            if (!string.IsNullOrEmpty(e.Protocol)) sb.Append(" protocol=").Append(e.Protocol);
            if (!string.IsNullOrEmpty(e.Service)) sb.Append(" service=").Append(e.Service);
            if (!string.IsNullOrEmpty(e.AuthState)) sb.Append(" auth=").Append(e.AuthState);
            if (!string.IsNullOrEmpty(e.InputShape))
            {
                var shape = e.InputShape!.Length > 200 ? e.InputShape[..200] + "…" : e.InputShape;
                sb.Append(" input=").Append(shape);
            }
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.Append("Return the top ").Append(topN).AppendLine(" by risk. JSON only.");
        return sb.ToString();
    }

    /// <summary>
    /// Best-effort parse of the model's ranked-list response. Same
    /// resilience strategy as <see cref="TryParseVerdict"/> — extract
    /// the outermost <c>{...}</c> block so a markdown-fenced response
    /// still works; return an empty ranking on garbage so the UI
    /// renders a "no ranking" affordance instead of crashing. Per-row
    /// invariants enforced: <c>risk</c> clamped to [0, 10],
    /// <c>endpointId</c> stays the model's verbatim string so the
    /// frontend can correlate without trust.
    /// </summary>
    private static ThreatModelRanking TryParseThreatModel(string? rawText, int topN)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new ThreatModelRanking([]);

        var text = rawText.Trim();
        var firstBrace = text.IndexOf('{', StringComparison.Ordinal);
        var lastBrace = text.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
            return new ThreatModelRanking([]);

        var slice = text[firstBrace..(lastBrace + 1)];
        try
        {
            using var doc = JsonDocument.Parse(slice);
            if (!doc.RootElement.TryGetProperty("ranked", out var rankedEl)
                || rankedEl.ValueKind != JsonValueKind.Array)
                return new ThreatModelRanking([]);

            var rows = new List<ThreatModelRow>(Math.Min(rankedEl.GetArrayLength(), topN));
            foreach (var rowEl in rankedEl.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.Object))
            {
                var id = rowEl.TryGetProperty("endpointId", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                var risk = rowEl.TryGetProperty("risk", out var riskEl) && riskEl.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(riskEl.GetInt32(), 0, 10)
                    : 0;
                var why = rowEl.TryGetProperty("why", out var whyEl) ? whyEl.GetString() ?? "" : "";
                var templates = Array.Empty<string>();
                if (rowEl.TryGetProperty("suggestedTemplates", out var tplEl)
                    && tplEl.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>(tplEl.GetArrayLength());
                    foreach (var t in tplEl.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String))
                    {
                        if (t.GetString() is { Length: > 0 } s)
                            list.Add(s);
                    }
                    templates = list.ToArray();
                }
                rows.Add(new ThreatModelRow(id, risk, why, templates));
                if (rows.Count >= topN) break;
            }
            return new ThreatModelRanking([.. rows]);
        }
        catch (JsonException)
        {
            return new ThreatModelRanking([]);
        }
    }

    // #60 — Nuclei template suggestion records + helpers ------------

    /// <summary>
    /// Request shape for <c>POST /api/ai/template-suggest</c>. Path /
    /// class are required; the rest narrows the prompt so the model
    /// can specialise the template (e.g. emit a body block for POST).
    /// </summary>
    private sealed record TemplateSuggestRequest(
        string Path,          // URL path / gRPC method-fqn
        string Class,         // vulnerability class — see KnownTemplateClasses
        string? Verb,
        string? Protocol,
        string? Service,
        string? InputShape,
        string? AuthState,
        string? Notes);       // optional human hint to nudge the model

    private sealed record TemplateSaveRequest(string Filename, string Yaml);

    /// <summary>
    /// Vulnerability classes the model is asked to specialise on. Acts
    /// as both the input enum and the documentation of what the
    /// frontend dropdown should offer.
    /// </summary>
    private static readonly string[] KnownTemplateClasses =
    [
        "auth-bypass",
        "idor",
        "mass-assignment",
        "parameter-tampering",
        "injection-sqli",
        "injection-cmdi",
        "injection-template",
        "ssrf",
        "path-traversal",
        "open-redirect",
    ];

    private static bool IsKnownTemplateClass(string cls) =>
        Array.Exists(KnownTemplateClasses, c =>
            string.Equals(c, cls.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string BuildTemplateSuggestPrompt(TemplateSuggestRequest req)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Vulnerability class: ").AppendLine(req.Class);
        sb.Append("Path: ").AppendLine(req.Path);
        if (!string.IsNullOrWhiteSpace(req.Verb)) sb.Append("Verb: ").AppendLine(req.Verb);
        if (!string.IsNullOrWhiteSpace(req.Protocol)) sb.Append("Protocol: ").AppendLine(req.Protocol);
        if (!string.IsNullOrWhiteSpace(req.Service)) sb.Append("Service: ").AppendLine(req.Service);
        if (!string.IsNullOrWhiteSpace(req.AuthState)) sb.Append("Auth state: ").AppendLine(req.AuthState);
        if (!string.IsNullOrWhiteSpace(req.InputShape))
        {
            var shape = req.InputShape!.Length > 1500 ? req.InputShape[..1500] + "…" : req.InputShape;
            sb.AppendLine("Input shape:");
            sb.AppendLine(shape);
        }
        if (!string.IsNullOrWhiteSpace(req.Notes))
        {
            sb.Append("Notes: ").AppendLine(req.Notes);
        }
        sb.AppendLine();
        sb.AppendLine("Output the YAML template only.");
        return sb.ToString();
    }

    /// <summary>
    /// Pull the YAML out of the model's response. Local models often
    /// wrap output in ```yaml fences or add a leading "Sure, here:"
    /// line; we strip both. Returns the original text unchanged when
    /// no fences are present so a well-behaved model isn't penalised.
    /// </summary>
    private static string ExtractYaml(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return string.Empty;
        var text = rawText.Trim();
        // Try ```yaml / ``` blocks first.
        var fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var afterOpen = text.IndexOf('\n', fenceStart);
            if (afterOpen > 0)
            {
                var fenceEnd = text.IndexOf("```", afterOpen + 1, StringComparison.Ordinal);
                if (fenceEnd > afterOpen)
                {
                    return text[(afterOpen + 1)..fenceEnd].Trim();
                }
            }
        }
        return text;
    }

    /// <summary>
    /// Compose a filename from the path + class so two suggestions for
    /// the same endpoint-class pair land at the same file (overwrite
    /// rather than accumulate). Strips path-traversal characters so the
    /// suggestedFilename can't escape the templates directory even if
    /// the save endpoint's own guard is somehow bypassed.
    /// </summary>
    private static string BuildFilename(TemplateSuggestRequest req)
    {
        var pathSlug = new string([.. req.Path
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')]);
        pathSlug = pathSlug.Trim('-');
        while (pathSlug.Contains("--", StringComparison.Ordinal))
        {
            pathSlug = pathSlug.Replace("--", "-", StringComparison.Ordinal);
        }
        if (string.IsNullOrEmpty(pathSlug)) pathSlug = "endpoint";
        // Cap so a deep path doesn't blow Windows' MAX_PATH.
        if (pathSlug.Length > 60) pathSlug = pathSlug[..60];
        // CA1308: file paths use lowercase by Bowire convention; the
        // ToUpperInvariant alternative isn't relevant for filename
        // slugs that go straight to disk.
#pragma warning disable CA1308
        var classSlug = req.Class.Trim().ToLowerInvariant();
#pragma warning restore CA1308
        return $"bowire-ai-{pathSlug}-{classSlug}.yaml";
    }

    // #62 — schema-aware fuzz values records + helpers ------------

    /// <summary>
    /// Request shape for <c>POST /api/ai/fuzz-values</c>. <see cref="FieldName"/>
    /// and <see cref="FieldType"/> are required; everything else
    /// narrows the prompt so the model can specialise (e.g. emit
    /// JSON-shaped values for an object field, byte-array tricks
    /// for a bytes field).
    /// </summary>
    private sealed record FuzzValuesRequest(
        string FieldName,
        string FieldType,            // "string", "int32", "int64", "double", "bool", "bytes", or a proto/JSON-schema type name
        string? FieldSchema,         // optional: full JSON schema fragment for the field
        string? FieldExample,        // optional: a known-good value as a starting point
        string? MethodName,
        string? Service,
        string? Protocol,
        string? Notes);

    private sealed record FuzzValue(JsonElement Value, string Why, string Severity);

    private static string BuildFuzzValuesPrompt(FuzzValuesRequest req)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Field name: ").AppendLine(req.FieldName);
        sb.Append("Field type: ").AppendLine(req.FieldType);
        if (!string.IsNullOrWhiteSpace(req.FieldExample))
        {
            sb.Append("Known-good example: ").AppendLine(req.FieldExample);
        }
        if (!string.IsNullOrWhiteSpace(req.FieldSchema))
        {
            var schema = req.FieldSchema!.Length > 1500 ? req.FieldSchema[..1500] + "…" : req.FieldSchema;
            sb.AppendLine("Field schema:");
            sb.AppendLine(schema);
        }
        if (!string.IsNullOrWhiteSpace(req.MethodName))
        {
            sb.Append("Method: ").AppendLine(req.MethodName);
        }
        if (!string.IsNullOrWhiteSpace(req.Service))
        {
            sb.Append("Service: ").AppendLine(req.Service);
        }
        if (!string.IsNullOrWhiteSpace(req.Protocol))
        {
            sb.Append("Protocol: ").AppendLine(req.Protocol);
        }
        if (!string.IsNullOrWhiteSpace(req.Notes))
        {
            sb.Append("Notes: ").AppendLine(req.Notes);
        }
        sb.AppendLine();
        sb.AppendLine("Up to 20 boundary values. JSON envelope only.");
        return sb.ToString();
    }

    /// <summary>
    /// Best-effort parse of the model's value list. Same resilience
    /// strategy as the threat-model parser — extract the outermost
    /// <c>{...}</c> block so a markdown-fenced response still works;
    /// drop malformed rows so one bad entry doesn't lose the rest;
    /// cap at 20 server-side so a runaway model can't blow the
    /// frontend's picker. Severity is normalised to one of
    /// <c>info / low / medium</c> (advisory only — the workbench
    /// never auto-classifies).
    /// </summary>
    private static FuzzValue[] TryParseFuzzValues(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return [];

        var text = rawText.Trim();
        var firstBrace = text.IndexOf('{', StringComparison.Ordinal);
        var lastBrace = text.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace) return [];

        var slice = text[firstBrace..(lastBrace + 1)];
        try
        {
            // Clone the values into a stable JsonDocument so the
            // returned slice doesn't hold a reference to the doc we
            // dispose at scope-exit.
            using var doc = JsonDocument.Parse(slice);
            if (!doc.RootElement.TryGetProperty("values", out var valuesEl)
                || valuesEl.ValueKind != JsonValueKind.Array)
                return [];

            var rows = new List<FuzzValue>(Math.Min(valuesEl.GetArrayLength(), 20));
            foreach (var rowEl in valuesEl.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.Object))
            {
                if (!rowEl.TryGetProperty("value", out var valueEl)) continue;
                var why = rowEl.TryGetProperty("why", out var whyEl) ? whyEl.GetString() ?? "" : "";
                var rawSeverity = rowEl.TryGetProperty("severity", out var sevEl) ? sevEl.GetString() ?? "" : "";
                var severity = NormalizeFuzzSeverity(rawSeverity);

                // Clone the JsonElement so it survives doc disposal.
                rows.Add(new FuzzValue(valueEl.Clone(), why, severity));
                if (rows.Count >= 20) break;
            }
            return [.. rows];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string NormalizeFuzzSeverity(string raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "info";
        if (trimmed.Equals("info", StringComparison.OrdinalIgnoreCase)) return "info";
        if (trimmed.Equals("low", StringComparison.OrdinalIgnoreCase)) return "low";
        if (trimmed.Equals("medium", StringComparison.OrdinalIgnoreCase)) return "medium";
        // Higher severities collapse to "medium" — the prompt allows
        // only info/low/medium, but a misbehaving model that says
        // "critical" shouldn't bypass the workbench's "user
        // classifies" guarantee. Force advisory-only ceiling.
        return "medium";
    }

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
