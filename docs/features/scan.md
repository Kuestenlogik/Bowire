---
title: Scan
summary: 'Run vulnerability templates against any HTTP-class target with `bowire scan`. Three template sources, one engine, SARIF 2.1.0 to GitHub Code Scanning / GitLab / Azure DevOps.'
---

# Security Scan — `bowire scan`

`bowire scan` runs vulnerability templates against a target URL and emits SARIF 2.1.0. Three template sources feed the same `AttackPredicate` engine:

1. **Built-in passive checks** — always on; no template file required.
2. **`Bowire.VulnDb`** — curated baseline template set, ships as a sibling NuGet ([`Kuestenlogik.Bowire.VulnDb`](https://github.com/Kuestenlogik/Bowire.VulnDb)).
3. **`projectdiscovery/nuclei-templates`** — 8000+ community-maintained YAML templates, opt-in via `--nuclei <dir>`.

Same engine, three corpora, one report. SARIF lands as the canonical output; every SARIF-aware consumer (GitHub Code Scanning, GitLab Security Dashboard, Azure DevOps) reads it without a translation step.

## Quick start

```bash
dotnet tool install -g Kuestenlogik.Bowire.Tool
bowire scan --target https://api.example.com --out findings.sarif
```

Combined with the Nuclei templates:

```bash
bowire scan --target https://api.example.com \
            --templates ~/.bowire/vulndb \
            --nuclei ~/nuclei-templates \
            --out findings.sarif --severity medium
```

## Flags

| Flag | Default | Notes |
|---|---|---|
| `--target, -t <url>` | &mdash; | Required. The URL to scan. Scheme picks the wire (http, https, grpc, …). |
| `--out, -o <path>` | `findings.sarif` | SARIF 2.1.0 output file. |
| `--templates <dir>` | shipped baseline | `Bowire.VulnDb` directory. |
| `--nuclei <dir>` | off | Path to a `projectdiscovery/nuclei-templates` clone. |
| `--severity <level>` | `low` | Minimum severity in the report (`low` / `medium` / `high` / `critical`). |
| `--suite <name>` | off | Named suite view. `owasp-api` runs the dedicated OWASP API Top 10 probes and prints a per-entry coverage table — see [below](#owasp-api-security-top-10-suite). |
| `--auth-header <hdr>` | &mdash; | Header applied to every probe, e.g. `"Authorization: Bearer <token>"`. Repeatable. Without it, scans of authenticated APIs land on the login wall. |
| `--auth-header-b <hdr>` | &mdash; | A **second** identity's header(s), same shape. Enables the OWASP API1 (BOLA) cross-identity check. Repeatable. |
| `--scope <host>` | target host | In-scope hostname or `*.`-glob. Repeat / comma-separate. Cross-host probes are blocked unless the host is in scope. |
| `--timeout <seconds>` | `30` | Per-probe HTTP timeout. |
| `--no-builtins` | off | Skip the always-on passive checks. |
| `--allow-self-signed-certs` | off | Trust self-signed TLS on the target — useful for staging. |

## Built-in passive checks

Three checks fire on every run regardless of template flags:

* **TLS version enumeration.** Raw `SslStream` handshakes against TLS 1.0 / 1.1 / 1.2 / 1.3 in sequence. Accepted handshakes on deprecated versions surface as high-severity findings (CWE-326).
* **Banner / version disclosure.** Scans for `Server`, `X-Powered-By`, `X-AspNet-Version`, `X-AspNetMvc-Version`, `Via` headers.
* **Verbose-error detection.** Trips default error pages (random path → 404, null-byte URL → 500, malformed query → 4xx) and regex-scans for stack-trace markers (CWE-209).

Each disclosed marker becomes its own SARIF rule so the Code-Scanning dashboard groups correctly.

## OWASP API Security Top 10 suite

`--suite=owasp-api` runs a set of dedicated per-entry probes and prints a coverage table that rolls every finding (templates + built-ins + probes) up against the [OWASP API Security Top 10 (2023)](https://owasp.org/API-Security/editions/2023/en/0x11-t10/):

```bash
bowire scan --target https://api.example.com --suite=owasp-api \
            --auth-header "Authorization: Bearer $TOKEN"
```

```text
  OWASP API Security Top 10 (2023) — suite coverage:
  [VULN] API4:2023  Unrestricted Resource Consumption   1 finding(s) across 1 probe(s)
  [ok]   API5:2023  Broken Function Level Authorization  1 probe(s), clean
  [----] API6:2023  Unrestricted Access to …             not assessed — probe skipped (missing input, or no black-box signal)
  …
  6/10 Top-10 entries exercised; 2 with vulnerability finding(s).
```

Each row is honest: `[----]` means **the probe couldn't assess it** (it lacked a required input, or found no black-box signal) — a clean scan is never a false pass. All ten entries have a dedicated probe. What each checks, and what input it needs:

| Entry | Probe checks | Needs |
|---|---|---|
| **API1** BOLA | Object read by a *second* identity while anonymous is blocked | `--auth-header` **and** `--auth-header-b`, object-scoped `--target` |
| **API2** Broken Authentication | Unauthenticated access, JWT `alg:none` / tampered-signature forgery, expired / no-`exp` token | `--auth-header` |
| **API3** BOPLA (mass assignment) | Server persists an unknown client-supplied (canary) property on PATCH | writable object `--target` |
| **API4** Resource Consumption | No rate limiting (429 / `RateLimit-*`), oversized body accepted | — |
| **API5** BFLA | Privileged management endpoints (actuator, `_cat`, pprof) reachable | — |
| **API7** SSRF | URL-input parameter fetched server-side (timing differential) | `--target` with a URL parameter |
| **API6** Sensitive Business Flows | Repeated identical POST accepted with no anti-automation friction (CAPTCHA / bot-mitigation / anti-replay token / throttle) | POST-accepting sensitive-flow `--target` |
| **API8** Security Misconfiguration | CORS (reflection / `*` / credentials), missing HSTS / `X-Content-Type-Options` / CSP | — |
| **API9** Improper Inventory | Older API versions reachable, exposed inventory/doc surfaces, active `Deprecation`/`Sunset` | — |
| **API10** Unsafe Consumption | Raw upstream / gateway error reflected to the client, or `3xx` redirect to a different host | — |

> API6 (sensitive business flows) and API10 (unsafe upstream consumption) have no clean *generic* black-box check, so their probes are conservative: a real `[ok]` / `[VULN]` verdict only on a strong signal, otherwise `[----]` (skipped) with a review-only reason rather than a false pass. API6's raw rate-limit facet stays with API4; API10 remains a server-side concern best confirmed by code / config review.

Beyond HTTP, the suite also runs **protocol-specific probes** that drive a protocol plugin's own invoke path: **GraphQL introspection**, **gRPC server reflection**, and **MCP tool/resource listing** exposed anonymously (→ API9, the protocol analog of an exposed API inventory), plus **gRPC transport-auth**, **WebSocket**, **MQTT**, and **SSE** anonymous-connect checks (→ API2, given `--auth-header`). They run only when the matching protocol plugin is deployed (the `bowire` tool ships them); an absent plugin or a non-matching target skips cleanly. A non-http/https `--target` (e.g. `mqtt://…`) skips the HTTP-only checks and runs just the protocol probes. See the [OWASP suite reference](../security/owasp-api/index.md#protocol-specific-probes).

The workbench surfaces the same suite in the **Security rail** (a target box + *Run OWASP suite* button paints each row covered / clean / vulnerable), backed by `POST /api/security/owasp-scan` — see the [HTTP API reference](../api/index.md).

## Endpoint discovery — `bowire scan spider`

Real security testing assumes *"find the things the developer forgot to declare"*. `bowire scan spider --url <base>` discovers candidate endpoints so the scanner + [OWASP suite](#owasp-api-security-top-10-suite) have an attack surface — including undocumented ones:

```bash
bowire scan spider --url https://api.example.com --out candidates.json
```

Sources: `robots.txt` (Disallow + Sitemap), `sitemap.xml`, an **OpenAPI/Swagger** document's `paths` (every path + method), a curated common-path HEAD sweep, and same-origin links on the base page. Conservative by default — same-host / `--scope` only, honours `robots.txt` (drop with `--no-robots`), and never authenticates beyond `--auth-header`. Candidates are **surfaced, not auto-added**: confirm the real ones into your workspace, treat the rest as potential leaks.

## Schema-aware mutations — `bowire scan mutate`

Bowire's edge is that it *knows the schema*. `bowire scan mutate` exercises the schema-aware mutation engine for a field type and prints the targeted invalid inputs it produces — type confusion, boundary + overflow, string-encoding tricks, enum out-of-range / case-variant, required-field omission, and structural attacks:

```bash
bowire scan mutate --type integer --seed 42 --budget 8
bowire scan mutate --type enum --enum RED,GREEN,BLUE --seed 1
bowire scan mutate --type string --format email --required
```

Each mutation carries a label and the behaviour a correctly-validating server should exhibit. Output is **seeded** (`--seed` reproduces the exact set) and **budget**-capped (`--budget`) so a scan stays bounded. This is the reproducible building block the scan-replay layer walks a whole OpenAPI / protobuf / GraphQL schema with.

## Exit-code semantics

`bowire scan` exits **0 whenever the scan ran end-to-end** — findings are the *product* of the scan, not a failure signal. Pipelines that want to gate on findings should add their own post-processing step (jq on the SARIF, or a Code Scanning branch-protection rule). The scanner only exits non-zero when the tool itself crashes (template parse fault, scope rejection, etc.).

## CI integration

Drop into any workflow that ingests SARIF. The reusable [`scan-template.yml`](https://github.com/Kuestenlogik/Bowire/blob/main/.github/workflows/scan-template.yml) Action wraps `scan` + SARIF upload behind one `uses:` line:

```yaml
- uses: Kuestenlogik/Bowire/.github/workflows/scan-template.yml@v1
  with:
    target: https://staging.example.com/api
    severity: medium
```

For GitLab CI / Azure DevOps see the [security architecture ADR](../architecture/security-testing.md).

## Related

* [OWASP API Top 10 suite](../security/owasp-api/index.md) — per-entry reference: what each probe checks + the input it needs
* [`bowire fuzz`](fuzz.md) — schema-aware field-level mutation, same SARIF output
* [Interceptor / `bowire proxy`](interceptor.md) — middleware + MITM proxy for capturing real sessions as templates
* [Recording](recording.md) — captured sessions are templates the scanner can replay
* [Security-testing ADR](../architecture/security-testing.md) — full design rationale
