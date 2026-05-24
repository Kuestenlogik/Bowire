---
summary: 'Schema-aware field-level fuzzing with `bowire fuzz`. Knows what each field expects, so a `lat` doesn''t see SQL-injection and an `image.bytes` gets magic-byte mutation instead of XSS strings. Baseline-diff oracle flags responses that look materially different from clean runs.'
---

# Field-level Fuzz — `bowire fuzz`

`bowire fuzz` runs schema-aware mutation against a target endpoint and emits SARIF 2.1.0. The fuzzer knows what each field expects — a numeric `lat` doesn't see SQL-injection payloads, a binary `image.bytes` field gets magic-byte mutation instead of XSS strings, an `email` field gets RFC 5322 edge cases.

The baseline-diff oracle decides what counts as a finding: each mutated response is compared against the unmutated baseline. Status change, error-shape divergence, latency spike beyond *N* standard deviations — any of those flags the input.

## Quick start

```bash
bowire fuzz --target https://api.example.com \
            --template recordings/order.bwr \
            --field '$.customer.email' \
            --payloads sqli,xss --out fuzz.sarif
```

Without `--field`, the fuzzer enumerates every leaf field in the request body and mutates each one in turn — useful for a coverage sweep against a small surface.

## Flags

| Flag | Default | Notes |
|---|---|---|
| `--target, -t <url>` | &mdash; | Required. Target URL; same shape as `bowire scan`. |
| `--template <path>` | &mdash; | Required. A recording (`.bwr`) that provides the baseline request and response shape. |
| `--field <jsonpath>` | all leaves | JSONPath to a single field to mutate. Repeatable. |
| `--payloads <list>` | all | Comma-separated payload categories: `sqli`, `xss`, `pathtrav`, `cmdinj`. |
| `--out, -o <path>` | `fuzz.sarif` | SARIF 2.1.0 output. |
| `--severity <level>` | `low` | Minimum severity to report. |
| `--baseline-runs <n>` | `3` | Clean runs to establish the response baseline (status, latency mean / std-dev). |
| `--latency-stdev <n>` | `4` | Number of standard deviations above baseline to count as a latency anomaly. |

## Payload categories

Each category ships with a curated wordlist; the scanner picks the entries that fit the target field's declared type.

| Category | Targets | Sample payloads |
|---|---|---|
| `sqli` | string fields | `' OR '1'='1`, `'; DROP TABLE`, `' UNION SELECT NULL--` |
| `xss` | string fields rendered as HTML | `<script>alert(1)</script>`, `"><svg onload=…>`, javascript-URI variants |
| `pathtrav` | file-path / URL fields | `../../etc/passwd`, `..\\..\\windows\\win.ini`, URL-encoded forms |
| `cmdinj` | string fields passed to OS shells | `; ls`, `\| cat /etc/passwd`, backtick + `$()` forms |

Numeric, boolean, and binary fields skip these categories and get type-appropriate mutation (boundary values, NaN / ∞, magic-byte flips for binary).

## Baseline-diff oracle

A finding requires the mutated response to *materially differ* from the clean baseline. The oracle compares four signals:

1. **Status code** — anything other than the baseline's status counts.
2. **Response shape** — JSONPath-aware diff of returned fields; new keys, missing keys, type changes all flag.
3. **Error markers** — stack-trace fragments, SQL-error regexes, `Stack overflow at line …` patterns.
4. **Latency** — > `--latency-stdev` standard deviations above baseline mean. Catches DoS-style payloads that trip into slow paths.

Any one signal is enough; all four firing on the same input becomes a high-severity composite finding.

## Exit-code semantics

Same as [`bowire scan`](scan.md) — exits 0 whenever the fuzz ran end-to-end. Findings are output; gating happens at the SARIF-consumer level (Code Scanning rules, jq post-processing).

## Related

* [`bowire scan`](scan.md) — vulnerability templates against a target
* [`bowire proxy`](proxy.md) — capture real sessions as templates for the fuzzer
* [Recording](recording.md) — `.bwr` format the fuzzer reads as its baseline
* [Security-testing ADR](../architecture/security-testing.md)
