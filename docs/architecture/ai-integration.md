---
summary: 'How Bowire offers AI-assisted features (explain method, generate body, diagnose failure, mock response,
semantic history search) without violating the local-first / no-cloud / no-account brand promise. Three model-access
modes (BYOK cloud, local Ollama/LM-Studio, MCP-client) plus a deterministic hint engine that works without any LLM.'
---

# Bowire AI integration

**Status:** concept (v1.5+ candidate). No implementation yet — this document fixes the design constraints before the
AI side-panel lands in the workbench UI. Adjacent piece: the [security-testing](security-testing.md) roadmap, which
lists MCP-based threat-modelling as a Tier-4 capability and shares the model-access path with this one.

## The tension this resolves

Bowire's positioning is uncompromisingly local-first: *"No cloud restriction. No account required. Free and open
source."* That line appears in the hero, the README, the package descriptions. Users install via `dotnet tool
install`, `winget`, `brew`, or Docker, point Bowire at their service, and never touch a Küstenlogik server.

2026-era developer tools (Cursor, Linear, Raycast, Notion AI) ship with an AI side-panel as a default-on feature.
Bowire's competitors — Postman AI, Bruno's planned AI features, Insomnia AI Runner — all assume a vendor-hosted model.
Following that pattern would either:

- **Force a Bowire-hosted cloud** (kills the "no cloud" promise),
- **Force a vendor-key-on-the-marketing-site** (kills the "no account" promise), or
- **Offer AI as a paid SKU** (kills the "free and open source" promise).

The constraint is therefore: **AI features must be a property of the user's environment, not of Bowire's
infrastructure.** Bowire ships the *UI surface* and the *prompts*; the model access is configured by the user, against
infrastructure the user already controls.

## Three model-access modes

All three modes plug into the same in-UI surface (an `ai-side-panel` slot, command-palette slash-commands,
inline-suggest in the JSON/form editors). The user configures one of them once in `Settings → AI`; switching is a
setting change, not a re-install.

### Mode 1 — Bring-your-own-key (BYOK), primary path

User enters an API key + provider in `Settings → AI`. Bowire's client-side JS calls the provider directly from the
browser, with the key kept in `localStorage` (or, when running embedded, in the host's `~/.bowire/secrets/` encrypted
store, same path as the existing environment-variable storage).

Providers to support out of the gate:

- **Anthropic** (`https://api.anthropic.com/v1/messages`) — primary recommendation; Claude is strong at
structured-output and schema-reasoning, both core to Bowire's use cases.
- **OpenAI** (`https://api.openai.com/v1/chat/completions`) — broadest user base.
- **OpenRouter** (`https://openrouter.ai/api/v1/chat/completions`) — single key, choice of model. Lowers the friction
of "pick a model".
- **Azure OpenAI** — for enterprise users whose company already has a deployment.

Bowire never proxies the request through Küstenlogik infrastructure. The key never leaves the user's machine (or the
user's ASP.NET host, in embedded mode). The Settings UI is explicit about this: *"Your API key stays on this machine.
Bowire calls the provider directly from your browser. We never see the key or your prompts."*

CORS will be the practical pain point for browser-direct calls. Anthropic, OpenAI, and OpenRouter all set permissive
CORS headers as of 2026. Where a provider does not (some Azure deployments), the embedded host can offer an opt-in
proxy endpoint at `/bowire/ai/proxy` that forwards to a single configured URL — still no Küstenlogik dependency.

### Mode 2 — Local models (Ollama / LM Studio), first-class

This is the mode that turns "local-first AI" from a slogan into a real story. Both Ollama (`http://localhost:11434`)
and LM Studio (`http://localhost:1234`) expose OpenAI-compatible HTTP APIs that Bowire can call without an internet
connection, without an API key, and without any data leaving the user's machine.

Auto-detect on startup: a 300 ms probe against the two default ports during the discovery phase. When found, the AI
panel shows a one-click "Connect to your Ollama instance — 3 models found (llama3:8b, qwen2.5:7b, codellama:13b)"
affordance. Picking a model writes the choice to Settings; nothing else required.

Auto-detect is opt-in via a checkbox in `Settings → AI → Local model auto-detect`, defaulting on. Privacy-paranoid
users can disable it and configure the endpoint manually.

Why this mode matters for the brand:

1. The marketing claim "AI-assisted, no cloud" is *actually true*. Other tools say "local-first" and then route
through OpenAI; Bowire with a local 7B model does it for real.
2. It matches Bowire's existing audience. Engineers running Bowire in air-gapped environments, on-prem deployments, or
against internal services already have strong reasons not to send their schemas to OpenAI. Many of them already run
Ollama for the same reason.
3. The model-quality bar is lower than expected — the AI features in the matrix below (generate JSON from schema,
explain a method's intent, summarize an error message) are well within 7B-model territory.

### Mode 3 — MCP client reversal

Bowire already speaks MCP — but as a *server*: the standalone CLI's `--enable-mcp-adapter` exposes the workbench's
discovered services as MCP tools for Claude Desktop / Cursor / Copilot to call. Mode 3 inverts that: Bowire becomes an
**MCP client** that uses *the user's existing MCP host* as a model gateway.

Concretely: the user pastes a stdio command (`claude mcp serve`) or a URL (`http://localhost:3845/mcp`) into Settings.
Bowire's AI panel routes its prompts through that MCP host, which carries the model affinity, the auth, and the
rate-limiting the user has already set up for their other tools.

This is the most niche mode but it has a coherent audience: power users who already run an MCP stack for Claude
Desktop, Cursor, or a corporate MCP gateway. For them, Bowire becomes "yet another MCP client" — a familiar shape —
and inherits whichever model their MCP host is wired to. No extra key, no extra config.

## Feature matrix — which features work in which mode

| Feature | BYOK cloud | Local 7B (Ollama / LM Studio) | MCP-client | Fallback when no model is configured |
|---|---|---|---|---|
| **Explain this method** (schema → prose) | ✓ | ✓ | ✓ | Schema-render fallback (no narrative) |
| **Generate request body** from schema | ✓ | ✓ (good) | ✓ | Form-builder default values |
| **Why did this fail?** (response → diagnosis) | ✓ | ⚠ (basic) | ✓ | Manual inspection / docs link |
| **Mock plausible response** | ✓ | ✓ | ✓ | Schema-default mock with placeholders |
| **Semantic history search** ("show me yesterday's failing port-call requests") | ✓ | ✓ | ✓ | Full-text search across
recorded sessions |
| **Natural-language assertion** ("response should mention every active berth") → test predicate | ✓ | ⚠ | ✓ | Manual
JSONPath / regex assertion |
| **Suggest a follow-up call** based on response | ✓ | ⚠ | ✓ | Empty |
| **Vulnerability prompt generation** (see [security-testing](security-testing.md) Tier-4) | ✓ | ⚠ | ✓ | Static
template library |

Two takeaways from the matrix:

1. The features that map cleanly to *structured-output-from-structured-input* (generate body from schema, mock from
schema, semantic search) are 7B-model-tractable. These should be the **flagship features** because they work in the
most-restricted environment.
2. The features that require *reasoning over open-ended text* (diagnose failure, natural-language assertion) benefit
measurably from frontier models. These should be **available but not flagship** — degrade gracefully when running
against a local model.

## The hint engine — always on, no model required

A deterministic rule-based engine runs alongside (and underneath) the LLM-powered features. It scans the current
workbench state — selected method, last response, current schema, recent call history — and surfaces context-aware
hints in the same UI slot the AI panel uses.

Examples:

- **`💡 This gRPC method returned an empty response — check Server-Reflection is enabled on the host.`** Triggered by:
gRPC protocol + 0-byte response + reflection-not-configured signal.
- **`💡 You called this 3 times in the last minute with id=1 — save as a quick-action?`** Triggered by: same method +
same body + ≥3 invocations in a sliding 60 s window.
- **`💡 The response has 2 fields the schema doesn't declare — schema may be stale.`** Triggered by: response JSON
keys ⊄ declared schema fields.
- **`💡 This SignalR hub method takes a callback parameter — use the "Subscribe" tab to capture results.`** Triggered
by: hub method signature inspection.

These hints exist regardless of model configuration. They give the AI panel a permanent presence even before the user
has set up a model, which means:

1. The panel never reads as "sign up to unlock features" — the empty state is *useful*.
2. When the user *does* connect a model, the panel transitions naturally — same slot, richer suggestions.
3. The rule-based hints are also a teaching surface: every hint that fires for a non-AI user is a moment of "Bowire
just noticed something useful about my workflow" — which is the brand promise the AI panel is supposed to deliver.

The hint engine lives in the same module as the AI prompt-routing (`src/Kuestenlogik.Bowire/wwwroot/js/ai/`, planned).
Hints are evaluated client-side from the live UI state — no network, no model, deterministic.

## UI principles

The AI side-panel must never read like a paywall or a sign-up gate. Three rules:

1. **The panel is always present, never empty.** With no model configured, it shows hint-engine output and a discreet
"Connect a model →" link. The user sees value before they configure anything.
2. **Configuration is one screen, not a flow.** `Settings → AI` is a single page: pick provider, paste key (or pick
local endpoint), pick model from the dropdown, done. No multi-step wizard.
3. **The privacy stance is loud, not buried.** Every model-related setting shows where data goes: *"Prompts go to
https://api.anthropic.com. Nothing goes to Küstenlogik."* The local-model path shows: *"Prompts stay on this machine.
Nothing leaves."*

The command palette gets slash-commands for the same features (`/explain`, `/mock`, `/diagnose`, `/generate body`).
These route through the same model-access layer.

## Sequenced rollout

Each phase is shippable on its own. Higher phases compound on lower ones.

### Phase 1 — Hint engine

No LLM dependency. Ships the side-panel UI slot, the rule-engine, and the first ~15 deterministic hints. Validates the
UX of "AI panel that's always there and always useful" before adding model dependency. Doubles as a teaching surface
that lands immediately.

### Phase 2 — Local-model auto-detect + Ollama / LM Studio integration

Adds the Mode-2 path: detect Ollama / LM Studio on startup, offer one-click connect, route the AI features through
them. Flagship features (generate body, mock response, semantic search) land here. Marketing line: *"AI features with
zero accounts, zero cloud."*

**Shipped scope.** The seam ships as the optional `Kuestenlogik.Bowire.Ai` NuGet — same opt-in shape as the protocol
plugins. Adding it gives the host an `IChatClient` (Microsoft.Extensions.AI) backed by OllamaSharp, which speaks both
Ollama's wire shape on `127.0.0.1:11434` and LM Studio's drop-in equivalent on `127.0.0.1:1234`. The standalone
`bowire` CLI bundles the package so laptop users get the chat surface out of the box; embedded hosts opt in by adding
the package + registering their own `IChatClient` *before* `AddBowireAi()` to reuse whatever AI infrastructure they
already run (`TryAddSingleton` respects the host's choice).

Server surface: three endpoints under the workbench base path —
`GET /api/ai/probe-local` (300 ms detect against both local providers), `GET /api/ai/status` (is an `IChatClient`
registered? which provider / model?), `POST /api/ai/chat` (proxy a chat completion through `IChatClient`). Frontend:
`ai.js` Phase 2 — paints a status footer + chat composer when `hasClient=true`, and surfaces detected providers as a
"Detected ollama at … — llama3.2:3b" affordance when the package is installed but no provider is connected yet. When
the package isn't installed every call returns 404 and the panel falls back to the Phase-1 hint engine — the host
stays usable.

CLI knobs: `--ai-provider` / `--ai-endpoint` / `--ai-model` map to `Bowire:Ai:ProviderId` / `Endpoint` / `Model`.
Defaults: provider `ollama`, endpoint `http://localhost:11434`, model `llama3.2:3b`, auto-detect on. Outbound
network calls stay opt-in: the probe only touches loopback, and the chat path goes wherever the user pointed
`--ai-endpoint` — same opt-in discipline as the plugin update check and telemetry exporter.

**Threat model (#59).** New endpoint `POST /api/ai/threat-model` takes the discovered service surface (a flat list of
`{endpointId, path, verb, protocol, service, inputShape, authState}`) and returns a ranked top-N with `{risk: 0-10,
why, suggestedTemplates[]}` per row. Driven from the AI side-panel: a "Run threat model" button collects the
workbench's current `services` list and renders the ranked rows with a colour-coded risk score (green <5 / amber
5-7 / red ≥8), a one-sentence rationale, suggested Nuclei template families as chips, and a "Copy bowire scan
command" shortcut per row. Server-side input cap is 200 endpoints (truncated + flagged in the response) so a
5 k-service host doesn't blow the local model's context. Rows without `endpointId` are dropped server-side to
guard against sloppy model output. Same prompt-engineering discipline as triage: respond only with the JSON
envelope, score below 5 when the endpoint looks read-only / well-scoped, never invent CVEs. Markdown-fenced
output recovered by the parser; garbage falls back to an empty ranking.

**Findings triage (#61).** New endpoint `POST /api/ai/triage` takes a finding + matched evidence + protocol /
endpoint context and returns a JSON verdict `{realScore: 0-100, reasoning, fix}`. The fuzz panel
(`semantics-menu.js`) renders a `?` button next to every `Vulnerable` row; click expands the row into an
AI-generated verdict with a colour-coded confidence score (green <30 / amber 30-69 / red ≥70) and a suggested
fix. Verdicts are cached in-memory per `(target, payload)` so reopening doesn't re-bill the model. The same
endpoint will serve the future Nuclei findings panel + the AI Settings UI's "explain finding" affordance —
the request shape is finding-agnostic on purpose. Prompt is engineered to keep the model conservative: when
evidence is thin, score below 50 and say what would confirm it; never invent CVEs or product names. Local
models often wrap JSON in prose; the parser extracts the first `{...}` block and falls back to a
"couldn't parse" verdict at score 50 so the UI never blanks. Evidence is capped at 4 k characters in the
prompt so a 100 k-line response can't blow the local model's context.

**Settings UI (#63).** Provider / endpoint / model land on a new "AI" tab in the workbench Settings dialog so the
user no longer needs to restart with new CLI flags to switch model or pick an endpoint. The form reads
`GET /api/ai/status` for the current binding, `GET /api/ai/probe-local` for detected local models (dropdown
source), and writes via `POST /api/ai/config`. Persistence goes through `IBowireUserStore` (#28 Phase 2) so
the choice survives restart; a `BowireAiRuntime` singleton holds the live options + the active `IChatClient`,
and a `MutableChatClient` proxy delegates every call to the runtime's current client so saves apply
without restarting the host. Embedded hosts that registered their own `IChatClient` before `AddBowireAi()`
still win — the save path persists the user's pick to disk for the next start but doesn't swap the host's
client; the tab signals this clearly with a "host-managed" status badge so the user understands what's
happening. Overlay precedence: defaults → `IConfiguration` → `configure` callback → user-config file
(disk wins because it represents an explicit user choice).

Phase 3 (BYOK cloud) reuses the same `IChatClient` seam — register `Microsoft.Extensions.AI.OpenAI` (or the
equivalent) instead of OllamaSharp and the endpoints carry over unchanged. Phase 4 (MCP-client reversal) layers
Microsoft Agent Framework on top of the same `IChatClient`.

### Phase 3 — BYOK cloud providers

Adds Anthropic / OpenAI / OpenRouter / Azure OpenAI in the Settings panel. Same UI surface, different routing. The
features that benefit most from frontier models (diagnose, natural-language assertion) become genuinely strong here.
CORS quirks and the optional embedded-host proxy ship in this phase.

### Phase 4 — MCP-client reversal

Adds the third mode. Niche but coherent with Bowire's existing MCP-server story. Documentation explicitly markets this
as "Bowire integrates with the MCP host you already run" rather than "another way to plug in AI". Closes the loop
with the Tier-4 security-testing roadmap, which uses the same routing layer for threat-modelling prompts.

## Open questions

These are the design decisions still up for grabs:

- **Model affinity per feature?** Should "diagnose failure" always use the most capable model in the user's configured
set (e.g. Sonnet over a local 7B if both are available), or should the user pick one model per feature? Default
proposal: one model, simplest mental model — power users can configure multiple later.
- **Streaming or non-streaming?** Streaming responses feel more 2026-modern, but add complexity for the local-model
path (some Ollama models stream slower than the network latency saved). Default proposal: streaming on by default,
fall back to non-streaming if first-token latency exceeds 2 s.
- **Caching prompts?** Anthropic prompt-caching saves real money on repeated schema-explanation calls. Default
proposal: yes, cache the system prompt + schema-context portions on Anthropic; ignore for other providers initially.
- **History of AI interactions?** Should the AI panel log its own conversation history (per service / per method)?
Default proposal: yes, scoped to the current recording. Cleared when the user starts a new session.
- **Cost transparency?** For BYOK users, show token / cost estimates. Default proposal: yes, in a footer line of the
AI panel — *"Last call: 1.2 k input / 380 output tokens, ~$0.0042 (estimate)"*. Builds trust by being upfront.

These are tractable in the per-phase design discussions and don't block the document being adopted as the framing
concept.