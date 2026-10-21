---
# The release's `#` heading. A sentence about this delivery, not the version.
title: <fill in before the tag>
version: 2.10.0
---

<One-sentence frame for what 2.10 is about. Replaces this placeholder
before the tag.>

## Highlights

<!-- Add a section per landed feature as the work merges. Pattern —
note the example heading is indented so the placeholder detector in
release.yml (which counts lines matching `^### `) does not mistake this
template for a filled-in body:

  ### <headline> (#issue)
  <2-4 sentences>
-->

### MCP tasks: call, follow, answer and cancel long-running tools (#801)

When an MCP server answers a tool call with a durable task, Bowire now follows it: the task's status streams in as it changes, then its result or error. Bowire speaks the `io.modelcontextprotocol/tasks` extension (2026-07-28) and the older 2025-11-25 shape, polls at the interval the server asks for, and switches to pushed status where the server agrees to send it. A new **Tasks** service answers a task's input requests (`update`), cancels it (`cancel`, cooperative), reads it (`get`) and resumes following it after a reload (`follow`) — from the workbench, the CLI and REST alike. Servers without tasks behave exactly as before.

## Breaking changes

<!-- Each change has been on a back-compat ramp through the prior minor
and is removed in this release. Add a section per breaking change, with
the migration path. -->

### Keyring references with a URL service split after the host (#870)

A `{{keyring.*}}` reference that starts with a URL scheme (`https://…`) is now split on the last `/` after the host instead of the first `/`: `{{keyring.https://api.example.com/v1/deploy-bot}}` is service `https://api.example.com/v1`, account `deploy-bot`. Only URL-shaped references are affected; they parsed to service `https:` before and did not resolve. Plain references (`github.com/deploy-bot`) split on the first `/` as before. Where the default split is not the one you need, write a `/` that must not split as `%2F` (and a literal `%2F` as `%252F`): `{{keyring.https://api.example.com/v1/team%2Fbot}}` is account `team/bot`.

### MCP tools read only the store layout of `recordings.json`, `collections.json` and `flows.json` (#882, #887)

`bowire.record.list`, `bowire.record.stop` and the `bowire://recordings`, `/recordings/{id}`, `/collections/{id}` and `/flows/{id}` resources accepted a file holding a bare JSON array. No Bowire store writes that shape — they all write `{"recordings": […]}` (and `collections` / `flows` likewise) — and the mock's recording loader already refused it. The MCP readers now take exactly the layout the stores write; `bowire.record.stop` refuses to close a session onto a file of any other shape instead of overwriting it, so the captured steps are kept. A hand-written bare array: wrap it as `{"recordings": [ … ]}`.

## Acknowledgements

<!-- Optional. Names of contributors who exercised rc / reported. -->
