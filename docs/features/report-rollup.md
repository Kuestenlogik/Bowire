---
title: Report rollup
description: One view over every Bowire report across a portfolio of services.
---

# Report rollup

Per-service findings answer "is *this* service healthy?" The rollup answers it for a portfolio: one row per service, folded from the reports Bowire already writes.

Nothing new has to be produced for it. Everything it reads is an artefact some Bowire command already emitted:

| Source | What it contributes |
|--------|---------------------|
| `bowire lint --format json` | findings by severity |
| `bowire contract verify` | contract pass / fail (filed under the **provider**) |
| scheduled benchmark runs, `bowire bench run --k6-summary` | latest p95, threshold verdict |
| `bowire scan` (SARIF) | findings at error level |
| `bowire test --junit` | tests passed / total |

## From the CLI

```bash
bowire report rollup --from reports/
```

```
  SERVICE      WORST    LINT (H/M/L)   CONTRACTS   TESTS       P95       LAST
  billing-api  HIGH     —              0/1         —           312ms     2026-08-20
  gateway      HIGH     —              —           —           —         —
               scan: 1 finding(s) at error level
  orders-api   MEDIUM   0/1/1          1/1         42/42       —         2026-08-19

  3 service(s) · 2 at high · 0 clean · 1 file(s) skipped
```

| Flag | Meaning |
|------|---------|
| `--from <path…>` | **Required.** Files or directories to read; directories are walked recursively. Repeatable. |
| `--json` | Emit the wire shape instead of the table. |
| `--fail-on <severity>` | Exit non-zero when a service is at or above `info` / `low` / `medium` / `high`. Default `none`. |
| `--service <name>` | Attribute every report to one service instead of inferring it. |

### An em dash is not a zero

`—` means **there is no such report**; `0/1` means the report exists and one contract failed. That distinction is the point: a service nobody has linted must never read as a clean bill of health. In `--json` the same distinction is `null` versus a number.

### How a report is attributed to a service

In this order:

1. `--service`, when given.
2. The report's own identity, where the format has one — a contract's **provider** is the service under test, so a broken contract lands in the provider's row rather than the consumer's.
3. The first path segment under the scanned root that isn't storage layout, so `reports/orders-api/lint.json` is `orders-api` and `.bowire/contract-results/web__orders.json` doesn't invent a service called `contract-results`.
4. The file name.

A CI job that gathers reports into `reports/<service>/` therefore needs no configuration at all.

### Worst severity

The `WORST` column is what `--fail-on` compares against:

- **high** — a failing contract, a failing test, a scan finding at error level, or a high lint finding. These are regressions, not style notes.
- **medium** — a breached latency budget, or a medium lint finding.
- **low** / **info** — lint findings only.
- **ok** — reports were read and nothing is outstanding.

## In the workbench

The **Rollup** rail renders the same table. Point it at a path, press *Roll up*, and click a row to see exactly which files fed it — a surprising number should never have to be taken on faith.

## From an agent

The `bowire.report.rollup` MCP tool returns the same shape, so an agent can answer the portfolio question without shelling out. Like the CLI and the rail, it only reads files; it never calls a service.

## In CI

```yaml
# after collecting each service's reports into reports/<service>/
- run: bowire report rollup --from reports/ --fail-on high
```

All three surfaces emit **the same JSON**, so a script can treat them alike.

## Scope

This reads reports that already exist on disk. Uploading them to a shared store, retaining history for trends, org login and admin actions (dismiss / known-issue / file a ticket) are the hosted dashboard, tracked separately in #188.

## See also

- [Contract testing](contract-testing.md) — one of the inputs, and the matrix this rollup sits one level above.
- [Performance graphs](performance.md) — benchmark thresholds and the k6 summary the rollup reads.
- [PR report bot](../setup/pr-bot.md) — produces a per-repo report from the same artefacts.
