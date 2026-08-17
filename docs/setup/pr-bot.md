---
title: PR report bot
summary: 'Surface a PR''s API / test / security impact as one updating PR comment'
---

# PR report bot

Bowire runs on the developer's machine, so a reviewer sees the code diff but
nothing about the **API impact** of a pull request. The `bowire-pr` GitHub
Action closes that gap: it runs the API-schema delta, the test suite, and the
security scan on the PR's head, and posts a **single comment** — updated in
place on every push — with what changed.

```
## Bowire PR report

| Section | Status |
| --- | --- |
| API schema | 🟢 |
| Tests | 🔴 |
| Security | 🟢 |

**API schema:** +2 methods, ~1 changed.
...
```

> **Prototype (v2.5).** The action currently lives inside the Bowire repo at
> `Kuestenlogik/Bowire/.github/actions/bowire-pr@main` while it stabilises, and
> the Perf section is not wired yet. It will move to its own
> `kuestenlogik/bowire-action` repo with a pinned `@v1` tag once it settles
> (tracked in [#183](https://github.com/Kuestenlogik/Bowire/issues/183)).

## What it does

| Section | Command behind it | Gate input |
| --- | --- | --- |
| **API schema** | `bowire diff` — a base snapshot vs. a head snapshot captured from the running target | `fail-on-schema: none \| breaking \| any` |
| **Tests** | `bowire test --junit` | `fail-on-tests: any \| never` |
| **Security** | `bowire scan` + `bowire scan report --baseline` | `fail-on-scan: never \| any` |

Everything runs inside the runner — the action hits your service on `localhost`,
never calls back to Bowire infrastructure, and no secrets leave the job.

## Minimal usage

The action captures the **head** snapshot itself from the running service; the
**base** snapshot is captured on the base branch and handed over so the diff has
something to compare against. The canonical shape is a two-step job:

```yaml
name: Bowire PR report
on: pull_request

permissions:
  contents: read
  pull-requests: write   # for the comment upsert

jobs:
  bowire:
    runs-on: ubuntu-latest
    steps:
      # 1. Capture the base snapshot: check out the base branch, start the
      #    service, and snapshot its API surface.
      - uses: actions/checkout@v7
        with:
          ref: ${{ github.event.pull_request.base.sha }}
      - name: Start the base service
        run: ./scripts/start-service.sh &        # your service on :8080
      - name: Capture base snapshot
        run: |
          dotnet tool install -g Kuestenlogik.Bowire.Tool
          bowire diff snapshot http://localhost:8080 -o base.snapshot.json

      # 2. Check out the head, start the service, run the report.
      - uses: actions/checkout@v7
      - name: Start the head service
        run: ./scripts/start-service.sh &
      - uses: Kuestenlogik/Bowire/.github/actions/bowire-pr@main
        with:
          target: http://localhost:8080
          base-snapshot: base.snapshot.json
          fail-on-schema: breaking     # a removed method / signature change fails the check
```

Drop `base-snapshot` and the API-schema section is simply skipped — the Tests
and Security sections still run against `target`.

## Inputs

| Input | Default | Notes |
| --- | --- | --- |
| `target` | — (required) | URL of the running head service. |
| `protocol` | *(guessed)* | Discovery plugin id (`rest`, `grpc`, `graphql`, …). |
| `base-snapshot` | *(none)* | Base-branch schema snapshot; empty skips the API section. |
| `test` | *(none)* | Recording / flow JSON for `bowire test`; empty skips Tests. |
| `scan` | `true` | Run `bowire scan` against the target. |
| `scan-templates` | *(none)* | Vulnerability-template directory. |
| `scan-baseline` | *(none)* | Base scan SARIF, for new/fixed findings. |
| `fail-on-schema` | `none` | `none` \| `breaking` \| `any`. |
| `fail-on-tests` | `any` | `any` \| `never`. |
| `fail-on-scan` | `never` | `never` \| `any`. |
| `tool-version` | *(latest)* | Pin a specific `Kuestenlogik.Bowire.Tool`. |

When the workflow is not a `pull_request` (e.g. `workflow_dispatch`), the report
is written to the run summary instead of posted — handy for a dry run.

## Related

- [`bowire diff`](standalone.md) — the schema-delta command the API section uses.
- [Security scanning](../features/scan.md) — the `bowire scan` lane.
- [CI setup](ci.md) — running `bowire test` as a CI gate.
