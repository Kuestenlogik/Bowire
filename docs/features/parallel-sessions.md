---
title: Parallel sessions
summary: 'Run a collection or recording as many concurrent sessions — on this host, or spread across several Bowire hosts — and keep the result in the Benchmarks pane. Executors take a target allowlist and a token; the coordinator can refuse executors in clear text; both write a chained audit log.'
---

# Parallel sessions

From a collection or a recording, the **Parallel sessions** button starts it as several concurrent sessions. Each session walks its share of the targets — session *k* takes the targets whose index modulo the session count is *k* — optionally ramped up, optionally stopping everyone at the first failure, optionally cycling through an environment pool.

With **hosts** filled in, the run is **distributed**: this Bowire becomes the *coordinator*, splits the sessions evenly across the hosts, and sends each host's share to its `POST /api/parallel/start-local`. Those hosts are the *executors*; the coordinator merges what they return into one result.

The result panel shows the sessions, the latency distribution and, for a distributed run, one line per host. **Save to Benchmarks** keeps it: the first save of a source creates a benchmark entry, every later run of the same source adds to that entry's history, so the Benchmarks pane diffs it against the run before. A saved distributed run keeps its hosts and what each of them did — including an executor the coordinator refused.

## Running an executor safely

An executor has to be reachable by its coordinator, so unlike a laptop Bowire it listens beyond `localhost` — and then `/api/parallel/start-local` would load-test whatever any caller names. Configure an executor with at least the first two of these:

| Setting | Environment variable | Effect |
|---|---|---|
| `Bowire:Parallel:TargetAllowlist` | `BOWIRE_PARALLEL_ALLOWLIST` | URL patterns the executor will send to. `*` stands for any run of characters and a pattern matches the whole target URL, case-insensitively: `https://api.staging.example/*`. A list, or one string separated by `,` or `;`. A job naming any target outside it is refused with **403** before a single request goes out. Unset, every target is accepted. |
| `Bowire:Parallel:Token` | `BOWIRE_PARALLEL_TOKEN` | On an executor: required as `Authorization: Bearer <token>`, otherwise **401** — checked before the job is even read. On a coordinator: the token it sends (a token in the run request takes precedence). |
| `Bowire:Parallel:RequireSignedExecutor` | `BOWIRE_PARALLEL_REQUIRE_SIGNED_EXECUTOR` | On a coordinator: refuse any executor that is not loopback and not `https`, before anything is sent — the token and the results never cross a network in clear. The refused host shows in the result as `refused: …`. |

```json
{
  "Bowire": {
    "Parallel": {
      "TargetAllowlist": [ "https://api.staging.example/*", "https://auth.staging.example/token" ],
      "Token": "…"
    }
  }
}
```

A coordinator that runs without hosts executes the targets itself, so its own allowlist applies to them as well.

Certificate checks on the way to an executor are the normal ones. Bowire's relaxed validation for self-signed development certificates (`Bowire:TrustLocalhostCert`) only ever applies to `localhost`, `127.0.0.1` and `::1`, never to an executor elsewhere.

## Audit log

Coordinator and executors append to `audit/parallel.jsonl` under the data directory, one JSON line per event:

| `kind` | Written by | When |
|---|---|---|
| `dispatch` | coordinator | once per run: targets, sessions, and each host's outcome |
| `run` | executor | once per job it ran: targets, sessions, pass / fail, duration |
| `refused` | executor (or a coordinator running locally) | once per target outside the allowlist |
| `unauthorized` | executor | a call without the right token |

The coordinator sends each executor the run's id in `X-Bowire-Parallel-Job`, and both sides record it as `job`, so a `dispatch` line and its executors' `run` lines join up. Every line carries `prevHash`, the SHA-256 of the line before it (64 zeros on the first): removing or editing a line breaks the chain at the next one. A chain cannot show lines cut off the end — keep the file where the process that writes it cannot also delete it if that matters.

## Executors from a hub

On a Bowire that is an [Agent hub](agent-hub.md), the dialog lists the live agents tagged `parallel-executor` under the Hosts field; a click adds one.

## Not yet

Mid-run failover to another executor and clock synchronisation across hosts are out of scope.
