---
title: OWASP API Security Top 10 suite
summary: 'Per-entry reference for `bowire scan --suite=owasp-api` — what each OWASP API Top 10 (2023) probe checks, the input it needs, its coverage status, and the remediation it points at.'
---

# OWASP API Security Top 10 (2023) suite

Bowire ships a structured test suite for the [OWASP API Security Top 10 (2023)](https://owasp.org/API-Security/editions/2023/en/0x11-t10/). Each entry has a dedicated probe behind the `IOwaspApiProbe` seam; the suite rolls every finding (templates + [built-in passive checks](../../features/scan.md#built-in-passive-checks) + probes) up against the ten entries and reports a per-entry status.

## Running it

CLI — prints a per-entry coverage table:

```bash
bowire scan --target https://api.example.com --suite=owasp-api \
            --auth-header "Authorization: Bearer $TOKEN" \
            --auth-header-b "Authorization: Bearer $TOKEN_B"
```

Workbench — the **Security rail** has a target box and a *Run OWASP suite* button that paints each row covered / clean / vulnerable, backed by `POST /api/security/owasp-scan` (which runs the HTTP probes **and** the protocol-specific probes). `GET /api/security/owasp-catalog` returns the ten entries as static metadata. The result has two tabs: **Coverage** (the per-entry table below) and **Compliance** — a per-scan overview that rolls the vulnerable findings up into an OWASP posture strip, a severity histogram, the peak CVSS, and a per-CWE breakdown (each CWE linking to its MITRE definition).

## Reading the table

| Marker | Meaning |
|---|---|
| `[VULN]` | A probe found a vulnerability for this entry. |
| `[ok]` | A probe ran and the target was clean. |
| `[----]` | **Not assessed** — no probe covers it, or the probe skipped because it lacked an input. A clean scan is never a false pass. |
| `[err]` | The probe errored (unreachable, etc.). |

## Entries

Every finding carries a severity, CVSS score, the `owaspApi` tag, and a remediation pointer (surfaced in SARIF `properties`).

### API1:2023 — Broken Object Level Authorization ✅
Cross-identity test: reads the object at `--target` as identity **A**, confirms anonymous access is blocked, then reads it as identity **B**. If B can read A's object while anonymous is denied, object-level authorization is missing (critical on an exact object match, otherwise high).
**Needs:** `--auth-header` (A) **and** `--auth-header-b` (B); an object-scoped `--target` (path ending in a numeric / UUID id).

### API2:2023 — Broken Authentication ✅
Establishes an authenticated 2xx baseline, then checks: authentication not enforced (2xx with no credential), JWT forgery (`alg:none` re-header + tampered signature), and token lifetime (expired token accepted / no `exp` claim). Forgery + expiry findings are gated on anonymous access being blocked, so a public API isn't misreported. A **protocol-specific variant** (see [Protocol-specific probes](#protocol-specific-probes)) extends the same entry to **gRPC transport auth** — invoking a method anonymously and reading the status trailer.
**Needs:** `--auth-header`.

### API3:2023 — Broken Object Property Level Authorization ✅
Mass assignment: PATCHes the object with a single harmless unknown **canary** property and checks whether the server persists it (confirmed by a follow-up GET). A server that stores arbitrary client-set fields is vulnerable to the whole class. Distinguishes *persisted* (high) from merely *echoed* (medium); nulls the canary afterwards.
**Needs:** a writable JSON-object `--target`; usually `--auth-header`.

### API4:2023 — Unrestricted Resource Consumption ✅
A modest rate-limit burst (no `429` / no `RateLimit-*` / `Retry-After` across N rapid requests) plus a 1 MB request-body check (a `2xx` instead of `413`).
**Needs:** nothing.

### API5:2023 — Broken Function Level Authorization ✅
Probes a curated set of privileged management-plane endpoints (Spring actuator `env`/`heapdump`/`configprops`, Elasticsearch `_cat`/`_cluster`, Go `pprof`, Prometheus `metrics`). A public `2xx` is a finding; `401`/`403` is healthy.
**Needs:** nothing (rides `--auth-header` when supplied — a `2xx` under a regular-user token is BFLA).

### API6:2023 — Unrestricted Access to Sensitive Business Flows ✅
No clean *generic* black-box check exists — whether a flow is *sensitive* is business context — so the probe assesses the property that makes a sensitive flow abusable once found: **the same state-changing POST replayed verbatim, accepted every time, with no anti-automation friction** (no CAPTCHA / challenge, no bot-mitigation layer like Cloudflare / DataDome / PerimeterX / Imperva, no per-request anti-replay token, no throttling). The raw rate-limit facet stays with **API4**. Conservative: it only flags when the endpoint actually accepts the repeated POST (`2xx`); a base that rejects POST or is unreachable is `[----]` (skipped) rather than a false pass.
**Needs:** a `--target` that accepts a state-changing POST — a sensitive-flow endpoint (checkout, invite, vote, signup), not the service root.

### API7:2023 — Server Side Request Forgery ✅
Timing differential: finds URL-input query parameters on `--target` (`url`, `uri`, `callback`, `webhook`, `dest`, `image`, …), swaps each for a non-routable blackhole address, and flags a large latency stall (the server tried to fetch it).
**Needs:** a `--target` that carries a URL parameter.

### API8:2023 — Security Misconfiguration ✅
Active CORS check (arbitrary-Origin reflection / `*` / `null`, escalated to high with credentials) + missing security headers (HSTS on https, `X-Content-Type-Options`; CSP + clickjacking on HTML responses only). Complements the always-on TLS / banner / verbose-error [built-ins](../../features/scan.md#built-in-passive-checks).
**Needs:** nothing.

### API9:2023 — Improper Inventory Management ✅
Older API versions still routed alongside the target's version, publicly-readable inventory / doc surfaces (`openapi.json`, `swagger.json`, `actuator`, …), and endpoints advertising `Deprecation` / `Sunset` yet still served. A catch-all baseline suppresses false positives. Two **protocol-specific variants** extend the same entry beyond HTTP (see [Protocol-specific probes](#protocol-specific-probes)): **GraphQL introspection** and **gRPC server reflection** exposed anonymously — each the protocol analog of an exposed API inventory.
**Needs:** nothing.

### API10:2023 — Unsafe Consumption of APIs ✅
A server-side concern (the API trusting data it consumes from upstream third-party APIs without validation), so the vulnerable code path is out of black-box reach. The probe takes the passive-heuristic route: it flags the one thing observable from outside — the target **leaking raw upstream data back to the client**, i.e. a reflected upstream / gateway error (`upstream connect error`, `502 Bad Gateway` naming a backend, `ECONNREFUSED` / `getaddrinfo`) or a `3xx` redirect to a **different host**. When no signal fires it does not claim a pass: it Skips with a review-only note pointing at a code / config review of the target's outbound integrations.
**Needs:** nothing (passive over the base response).

## Protocol-specific probes

The ten probes above speak HTTP. Bowire also runs **protocol-specific probes** that drive a protocol plugin's own invoke / discovery path to reach vulnerability classes that only exist below HTTP, tagging their findings to the same OWASP entry so they roll up into the coverage table:

| Probe | Protocol | Rolls up to | Check |
|---|---|---|---|
| GraphQL introspection | `graphql` | API9 | An anonymous `__schema` introspection query returns the schema — public introspection lets anyone map the whole API surface. |
| gRPC server reflection | `grpc` | API9 | Anonymous gRPC Server Reflection returns services — anyone can enumerate every service, method, and message schema without a `.proto`. |
| gRPC transport auth | `grpc` | API2 | When `--auth-header` asserts a credential is expected, one read-only, unary, reflection-discovered method is invoked **without** it; a gRPC status trailer showing the call reached the handler (rather than `Unauthenticated` / `PermissionDenied`) means auth isn't enforced at the transport. |

The reflection and introspection checks are **discovery-only** (they never invoke a method or mutate the target); the gRPC transport-auth check invokes exactly one method, gated to a read-only name so it can't trip a mutating flow, and only when reflection already surfaced a method to test and `--auth-header` says auth is expected. All probe **anonymously**, so they measure real external exposure. Each runs only when its protocol plugin is deployed next to the host (the `bowire` tool ships them); an absent plugin, a target that doesn't speak the protocol, or a per-probe timeout is reported `[----]` (skipped), never a false pass. Further variants (WS / MQTT / SSE / MCP) are tracked in [#184](https://github.com/Kuestenlogik/Bowire/issues/184) / [#381](https://github.com/Kuestenlogik/Bowire/issues/381).

## Coverage

All ten entries have a dedicated probe. API6 (business-flow friction) and API10 (unsafe upstream consumption) have no clean generic black-box check, so their probes are conservative — a real `[ok]` / `[VULN]` verdict only on a strong signal, otherwise `[----]` (skipped) with a review-only reason rather than a false pass. See the [security-testing ADR](../../architecture/security-testing.md) for the rationale, and [`bowire scan`](../../features/scan.md) for the full CLI.
