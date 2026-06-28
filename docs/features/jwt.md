---
title: JWT
summary: 'Inspect, validate, and tamper with JSON Web Tokens via `bowire jwt`. Decodes header / payload / signature, checks `exp` / `nbf` / `iat`, probes `alg: none` acceptance, re-signs with a chosen secret, overrides individual claims.'
---

# JWT Toolkit — `bowire jwt`

`bowire jwt` is the security-focused JWT helper inside the Bowire CLI. Two subcommands cover the everyday JWT smoke-test flow:

* **`bowire jwt decode`** — split header / payload / signature, validate temporal claims, list every present claim.
* **`bowire jwt tamper`** — probe `alg: none` acceptance, re-sign with a chosen secret, override individual claims.

It ships in the same binary as [`bowire scan`](scan.md), [`bowire fuzz`](fuzz.md), and [`bowire proxy`](proxy.md). Authentication for live API calls is a separate concern — see [Authentication](authentication.md) for the workbench's auth-helper panel.

## Decode — inspect a token

```bash
bowire jwt decode \
  eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjMifQ.HMAC
```

Output:

```text
Header:
  alg: HS256
  typ: JWT

Payload:
  sub: "123"
  exp: 1735776000  (2025-01-02 00:00:00 UTC, expires in 5d 14h)
  iat: 1735689600  (2024-12-31 00:00:00 UTC)

Signature:
  HMAC-SHA256 (32 bytes)
  Verification: skipped (no --secret)

Claims:
  exp:  OK   (not expired)
  nbf:  n/a  (not present)
  iat:  OK   (in the past)
```

Validate the signature too:

```bash
bowire jwt decode <token> --secret "shhh"
# Verification: OK
```

`--secret` accepts a literal value, `@file` to read from a file, or `env:NAME` to read from an environment variable.

### Decode flags

| Flag | Default | Notes |
|---|---|---|
| `<token>` | &mdash; | Required. Positional. |
| `--secret <value>` | none | HMAC secret or PEM public key for signature verification. |
| `--json` | off | Machine-readable JSON output instead of the human-formatted block. |
| `--at <iso8601>` | now | Override "current time" for `exp` / `nbf` evaluation. |

## Tamper — generate variants for testing

`bowire jwt tamper` is intentionally a security-test tool: it produces deliberately altered tokens so you can confirm your service rejects them.

```bash
# Strip the signature and switch to alg: none
bowire jwt tamper <token> --alg none

# Re-sign with a known weak secret
bowire jwt tamper <token> --alg HS256 --secret "password"

# Override a claim
bowire jwt tamper <token> --claim 'sub="admin"' --alg HS256 --secret "shhh"

# Combine — tamper a claim AND switch to alg none
bowire jwt tamper <token> --claim 'role="admin"' --alg none
```

### Tamper flags

| Flag | Default | Notes |
|---|---|---|
| `<token>` | &mdash; | Required. Base token to tamper. |
| `--alg <value>` | original | Set the `alg` header. Common values: `none`, `HS256`, `RS256`. |
| `--secret <value>` | none | Required when `--alg` is symmetric (HS*). |
| `--key <pem-path>` | none | PEM private key for asymmetric algs (RS*, ES*, PS*). |
| `--claim '<name>=<value>'` | &mdash; | Repeatable. Override a payload claim. Value is parsed as JSON (`5`, `true`, `"str"`, `["arr"]`). |
| `--remove-claim <name>` | &mdash; | Repeatable. Drop a claim. |
| `--out <path>` | stdout | Write the tampered token to a file. |

## The standard smoke-test sequence

Three runs catch the textbook JWT vulnerabilities:

1. **`alg: none` acceptance** — send a tampered token with `--alg none` and no signature. Service must reject (CWE-345).
2. **Weak-secret HMAC** — re-sign with `--secret "password"` / `"changeme"` / `"secret"`. Service must reject if it doesn't actually expect that secret.
3. **Claim override** — change `sub`, `role`, `aud`, or any policy-bearing claim. Service must reject when the signature is recomputed with the wrong key.

The toolkit is shipped alongside the scan engine, not separately — it lives in the same CLI binary your CI is already calling.

## Related

* [Authentication](authentication.md) — JWT *configuration* for live workbench requests (signing your own valid tokens to call the API)
* [`bowire scan`](scan.md) — automate the JWT smoke-test sequence as a security template
* [Security-testing ADR](../architecture/security-testing.md)
