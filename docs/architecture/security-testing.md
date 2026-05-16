---
summary: 'Strategy + architecture for evolving Bowire into a multi-protocol, schema-aware cyber-security testing tool. Frames the capability tiers, the vulnerability-corpus format, the replay-as-attack primitive, and the differentiation lane against Burp Suite / OWASP ZAP / Nuclei.'
---

# Bowire as a cyber-security testing tool

**Status:** draft (v1.4+ candidate). The Tier-1 anchor — recording-as-attack-replay — is the first incremental piece. Higher tiers track to subsequent releases.

## Why this is a distinct lane

Bowire ships today as a "multi-protocol API workbench" — discover, invoke, record, replay across gRPC / REST / GraphQL / SignalR / MCP / SSE / WebSocket / MQTT / OData / Socket.IO. Every cyber-security testing tool of repute already overlaps with this surface for **one** transport:

- **Burp Suite Pro / ZAP** — HTTP-zentric. The gold standard for intercepting-proxy + repeater + intruder + scanner against REST/GraphQL. gRPC needs plugins + manual `.proto` wiring; SignalR / WebSocket / MQTT have at best community-contributed half-solutions.
- **Nuclei** — YAML-template-driven, fast, multi-transport. But the template DSL is HTTP-first and the ~10k-template community library is overwhelmingly HTTP/CVE-focused.
- **Postman / Insomnia** — REST + GraphQL functional testing, no security-specific surface.
- **42Crunch / Salt / Noname / Bright** — enterprise API-Security-Posture-Management SaaS. Schema-compliance-heavy, weaker on active testing, no self-host story.

Bowire's sweet-spot — and the case for evolving it into a cyber-security tool — is the intersection of three properties no incumbent owns:

1. **Multi-protocol-native, including the non-HTTP ones.** A single tool that can hit a gRPC service via reflection, a SignalR hub via its typed methods, an OData endpoint via `$metadata` introspection, and an MQTT broker via topic enumeration — all in the protocol's own dialect, not as a polyfill over HTTP.
2. **Schema-aware.** The frame-semantics framework (v1.3.0) already classifies fields by kind (`coordinate.latitude`, `image.bytes`, `audio.bytes`, `timestamp`, …). A schema-aware fuzzer knows not to throw SQL injection payloads at a latitude field, knows that `image.bytes` deserves magic-byte mutation instead of XSS payloads, knows that an `audio.sample-rate` field is a numeric integer and not a string. None of the incumbents have this for non-HTTP protocols.
3. **Recording → Replay as a first-class workflow.** Already shipped. Recordings carry interpretations + schema-snapshots (Phase 5) so replay is deterministic across detector drift. This is the natural primitive for "vulnerability corpus" — each known vulnerability becomes a captured-flow + an expected-vulnerable-response predicate.

The product positioning: **"Burp Suite for the non-HTTP protocols, with schema-awareness, self-hosted, with AI-assisted threat modeling via MCP."**

## Capability tiers

Roadmap unfolds in four tiers. Each tier is sellable on its own; the upper tiers compound on the lower ones.

### Tier 1 — Foundation

The minimum that makes Bowire a credible security tool at all.

- **Active Scanner subcommand** (`bowire scan --target X`) with built-in passive checks:
    - HTTP security-headers (CSP, HSTS, X-Frame-Options, Permissions-Policy, Cross-Origin-Resource-Policy, X-Content-Type-Options).
    - TLS version + cipher enumeration against the target.
    - Verbose-error-detection: probe with malformed input, look for stack-trace / internal-path / framework-version leakage in 500-class responses.
    - Banner/version disclosure (Server header, X-Powered-By, gRPC reflection trace).
- **Vulnerability-template format.** YAML-or-JSON DSL, multi-protocol-first, schema-aware. Same DSL the active scanner consumes for its own built-in checks; community contributions land in a separate `kuestenlogik/bowire-vulndb` repo.
- **Reporting.** Console table for interactive runs. SARIF JSON output for CI/CD integration (GitHub Code Scanning, GitLab Security Dashboard, Azure DevOps consume SARIF directly). HTML report for human-readable artifact upload.
- **Authentication profile.** Pre-scan login flow that captures a token, carries it through every subsequent request. Without this, scans of authenticated APIs are useless.
- **Scope-awareness.** Explicit in-scope / out-of-scope URL list. The scanner refuses to probe URLs outside scope so it can't accidentally hit a third party (e.g. a CDN, a partner API).

### Tier 2 — Specialty (where Bowire wins outright)

The differentiators that no other tool has.

- **Schema-aware fuzzing.** Right-click on a JSON-tree field in the workbench → "Fuzz this field with [SQLi / XSS / pathTrav / cmdInj / JNDI / SSTI / nullbyte]". The frame-semantics layer skips fields whose kind doesn't match the payload class (don't fuzz `coordinate.latitude` with `'; DROP TABLE`). `image.bytes` fields get magic-byte mutation; `timestamp` fields get out-of-range probes.
- **JWT toolkit.** Decode any `Authorization: Bearer` token in a recording; tamper with header / payload / signature; replay the tampered call. Built-in checks for the well-known JWT attacks (`alg:none`, key-confusion, `kid` injection, claims-confusion).
- **Multi-protocol attack templates** — the killer-feature for the lane:
    - **gRPC**: Server Reflection in production, unauthenticated server-streaming DoS amplification, oversized-message handling, status-code-leak in `grpc-message` trailers.
    - **GraphQL**: Introspection-in-production, deep-nesting DoS, alias amplification, batched-query unauth, field-suggestion error-message leak.
    - **SignalR**: Hub-method brute-force, group-membership-bypass, hub-method-DoS via reconnect storm.
    - **OData**: `$expand`-driven IDOR, `$filter` injection, navigation-property leak.
    - **WebSocket**: Subprotocol confusion, message smuggling, missing origin check.
    - **MQTT**: Topic enumeration, ACL bypass, retained-message disclosure on `$SYS/#`.
- **CVE-corpus repo** (`kuestenlogik/bowire-vulndb`, MIT-licensed). Community-contributable templates, CI-validated against per-CVE vulnerable-by-design containers. `bowire vulndb update` pulls the latest set.

### Tier 3 — Pro-grade catch-up (parity with Burp / ZAP)

The features that turn Bowire into a "single tool" choice for a pen-tester instead of "use Bowire AND Burp".

- **Intercepting proxy.** Inline modify of in-flight requests. Largest UX investment of the four tiers — a full request-rewrite UI, breakpoints, conditional-pass-through. Same model Burp's Repeater + Intruder uses.
- **Active fuzzer with curated payload library.** Wordlists per category (FuzzDB, SecLists), per-field invocation, statistical timing analysis (blind-SQL-injection oracle).
- **Sensitive-data scanner.** Response-body regex sweep for PII (credit cards, SSN, EU phone formats), API keys (AWS / GCP / Stripe / GitHub formats), JWT / OAuth tokens, internal IP addresses, S3 / Azure / GCS bucket URLs.
- **CI/CD integration.** First-class GitHub Action (`kuestenlogik/bowire-scan-action`), GitLab template, exit-code-on-finding for fail-on-vuln pipelines.
- **BOLA / BFLA tests.** Two auth tokens supplied — scanner attempts each tokenized request as the other user, flags 200-when-it-should-401 as Broken-Object-Level-Authorization.

### Tier 4 — Differentiation

Features that distinguish Bowire from "just another DAST tool".

- **AI-assisted threat modeling via MCP.** Bowire's MCP-server surface (already shipped) lets an LLM call into discovery / replay. New direction: Bowire's MCP adapter exposes a `bowire.threat-model` prompt that asks Claude (or any MCP-aware LLM) to look at the discovered service tree and propose the top-N riskiest endpoints + suggested attack templates. Operator confirms → templates run. The LLM is the smart-default-template-picker; Bowire is the execution engine.
- **Recording-driven security regression tests.** Known-good attack as a recording, runs in CI after every deploy. Failure = "this vulnerability got fixed but is back". Reuses the Phase-5 replay-determinism machinery so the regression test stays valid across detector drift.
- **Multi-protocol attack chains.** Sequence: record a gRPC discovery → mutate to extract the schema → replay schema fields as SQL-injection payloads against the same service's REST surface → assert no leak. The chain itself is a recording — versionable, reviewable, replayable.
- **Compliance mapping.** Automatic OWASP API Top 10 / CWE / CVSS scoring per finding. The HTML report has a compliance-overview tab; the SARIF output carries the mapping in `properties.security-severity` so GitHub Code Scanning groups findings the same way.

## Vulnerability template format (Tier-1 anchor)

A vulnerability template is a YAML or JSON document with three sections: identifying metadata, the request that probes for the vulnerability, and the predicate that determines whether the response indicates a vulnerable target.

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
- `latencyMsAtLeast: <int>` — response latency ≥ N ms (blind-SQL-injection / timing-oracle detection)

Composite operators:

- `allOf: [<predicate>, …]` — all sub-predicates match
- `anyOf: [<predicate>, …]` — at least one sub-predicate matches
- `not: <predicate>` — sub-predicate does NOT match

### Why YAML + JSON both

YAML is human-friendly (multi-line strings, comments, indented hierarchy) — community-contributable templates ship as YAML. JSON is machine-friendly — the workbench's internal IR, the SARIF output, and the wire format between `bowire scan` instances ship as JSON. The CLI accepts both via file extension.

## Pflegemodell — three concentric corpora

1. **Core community corpus** — `kuestenlogik/bowire-vulndb` repo on GitHub, MIT-licensed. PR-driven contributions, CI validates each template against a per-template vulnerable-by-design container (a Bowire sample app intentionally configured wrong). Two CI passes per PR: positive (template fires against vulnerable container) and negative (template stays quiet against patched container). Auto-sync from NVD: monthly job opens issues for new gRPC / GraphQL / etc. CVEs that lack a template.
2. **Curated set** — the Top-20 per protocol, reviewed by named maintainers, surfaced on the marketing site as a Trust-Signal page. Subset of the community corpus.
3. **Private add-ons** — a future commercial differentiator. Proprietary 0-day templates a Bowire customer subscribes to; ships as encrypted bundles unlocked by license-key, separate from the public corpus.

`bowire vulndb update` is the CLI plumbing — `git pull` against the public corpus, `~/.bowire/vulndb-local/` for handwritten templates, license-gate against the private bundle when a subscription is active.

## Replay-as-attack — the Tier-1 implementation

The first concrete piece. Three additive changes:

1. **Recording-format extension.** `BowireRecording` gains optional fields:
    - `attack: true/false` (default `false` — preserves backwards compat with every existing recording)
    - `vulnerability: { id, cwe, owaspApi, severity, cvss, protocols, references, remediation }`
    - `vulnerableWhen: <predicate-tree>`
2. **Predicate engine** in `Kuestenlogik.Bowire.Security.AttackPredicate`. Walks a recorded response (status + headers + body) against the predicate-tree, returns a boolean. Reusable from the scanner subcommand AND from the workbench's right-click "test as attack" UI later (Tier 2).
3. **`bowire scan` subcommand.** Takes a target URL + a corpus directory of template files. For each template: replay the probe against the target via the existing protocol-plugin's invoke path, evaluate `vulnerableWhen` against the response, emit a finding. Output: console table by default; `--out findings.sarif` for CI artifact upload; `--severity high` filter; `--format json` for structured pipe-to-jq.

The replay engine is the same one the mock server uses to replay recorded fixtures, just with the direction flipped — we're sending the recording's request to the target (instead of replaying its response back to a client).

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

The free tier stays "Bowire Core + `bowire scan` subcommand + public vulndb". Paid tier adds Tier-3 features (intercepting proxy, private vulndb subscription, AI threat-modeling via hosted LLM). This keeps the OSS-friendly story while leaving room for a commercial product.

Pick a name later; the architecture in this ADR doesn't depend on it.

## Roadmap order

1. **Recording-as-attack-replay** (Tier 1 anchor) — recording-format extension, predicate engine, `bowire scan` subcommand, three example templates, SARIF output. Land first because it's mostly mechanical extension of existing infrastructure.
2. **`bowire scan` built-in passive checks** — headers, TLS, verbose-errors, banners. Same scanner subcommand, additional built-in modules.
3. **`kuestenlogik/bowire-vulndb` repo** — schema, 20+ Top-OWASP-API templates, CI-validation pipeline, `bowire vulndb update` plumbing.
4. **Schema-aware fuzzing UI** (Tier 2 anchor) — right-click on JSON-tree field → fuzz with category.
5. **JWT toolkit** — Tier 2.
6. **Multi-protocol attack-template library** — gRPC reflection, GraphQL introspection, OData IDOR templates as community contribs to vulndb.
7. **Intercepting proxy** (Tier 3 anchor) — separate Phase, largest UX surface.
8. **MCP-driven threat modeling** (Tier 4) — LLM-suggested templates against the discovered service tree.

Each item is a single PR-set of bounded scope; the order minimises rework (the predicate engine from #1 underpins #2-#6, the vulndb repo from #3 holds the templates from #6).

## What this is NOT

To prevent scope creep:

- **NOT a network scanner.** Bowire doesn't probe IP ranges, doesn't do port discovery, doesn't run nmap. Nmap exists. Bowire works once a target service is known.
- **NOT a host-based scanner.** Bowire doesn't introspect the OS, doesn't read /etc/passwd, doesn't audit running processes. That's nessus-territory.
- **NOT a SAST tool.** Bowire doesn't analyse source code. SonarQube, Snyk Code, GitHub CodeQL exist.
- **NOT a runtime defense product.** Bowire is a testing tool, not an inline WAF / IDS / IPS. Bowire's findings inform what should be defended against; the defense itself is somebody else's product.

Bowire stays a multi-protocol API exercise tool. The security lane extends what kind of "exercise" — from "does this API still work" to "where does this API still leak".
