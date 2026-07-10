---
summary: 'Strategy + architecture for evolving Bowire into a multi-protocol, schema-aware cyber-security testing tool. Frames the capability tiers, the vulnerability-template format, the replay-as-attack primitive, and the differentiation lane against Burp Suite / OWASP ZAP / Nuclei.'
---

# Bowire as a cyber-security testing tool

**Status:** Tiers 1 + 2 shipped through v1.4.x. Tier 3's intercepting-proxy anchor shipped; remaining Tier 3 items + Tier 4 are open work tracked on the [ROADMAP](../../ROADMAP.md).

## Why this is a distinct lane

Bowire ships today as a "multi-protocol API workbench" — discover, invoke, record, replay across gRPC / REST / GraphQL / SignalR / MCP / SSE / WebSocket / MQTT / OData / Socket.IO. Every cyber-security testing tool of repute already overlaps with this surface for **one** transport:

- **Burp Suite Pro / ZAP** — HTTP-zentric. The gold standard for intercepting-proxy + repeater + intruder + scanner against REST/GraphQL. gRPC needs plugins + manual `.proto` wiring; SignalR / WebSocket / MQTT have at best community-contributed half-solutions.
- **Nuclei** — YAML-template-driven, fast, multi-transport. But the template DSL is HTTP-first and the ~10k-template community library is overwhelmingly HTTP/CVE-focused.
- **Postman / Insomnia** — REST + GraphQL functional testing, no security-specific surface.
- **42Crunch / Salt / Noname / Bright** — enterprise API-Security-Posture-Management SaaS. Schema-compliance-heavy, weaker on active testing, no self-host story.

Bowire's sweet-spot — and the case for evolving it into a cyber-security tool — is the intersection of three properties no incumbent owns:

1. **Multi-protocol-native, including the non-HTTP ones.** A single tool that can hit a gRPC service via reflection, a SignalR hub via its typed methods, an OData endpoint via `$metadata` introspection, and an MQTT broker via topic enumeration — all in the protocol's own dialect, not as a polyfill over HTTP.
2. **Schema-aware.** The frame-semantics framework (v1.3.0) already classifies fields by kind (`coordinate.latitude`, `image.bytes`, `audio.bytes`, `timestamp`, …). A schema-aware fuzzer knows not to throw SQL injection payloads at a latitude field, knows that `image.bytes` deserves magic-byte mutation instead of XSS payloads, knows that an `audio.sample-rate` field is a numeric integer and not a string. None of the incumbents have this for non-HTTP protocols.
3. **Recording → Replay as a first-class workflow.** Already shipped. Recordings carry interpretations + schema-snapshots (Phase 5) so replay is deterministic across detector drift. This is the natural primitive for "vulnerability templates" — each known vulnerability becomes a captured-flow + an expected-vulnerable-response predicate.

The product positioning: **"Burp Suite for the non-HTTP protocols, with schema-awareness, self-hosted, with AI-assisted threat modeling via MCP."**

## Capability tiers

Roadmap unfolds in four tiers. Each tier is sellable on its own; the upper tiers compound on the lower ones.

### Tier 1 — Foundation — shipped

The minimum that makes Bowire a credible security tool at all. Covered by `bowire scan` + the seed templates in `kuestenlogik/Bowire.VulnDb`. Live items: active scanner subcommand with built-in passive checks (HTTP security-headers, TLS version + cipher enumeration, verbose-error detection, banner/version disclosure); CVE lookup that matches the `Server` / `X-Powered-By` banner against a VulnDb corpus (`--cve-db`, OWASP A06); vulnerability-template format (YAML + JSON DSL, multi-protocol-first, schema-aware); SARIF + HTML + console reporting for CI/CD integration, plus `bowire scan report --in <sarif> [--baseline <prev>]` (#107) — a deterministic markdown report grouping findings by severity + OWASP with a diff-vs-baseline (new / fixed / still-open), and `POST /api/ai/security-report` which layers an AI executive summary on top (degrading to the deterministic report when no model is connected); authentication profile (`--auth-header`); scope-awareness (`--scope`); vulnerable-by-design sample app for regression testing.

#### Headless auth flows (`--auth-flow`) — #190

Real APIs sit behind a multi-step login (`/login` → cookie → `/token` → JWT), and the token expires. Rather than paste a fresh bearer into `--auth-header` before every run, `bowire scan --auth-flow flow.json` runs a recorded login → token chain **once** before the scan, extracts the token, and injects it into every probe. Because the flow runs at the start of each scan, an expired token is simply re-fetched.

A flow is an ordered list of HTTP steps; each step may capture values from its response (JSON path, regex, response header, or `Set-Cookie`) into named variables reusable in later steps as `{{var}}`. **Secrets are never inlined** — request fields reference `{{env.NAME}}`, read from the process environment, so a checked-in flow file carries `{{env.CLIENT_SECRET}}`, not the secret.

```jsonc
// oauth-client-credentials.flow.json
{
  "grant": "client_credentials",
  "steps": [
    {
      "url": "https://idp.example.com/oauth/token",
      "form": {
        "grant_type": "client_credentials",
        "client_id": "scanner",
        "client_secret": "{{env.SCANNER_CLIENT_SECRET}}"
      },
      "capture": [ { "var": "access_token", "json": "access_token" } ]
    }
  ]
}
```

```bash
SCANNER_CLIENT_SECRET=… bowire scan --target https://api.example.com --auth-flow oauth-client-credentials.flow.json --suite owasp-api
```

The token variable defaults to `access_token` / `id_token` / `token` / `jwt` (override with the flow's `token` field); the injected header defaults to `Authorization: Bearer <token>` (override with `injectHeader` / `injectPrefix`). This covers the scriptable grants — client-credentials, resource-owner-password, and any login → token chain. Browser-interactive grants (OAuth authorization-code / device-code, OIDC discovery + JWKS validation), the workbench capture UI, and a `{{auth.token}}` variable source are tracked as #190 follow-ups.

#### Active (mutating) probe mode (`--active`) — #395–#400

The default scan is **black-box and side-effect-free**: it connects, reads, and observes but never publishes, never holds a connection open to soak a server, never opens hundreds of streams. A second tier of checks (#395–#400) genuinely needs to *mutate* the target or hold resources over time — retained-message poisoning, wildcard-subscribe delivery, WebSocket compression-bomb / slow-loris, SSE slow-consumption, gRPC concurrent-stream fork-bomb. Those run **only** when the operator opts in with `--active`, and never otherwise.

`--active` prints a mutating-mode banner, then runs the registered `IActiveProtocolProbe`s alongside the passive scan. Each active probe namespaces + cleans up any side effect it leaves and stays within the operator-set budgets: `--active-duration <sec>` (time-based probes), `--active-concurrency <N>` (fan-out probes), `--active-expected-topic <t>` (the MQTT wildcard-subscribe scope).

**Shipped:**
- MQTT **retained-message poisoning** (#395) — publishes a retained message to a unique `bowire/probe/<nonce>` topic, opens a fresh subscription to check whether the broker persisted + re-delivered it (the poisoning vector), then clears the retained state by publishing an empty retained payload. Rolls up to API8:2023 (Security Misconfiguration).
- MQTT **will-message abuse** (#395) — connects with a malicious Last-Will-and-Testament on a `bowire/probe/will-<nonce>` topic, drops the session ungracefully to fire the will, and checks whether the broker delivers it unfiltered to a subscriber (a connect-time message-injection vector). Rolls up to API8:2023. Setting a will + dropping ungracefully are capabilities the shared MQTT plugin deliberately doesn't expose, so this probe is **rail-isolated**: it brings its own MQTT client rather than complicating the plugin's connect/discovery path (the workbench keeps working without the security rail).
- MQTT **wildcard-subscribe privilege** (#396) — with an authenticated client, subscribes to `#` and observes what the broker actually *delivers* over a bounded window (retained messages included), then flags any delivered topic outside the operator-supplied `--active-expected-topic` scope as over-broad access (topic-level authorization failure). Verdict is delivery-based, not SUBACK-based. Rolls up to API1:2023 (BOLA).
- gRPC **concurrent-stream fork-bomb** (#399) — discovers a server-streaming method by reflection and opens `--active-concurrency N` concurrent streams. A stream rejected with `RESOURCE_EXHAUSTED` before N ⇒ the server rate-limits (Safe, reported at the count it kicked in); all N accepted ⇒ "no concurrent-stream limit observed at N" (the finding names N honestly, since HTTP/2 permits 100+ streams by default). Rolls up to API4:2023 (CWE-770).
- **Timing-based DoS** (#398), both budgeted by `--active-duration`: WebSocket **slow-loris** holds an idle socket open and flags the absence of a server-side idle/read timeout within the window; SSE **slow-consumption** reads the event stream very slowly and flags a server that keeps feeding a deliberately-slow reader with no drop / backpressure. Both are honest about the window ("no timeout / drop observed within Ns") and roll up to API4:2023 (CWE-400).
- WebSocket **compression-bomb** (#397) — negotiates `permessage-deflate` and sends a bounded, highly-compressible frame that amplifies on decompression; a server that closes the connection (e.g. 1009 Message Too Big) caps decompressed size (Safe), one that keeps the connection open decompressed the amplified frame with no size/ratio limit (Vulnerable). Rolls up to API4:2023 (CWE-409). Also **rail-isolated**: it brings its own `ClientWebSocket` (with `DangerousDeflateOptions`) rather than teaching the shared WebSocket channel about permessage-deflate.

The `--active` cluster (#395–#399) is complete. The MCP tool-call-injection concern (#400) ships as a **passive** static-inventory check (below) rather than an active probe — the adversarial-payload variant is an AI-semantics problem routed to the AI security-scan orchestration (#104/#106).

```bash
bowire scan --target mqtt://broker.example.com:1883 --suite protocol --auth-header "…" --active
```

### Tier 2 — Specialty — shipped

The differentiators that no other tool has. Live items: schema-aware fuzzing both as workbench right-click UI ("Fuzz this field ▸") and CLI (`bowire fuzz` with SQLi / XSS / path-traversal / command-injection categories, schema-aware skip for numeric/bool fields, baseline-diff oracle detection); JWT toolkit (`bowire jwt decode` + `bowire jwt tamper` with `--alg-none`, `--set claim=value`, `--secret <key>`, plus `bowire jwt analyze` — deterministic security flags: alg=none, symmetric-HMAC crackability, missing/expired/long-lived `exp`, missing `nbf`, scope creep, audience binding, `kid` surface — and an AI narrative over those flags via `POST /api/ai/jwt-analyze`, #105, which degrades to the deterministic analysis when no model is connected); multi-protocol attack template library bootstrap in [`kuestenlogik/Bowire.VulnDb`](https://github.com/Kuestenlogik/Bowire.VulnDb) (initial 7 templates across gRPC / GraphQL / REST / OData, CI validation against the vulnerable-by-design sample).

**Open:** Multi-protocol attack template **library expansion** — see ROADMAP for the next batch (SignalR brute-force, OData IDOR, MQTT ACL bypass, WebSocket subprotocol confusion, …) plus the monthly NVD-sync workflow + `bowire vulndb update` plumbing.

### Tier 3 — Pro-grade catch-up (parity with Burp / ZAP)

The features that turn Bowire into a "single tool" choice for a pen-tester instead of "use Bowire AND Burp".

**Shipped:**
- **Intercepting proxy** (`bowire proxy`) — Kestrel-hosted forward proxy with capture store + SSE live feed, HTTPS MITM via auto-generated CA + on-the-fly leaf-cert minting, workbench Proxy view that streams captured flows and converts them into recordings.
- **Active fuzzer** — CLI `bowire fuzz` ships with built-in wordlists per category (sqli / xss / pathtrav / cmdinj). Workbench right-click UI exposes the same engine.
- **CI/CD integration** — reusable `scan-template.yml` workflow, first-class GitHub Action exit-code semantics (findings are output, not failure), SARIF + GitHub Code Scanning compatibility chain (severity-label → CVSS midpoint mapping, `logicalLocations` for non-https URIs, `physicalLocation` pointing at the scan workflow, `partialFingerprints` for cross-run alert tracking).

**Open:**
- [ ] **Curated payload library at scale** — beyond the built-in seed wordlists, pull from FuzzDB / SecLists, statistical timing analysis (blind-SQL-injection oracle).
- [ ] **Sensitive-data scanner.** Response-body regex sweep for PII (credit cards, SSN, EU phone formats), API keys (AWS / GCP / Stripe / GitHub formats), JWT / OAuth tokens, internal IP addresses, S3 / Azure / GCS bucket URLs.
- [ ] **BOLA / BFLA tests.** Two auth tokens supplied — scanner attempts each tokenised request as the other user, flags 200-when-it-should-401 as Broken-Object-Level-Authorization.

### Tier 4 — Differentiation

Features that distinguish Bowire from "just another DAST tool". Most items open; the OWASP API Top 10 suite (compliance mapping, below) shipped in v2.3.

- [ ] **AI-assisted threat modeling via MCP.** Bowire's MCP-server surface (already shipped) lets an LLM call into discovery / replay. New direction: Bowire's MCP adapter exposes a `bowire.threat-model` prompt that asks Claude (or any MCP-aware LLM) to look at the discovered service tree and propose the top-N riskiest endpoints + suggested attack templates. Operator confirms → templates run. The LLM is the smart-default-template-picker; Bowire is the execution engine.
- [ ] **Recording-driven security regression tests.** Known-good attack as a recording, runs in CI after every deploy. Failure = "this vulnerability got fixed but is back". Reuses the Phase-5 replay-determinism machinery so the regression test stays valid across detector drift.
- [ ] **Multi-protocol attack chains.** Sequence: record a gRPC discovery → mutate to extract the schema → replay schema fields as SQL-injection payloads against the same service's REST surface → assert no leak. The chain itself is a recording — versionable, reviewable, replayable.
- [~] **Compliance mapping.** The **OWASP API Security Top 10 (2023) suite** shipped in v2.3 (#173): dedicated per-entry probes behind the `IOwaspApiProbe` seam, exposed as `bowire scan --suite=owasp-api` (per-entry coverage table), the `/api/security/owasp-catalog` + `/api/security/owasp-scan` workbench endpoints, and a Security-rail view that paints each entry covered / clean / vulnerable. Every finding carries its `owaspApi` tag + CVSS in `properties.security-severity`, so GitHub Code Scanning groups findings the same way. All ten entries have a dedicated probe (#381) — API6 (business-flow friction) and API10 (unsafe upstream consumption) have no clean generic black-box check, so their probes stay conservative: a real verdict only on a strong signal, otherwise a review-only skip rather than a false pass. Beyond HTTP, **protocol-specific probes** (behind the `IOwaspProtocolProbe` seam) drive a protocol plugin's own invoke path, reaching vulnerability classes that only exist below HTTP: GraphQL introspection, gRPC server reflection, and MCP tool/resource listing (→ API9), plus gRPC transport-auth, WebSocket, MQTT, and SSE anonymous-connect checks (→ API2), and an MCP destructive-tool inventory check that flags mutating tools reachable without a function-level authorization gate (→ API5, #400 — the deterministic slice of tool-call injection). The Security rail's OWASP view has a **Compliance** tab beside Coverage: a per-scan OWASP/CWE/CVSS overview (posture strip, severity histogram, peak CVSS, per-CWE breakdown). Deeper per-protocol scanner checks beyond this OWASP auth/inventory mapping are tracked in #184. See the [OWASP API Top 10 suite reference](../security/owasp-api/index.md).

## Vulnerability template format

The shape every template in [`kuestenlogik/Bowire.VulnDb`](https://github.com/Kuestenlogik/Bowire.VulnDb) follows. YAML or JSON, three sections: identifying metadata, the probe request, the predicate that decides "is this target vulnerable".

```yaml
id: BWR-GRPC-001
title: gRPC Server Reflection exposes internal services in production
cve: []                       # NVD entry, optional
cwe: CWE-540                  # Inclusion of Sensitive Information
owaspApi: API3-2023-BOPLA
severity: high                # low / medium / high / critical
cvss: 7.5                     # CVSS 3.1 base score, optional
protocols: [grpc, grpc-web]
authors: [thomas-stegemann]
introduced: 2026-05-15
references:
  - https://github.com/grpc/grpc/blob/master/doc/server-reflection.md

# What to send. Mirrors the existing BowireRecordingStep shape so the
# same replay engine that powers `bowire mock` also drives `bowire scan`.
probe:
  protocol: grpc
  service: grpc.reflection.v1alpha.ServerReflection
  method: ServerReflectionInfo
  body: |
    { "listServices": "" }

# When this matches, the target is vulnerable. Composite operators
# (allOf / anyOf / not) nest arbitrarily.
vulnerableWhen:
  allOf:
    - status: 200
    - bodyJsonPathExists: "$.listServicesResponse.service[*]"
    - bodyJsonPath: "$.listServicesResponse.service[*].name"
      anyValueMatches: ".*Admin.*|.*Internal.*"

remediation: |
  In ASP.NET Core, drop `app.MapGrpcReflectionService()` from the
  production build, OR gate it behind `.RequireAuthorization("Admin")`.
  Verify with: `grpcurl -plaintext <host> list` — should return
  `Failed to list services: ...`.
```

### Predicate operators

Leaf operators:

- `status: <int>` — HTTP status code equals
- `statusIn: [int, …]` — HTTP status code is in the list
- `bodyContains: "<substr>"` — response body contains literal substring
- `bodyMatches: "<regex>"` — response body matches regex (RE2 syntax)
- `bodyJsonPath: "<path>"` + `equals: "<value>"` — JSONPath result equals
- `bodyJsonPathExists: "<path>"` — JSONPath has at least one match
- `bodyJsonPath: "<path>"` + `anyValueMatches: "<regex>"` — at least one JSONPath result matches the regex
- `headerEquals: { Name: "<value>" }` — header value equals
- `headerExists: ["Name", …]` — header is present
- `headerMissing: ["Name", …]` — header is absent
- `latencyMsAtLeast: <int>` — response latency ≥ N ms (blind-SQL-injection / timing-oracle detection)

Composite operators:

- `allOf: [<predicate>, …]` — all sub-predicates match (implicit when multiple leaves sit on one node)
- `anyOf: [<predicate>, …]` — at least one sub-predicate matches
- `not: <predicate>` — sub-predicate does NOT match

### Why YAML + JSON both

YAML is human-friendly (multi-line strings, comments, indented hierarchy) — community-contributable templates ship as YAML. JSON is machine-friendly — the workbench's internal IR, the SARIF output, and the wire format between `bowire scan` instances ship as JSON. The CLI accepts both via file extension.

## Pflegemodell — three concentric corpora

1. **Core community template set** — [`kuestenlogik/Bowire.VulnDb`](https://github.com/Kuestenlogik/Bowire.VulnDb) on GitHub, MIT-licensed. PR-driven contributions, CI validates each template against a per-template vulnerable-by-design container. Two CI passes per PR: positive (template fires against vulnerable container) and negative (template stays quiet against patched container).
2. **Curated set** — the Top-20 per protocol, reviewed by named maintainers, surfaced on the marketing site as a Trust-Signal page. Subset of the community template set.
3. **Private add-ons** — a future commercial differentiator. Proprietary 0-day templates a Bowire customer subscribes to; ships as encrypted bundles unlocked by license-key, separate from the public template set.

`bowire vulndb update` (planned) is the CLI plumbing — `git pull` against the public template set, `~/.bowire/vulndb-local/` for handwritten templates, license-gate against the private bundle when a subscription is active.

## Differentiation vs incumbents

| Tool | Wins on | Bowire's lane |
|---|---|---|
| **Burp Suite Pro** | HTTP intercepting proxy, scanner, extensibility (BApps), $499/user/year. | Bowire owns gRPC / SignalR / WebSocket / MQTT / OData natively. Bowire is free + self-hosted. |
| **OWASP ZAP** | Free, scriptable, similar HTTP feature set to Burp. | Same non-HTTP-protocol gap. |
| **Nuclei** | Massive template library (~10k), fast. | Bowire's templates are schema-aware (frame-semantics) and multi-protocol-first. |
| **Postman** | Auth flows, environments, collaboration. | No security testing surface. Bowire's workflow primitives (env + recording + replay) already match Postman's; the security layer is the addition. |
| **42Crunch / Salt / Bright** | SaaS API Security Posture Management. | Bowire is self-hostable, OSS, with on-prem replay determinism. Enterprise lane separate from Bowire's open core. |

The positioning is clear: **Bowire is not trying to be Burp + 1**. It's the multi-protocol-native + schema-aware security tool the existing incumbents leave a gap for.

## Branding consideration

For commercial positioning beyond the open core, a sub-brand may be warranted:

- **Bowire Scout** — recon-fokussiert, fits the existing maritime brand vocabulary.
- **Bowire Probe** — active-scanning-fokussiert.
- **Surgewave Sentinel** — full sub-product in the Surgewave / Capespire / Sealbolt brand family the org already uses.

The free tier stays "Bowire Core + `bowire scan` subcommand + public vulndb". Paid tier adds Tier-3+ features (curated payload library at scale, private vulndb subscription, AI threat-modeling via hosted LLM). This keeps the OSS-friendly story while leaving room for a commercial product.

Pick a name later; the architecture in this ADR doesn't depend on it.

## What this is NOT

To prevent scope creep:

- **NOT a network scanner.** Bowire doesn't probe IP ranges, doesn't do port discovery, doesn't run nmap. Nmap exists. Bowire works once a target service is known.
- **NOT a host-based scanner.** Bowire doesn't introspect the OS, doesn't read /etc/passwd, doesn't audit running processes. That's nessus-territory.
- **NOT a SAST tool.** Bowire doesn't analyse source code. SonarQube, Snyk Code, GitHub CodeQL exist.
- **NOT a runtime defense product.** Bowire is a testing tool, not an inline WAF / IDS / IPS. Bowire's findings inform what should be defended against; the defense itself is somebody else's product.

Bowire stays a multi-protocol API exercise tool. The security lane extends what kind of "exercise" — from "does this API still work" to "where does this API still leak".
