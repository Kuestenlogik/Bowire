---
title: Contract testing
summary: "Turn a recording into a Pact-style consumer contract, then verify the provider still honours it — in CI, with the same reports as bowire test."
---

# Contract testing

Microservice teams break each other when one side changes an API without telling the other. **Contract testing** catches that at build time: the consumer declares "these are the requests I make and the responses I depend on", and the provider's CI replays those interactions and fails the build if anything no longer matches.

Bowire hosts **both sides** with no extra integration cost — a recording is already a consumer-side trace, so a contract is one command away. Contracts are **Pact-compatible** (Pact Specification v3), so they publish to and pull from a standard [Pact Broker](https://docs.pact.io/pact_broker).

> Contracts are HTTP/REST. Pact is an HTTP contract format and brokers reject non-HTTP shapes, so `contract publish` projects only the REST steps of a recording into interactions and skips the rest.

## Consumer side — `bowire contract publish`

Turn a recording captured on the consumer into a contract file:

```bash
bowire contract publish orders-consumer.bwr --provider order-service
# → Contract written to orders-consumer-order-service.pact.json (2 interactions).
```

Each REST step becomes one interaction — its request (verb, path, body, HTTP headers) and its recorded response (status + body) become the expected shape.

| Flag | Meaning |
|------|---------|
| `--provider <name>` | **Required.** The provider this contract is against. |
| `--consumer <name>` | Consumer name. Defaults to the recording's name. |
| `--out <file>` | Output path. Defaults to `<consumer>-<provider>.pact.json`. |
| `--broker-url <url>` | Also push to a Pact Broker (outbound — opt-in). Omit to only write the file. |
| `--consumer-version <v>` | Consumer version for the broker publish. Defaults to a timestamp. |
| `--tag <tag>` | Tag the published consumer version (e.g. a branch name). |

Publishing to a broker:

```bash
bowire contract publish orders-consumer.bwr \
  --provider order-service \
  --broker-url "$PACT_BROKER_URL" --consumer-version "$GIT_SHA" --tag main
```

## Provider side — `bowire contract verify`

Replay a contract's interactions against the live provider and fail on any mismatch:

```bash
bowire contract verify orders-consumer-order-service.pact.json \
  --provider-url http://localhost:8080
```

```
  Contract verify   orders-consumer → order-service   (2 interactions)

  PASS  GET  GET /orders/42   200 · 6ms
  FAIL  POST POST /orders     201 · 2ms
        ✗ body matches-shape — $.id: missing (present in snapshot)

  1/2 interactions held   3/4 checks   in 71 ms
```

Pull the latest contract from a broker instead of a file:

```bash
bowire contract verify \
  --broker-url "$PACT_BROKER_URL" --provider order-service --tag main \
  --provider-url http://localhost:8080
```

| Flag | Meaning |
|------|---------|
| `--provider-url <url>` | **Required.** Base URL of the live provider to replay against. |
| `--broker-url <url>` + `--provider <name>` | Pull the latest contract from a broker (outbound — opt-in) instead of passing a file. |
| `--tag <tag>` | Pull the latest contract carrying this tag. |
| `--junit <file>` | Write a JUnit XML report (Jenkins / GitLab / Azure DevOps / GitHub). |
| `--sarif <file>` | Write a SARIF 2.1.0 report (GitHub Code Scanning). |

### Matching semantics

Verification is **structural**, matching Pact's intent: the provider may add fields and vary values, but every field the consumer relies on must be present with the same JSON kind.

- **Status** must match exactly.
- **Body** — each field in the contract's expected body must exist in the actual response with the same JSON type. Extra fields on the provider are fine; a **missing** or **type-changed** field the consumer depends on fails the interaction (e.g. `$.status: missing`).

Exit codes match `bowire test`: **0** = every interaction held, **1** = a mismatch.

## The matrix — consumer × provider at a glance

Every `bowire contract verify` run stores its verdict under `.bowire/contract-results/` (one file per consumer × provider pair, newest run wins). The matrix rolls those stored results up into a grid: rows are consumers, columns are providers, each cell carries pass/fail, how many interactions held, and when it last ran.

```bash
bowire contract matrix
```

```
            weather-service
dashboard   PASS 1/1
mobile-app  FAIL 0/1

  1 passing · 1 failing
```

| Flag | Meaning |
|------|---------|
| `--json` | Emit the matrix as JSON instead of the text grid. |
| `--fail-on-failures` | Exit **1** when any cell is failing — the CI gate. |

Pairs nobody verified appear as `—`: the grid is the full cross-product, so a missing verification is visible rather than silently absent.

The same rollup is available from all four surfaces, from one shared engine:

| Surface | How |
|---------|-----|
| CLI | `bowire contract matrix` (text grid, `--json`, `--fail-on-failures`) |
| Workbench | the **Contracts** rail — the grid, with drill-in to a failing interaction's shape diff |
| HTTP | `GET /api/contracts/matrix` |
| MCP | the `bowire.contract.matrix` tool — returns each cell plus, for failing cells, only what broke |

Reading the matrix **never contacts a provider**. It reports what `verify` already stored, so opening the workbench rail cannot trigger outbound calls; verification stays an explicit act on the CLI or in CI.

In the workbench, the Contracts rail ships in `Kuestenlogik.Bowire.Contracts`. The standalone tool bundles it; an embedded host opts in by referencing the package.

## In CI

```yaml
# consumer pipeline — publish after recording/building tests
- run: bowire contract publish traces.bwr --provider order-service \
       --broker-url ${{ secrets.PACT_BROKER_URL }} --consumer-version ${{ github.sha }} --tag ${{ github.ref_name }}

# provider pipeline — verify before deploy
- run: bowire contract verify --broker-url ${{ secrets.PACT_BROKER_URL }} \
       --provider order-service --tag main --provider-url http://localhost:8080 \
       --junit contract-results.xml

# gate on the rollup after verifying several contracts
- run: bowire contract matrix --fail-on-failures
```

The broker path (publish push / verify pull) is always **opt-in** via an explicit `--broker-url` — Bowire never reaches the network on its own.

## See also

- [Recording](recording.md) — capture the consumer-side traces contracts are built from.
- [`bowire test`](cli-mode.md) — the flow/collection CI runner contracts share their report surface with.
