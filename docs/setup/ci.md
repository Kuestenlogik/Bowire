---
title: Bowire in CI
summary: "Run bowire test / contract verify headless in any pipeline — exit codes, JUnit / SARIF reports, and a ready-made GitHub Action."
---

# Bowire in CI

`bowire` is a self-contained .NET tool, so any CI runner that can install a .NET global tool can run your API tests headless. Every runner exits non-zero on failure (unless you soften it with `--fail-on never`) and can emit JUnit XML + SARIF for the platform's native reporters.

## The command

```bash
dotnet tool install -g Kuestenlogik.Bowire.Tool

# one flow / collection
bowire test ./flows/smoke.json --junit results.xml --sarif results.sarif

# every flow in a git-native workspace directory (aggregates pass/fail)
bowire test --workspace ./bowire --junit results.xml
```

| Flag | Meaning |
|------|---------|
| `--junit <file>` | JUnit XML — Jenkins, GitLab CI, Azure DevOps, GitHub test reporters. |
| `--sarif <file>` | SARIF 2.1.0 — GitHub Code Scanning tab. |
| `--annotations` | GitHub `::error` inline PR annotations (no reporter action needed). |
| `--fail-on any \| never` | `any` (default) exits non-zero on any failed check; `never` runs + reports but always exits 0 (a step **error** still exits 2, so a broken backend is never masked). |
| `--workspace <dir>` | Run every Flow JSON in a workspace directory's `flows/` folder; per-flow reports are written as `<report>.<flow>.<ext>` so a glob picks them all up. |
| `--env-file <f>` / `--env KEY=VAL` | Feed the `{{var}}` resolver; secrets stay in env vars, never in checked-in files. |
| `--secret <NAME>` / `--secret-file <f>` | Mark variables whose resolved values must be redacted from every output (see [Secrets](#redacting-secrets)). |

Exit codes: **0** all passed · **1** an assertion / expectation failed · **2** a step errored before evaluation (backend down, malformed file).

## Redacting secrets

A failing assertion prints the expected + actual values, and those land verbatim in the TTY log, the JUnit failure text, the SARIF message, and the GitHub `::error` annotation. If a token or password flows through a request or a response, mark the variable that carries it as a secret and Bowire masks its **value** everywhere it would otherwise appear:

```bash
# resolve DEPLOY_TOKEN normally, but redact its value from every output
bowire test ./flows/smoke.json \
  --env-file .env.ci \
  --secret DEPLOY_TOKEN --secret API_KEY \
  --junit results.xml --sarif results.sarif --annotations
```

- `--secret <NAME>` is repeatable; `--secret-file <file>` reads one variable **name** per line (blank lines and `#` comments ignored) so a long list stays off the command line. The file holds names, never values — the value is resolved from the run's variable scope (`--env`, `--env-file`, `--keyring`), so **secret values never live in a checked-in file**.
- The variable still resolves normally into the request/assertion — only its rendered value is masked in the output. A value longer than eight characters renders as `***` plus its last four (e.g. `***c5f9`) so two different secrets stay distinguishable in a diff; a shorter value collapses to a bare `***`. The mask is stable across a run.
- Empty / whitespace values are never redacted (masking `""` would blank every line).

Feed the actual value in from your CI's secret store through `--env` / `--env-file` (or `--keyring`), exactly as you already do for any other variable — the checked-in flow file only references `{{DEPLOY_TOKEN}}`:

```yaml
      - run: >-
          bowire test ./flows/smoke.json --junit results.xml --annotations
          --env DEPLOY_TOKEN=${{ secrets.DEPLOY_TOKEN }}
          --secret DEPLOY_TOKEN
```

## GitHub Actions

Use the bundled composite action — it installs the tool and runs any `bowire` command:

```yaml
name: API tests
on: [push, pull_request]

jobs:
  bowire:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: Kuestenlogik/Bowire/packaging/github-action@v2
        with:
          dotnet-version: "10.0.x"
          args: test --workspace ./bowire --junit results.xml --sarif results.sarif --annotations

      - name: Publish test report
        if: always()
        uses: dorny/test-reporter@v1
        with:
          name: Bowire
          path: results*.xml
          reporter: java-junit

      - name: Upload SARIF
        if: always()
        uses: github/codeql-action/upload-sarif@v3
        with:
          sarif_file: results.sarif
```

Or without the action, straight from a shell step:

```yaml
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: "10.0.x" }
      - run: dotnet tool install -g Kuestenlogik.Bowire.Tool
      - run: bowire test ./flows/smoke.json --junit results.xml --annotations
```

## Contract testing in CI

The consumer publishes, the provider verifies — see [Contract testing](../features/contract-testing.md):

```yaml
# consumer pipeline
- uses: Kuestenlogik/Bowire/packaging/github-action@v2
  with:
    args: >-
      contract publish traces.bwr --provider order-service
      --broker-url ${{ secrets.PACT_BROKER_URL }}
      --consumer-version ${{ github.sha }} --tag ${{ github.ref_name }}

# provider pipeline
- uses: Kuestenlogik/Bowire/packaging/github-action@v2
  with:
    args: >-
      contract verify --broker-url ${{ secrets.PACT_BROKER_URL }}
      --provider order-service --tag main
      --provider-url http://localhost:8080 --junit contract-results.xml
```

## GitLab CI

```yaml
bowire:
  image: mcr.microsoft.com/dotnet/sdk:10.0
  script:
    - dotnet tool install -g Kuestenlogik.Bowire.Tool
    - export PATH="$PATH:$HOME/.dotnet/tools"
    - bowire test --workspace ./bowire --junit results.xml
  artifacts:
    when: always
    reports:
      junit: results*.xml
```
