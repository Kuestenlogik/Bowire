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

Workbench — the **Security rail** has a target box and a *Run OWASP suite* button that paints each row covered / clean / vulnerable, backed by `POST /api/security/owasp-scan`. `GET /api/security/owasp-catalog` returns the ten entries as static metadata.

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
Establishes an authenticated 2xx baseline, then checks: authentication not enforced (2xx with no credential), JWT forgery (`alg:none` re-header + tampered signature), and token lifetime (expired token accepted / no `exp` claim). Forgery + expiry findings are gated on anonymous access being blocked, so a public API isn't misreported.
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

### API6:2023 — Unrestricted Access to Sensitive Business Flows ⛔
No reliable black-box probe — this needs business-flow semantics. The generic rate-limit facet is covered by **API4**. Stays `[----]` by design.

### API7:2023 — Server Side Request Forgery ✅
Timing differential: finds URL-input query parameters on `--target` (`url`, `uri`, `callback`, `webhook`, `dest`, `image`, …), swaps each for a non-routable blackhole address, and flags a large latency stall (the server tried to fetch it).
**Needs:** a `--target` that carries a URL parameter.

### API8:2023 — Security Misconfiguration ✅
Active CORS check (arbitrary-Origin reflection / `*` / `null`, escalated to high with credentials) + missing security headers (HSTS on https, `X-Content-Type-Options`; CSP + clickjacking on HTML responses only). Complements the always-on TLS / banner / verbose-error [built-ins](../../features/scan.md#built-in-passive-checks).
**Needs:** nothing.

### API9:2023 — Improper Inventory Management ✅
Older API versions still routed alongside the target's version, publicly-readable inventory / doc surfaces (`openapi.json`, `swagger.json`, `actuator`, …), and endpoints advertising `Deprecation` / `Sunset` yet still served. A catch-all baseline suppresses false positives.
**Needs:** nothing.

### API10:2023 — Unsafe Consumption of APIs ⛔
No black-box probe — this is a server-side concern (the API consuming upstream third-party APIs without validation), not observable from outside. Stays `[----]` by design.

## Coverage

Eight of ten entries have a dedicated probe. API6 and API10 have no reliable automated black-box check and remain review-only. See the [security-testing ADR](../../architecture/security-testing.md) for the rationale, and [`bowire scan`](../../features/scan.md) for the full CLI.
