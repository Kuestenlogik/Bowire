---
summary: 'Three seed vulnerability templates that demonstrate the recording-as-attack-replay format. Each file is a regular Bowire recording with `attack: true`, a `vulnerability` metadata block, and a `vulnerableWhen` predicate the bowire scan subcommand evaluates against the target response.'
---

# Vulnerability template examples

Three seed templates that demonstrate the **recording-as-attack-replay** format the Tier-1 anchor of the [security-testing ADR](../../architecture/security-testing.md) introduces. Each one is a regular `BowireRecording` JSON file with three additional fields the scanner consumes:

- `attack: true` — flags this file as a security probe (not a fixture for `bowire mock`)
- `vulnerability: {...}` — identifying + classification metadata (id, CWE, OWASP API mapping, severity, CVSS, references, remediation)
- `vulnerableWhen: {...}` — predicate-tree that fires when the response indicates the target is vulnerable

The probe itself is the recording's first `steps` entry. The scanner replays that step against `--target`, evaluates `vulnerableWhen` against the response, and emits a finding when the predicate matches.

## Running them

```
bowire scan --target https://api.example.com --corpus docs/security/examples
```

To run a single template:

```
bowire scan --target https://api.example.com --template docs/security/examples/graphql-introspection.json
```

To emit SARIF for CI dashboards (GitHub Code Scanning, GitLab Security Dashboard, Azure DevOps):

```
bowire scan --target https://api.example.com --corpus docs/security/examples --out findings.sarif
```

Filter to high-severity findings only:

```
bowire scan --target https://api.example.com --corpus docs/security/examples --severity high
```

## The three seeds

| File | Severity | What it tests | Why it matters |
|---|---|---|---|
| `grpc-server-reflection.json` | high | gRPC Server Reflection enabled in production | Leaks the entire service catalogue + method signatures to anyone who can reach the port. Reflection is meant for dev tooling; ASP.NET Core ships it on by default if you wire it. |
| `graphql-introspection.json` | medium | GraphQL `__schema` introspection enabled | Same shape, GraphQL flavour. Every public schema field becomes an attack-surface map for the caller. Most frameworks ship it on. |
| `rest-missing-security-headers.json` | low | Baseline browser-protection headers missing | Clickjacking (X-Frame-Options), MIME-sniffing (X-Content-Type-Options), downgrade attacks (HSTS), arbitrary script injection (CSP). The "do you have security-headers middleware wired?" check. |

## Template structure

```json
{
  "id": "your-id",
  "name": "Human-readable title",
  "attack": true,
  "vulnerability": {
    "id": "STABLE-ID",
    "cwe": "CWE-NNN",
    "owaspApi": "APIn-YYYY-NAME",
    "severity": "low | medium | high | critical",
    "cvss": 7.5,
    "protocols": ["rest", "graphql", ...],
    "references": ["https://..."],
    "remediation": "Free-form Markdown describing the fix."
  },
  "steps": [
    {
      "id": "probe-1",
      "protocol": "rest",
      "service": "...",
      "method": "...",
      "methodType": "Unary",
      "httpVerb": "GET",
      "httpPath": "/some/path",
      "body": "...",
      "metadata": { "Header-Name": "value" }
    }
  ],
  "vulnerableWhen": {
    "allOf": [
      { "status": 200 },
      { "bodyJsonPath": { "path": "$.foo", "exists": true } }
    ]
  }
}
```

### Predicate operators

**Leaf operators** (test one property of the response):

| Operator | Matches when |
|---|---|
| `status: <int>` | HTTP status equals |
| `statusIn: [int, ...]` | HTTP status is in the list |
| `bodyContains: "<substr>"` | Response body contains the literal substring |
| `bodyMatches: "<regex>"` | Response body matches the regex |
| `bodyJsonPath: { path, exists / equals / matches / anyValueMatches }` | JSONPath result satisfies the inner operator |
| `headerEquals: { Name: "value" }` | Header value equals (case-insensitive name) |
| `headerExists: ["Name"]` | Header is present |
| `headerMissing: ["Name"]` | Header is NOT present (useful for security-header checks) |
| `latencyMsAtLeast: <int>` | Response latency ≥ N ms (blind-SQLi / timing-oracle) |

**Composite operators** (combine sub-predicates — nest arbitrarily):

| Operator | Matches when |
|---|---|
| `allOf: [<pred>, ...]` | Every sub-predicate matches |
| `anyOf: [<pred>, ...]` | At least one sub-predicate matches |
| `not: <pred>` | The sub-predicate does NOT match |

### JSONPath subset

The `bodyJsonPath.path` field accepts the same JSONPath subset the workbench's `bowireResolveJsonPath` supports:

- `$` — root
- `$.foo` — object property
- `$.foo.bar` — nested property
- `$.foo[0]` — array index
- `$.foo[*]` — array wildcard (returns every element)
- `$.foo[*].name` — wildcard + further navigation

## Contributing templates

The seed templates here are a starting point. The intent (per the [security-testing ADR](../../architecture/security-testing.md)) is to land a separate `kuestenlogik/bowire-vulndb` community repo that holds the bulk of the corpus with per-template CI validation against vulnerable-by-design containers. Until that repo exists, dropping a new template here and opening a PR against the main Bowire repo is fine.

Template authoring conventions:

- **Use a stable `id`** — once published, never reuse. The CI dashboards group by `id`; renaming breaks history.
- **Lower the severity bound when you're unsure.** Operators filter the high-severity templates first; better to be reported as `medium` and run than skipped because the severity was inflated.
- **Pair an `anyOf` of detection-signals** in the predicate rather than a single brittle regex. The seed templates do this — multiple signals tolerate minor server-response variations.
- **The `remediation` field is the most important part of the template.** A finding without an actionable fix is just noise.
