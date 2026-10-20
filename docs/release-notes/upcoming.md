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

## Acknowledgements

<!-- Optional. Names of contributors who exercised rc / reported. -->
