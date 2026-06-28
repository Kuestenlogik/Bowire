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
| `--no-builtins` | off | Skip the always-on passive checks. |
| `--allow-self-signed-certs` | off | Trust self-signed TLS on the target — useful for staging. |

## Built-in passive checks

Three checks fire on every run regardless of template flags:

* **TLS version enumeration.** Raw `SslStream` handshakes against TLS 1.0 / 1.1 / 1.2 / 1.3 in sequence. Accepted handshakes on deprecated versions surface as high-severity findings (CWE-326).
* **Banner / version disclosure.** Scans for `Server`, `X-Powered-By`, `X-AspNet-Version`, `X-AspNetMvc-Version`, `Via` headers.
* **Verbose-error detection.** Trips default error pages (random path → 404, null-byte URL → 500, malformed query → 4xx) and regex-scans for stack-trace markers (CWE-209).

Each disclosed marker becomes its own SARIF rule so the Code-Scanning dashboard groups correctly.

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

* [`bowire fuzz`](fuzz.md) — schema-aware field-level mutation, same SARIF output
* [`bowire proxy`](proxy.md) — intercepting MITM proxy for capturing real sessions as templates
* [Recording](recording.md) — captured sessions are templates the scanner can replay
* [Security-testing ADR](../architecture/security-testing.md) — full design rationale
