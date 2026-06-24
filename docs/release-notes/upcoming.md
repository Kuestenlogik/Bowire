---
title: <fill in before the tag>
version: 2.1.0
---

<One-sentence frame for what 2.1 is about. Replaces this placeholder
the moment the first 2.1 work lands.>

## Highlights

<!-- Add a section per landed feature as the work merges. Pattern:
### <headline> (#issue)
<2-4 sentences>
-->

### MCP-over-MCP forwarder — `bowire mcp serve --attach` (#286)

A thin Bowire process can now relay every incoming MCP tool call to a heavier Bowire running on the operator's workstation. `bowire mcp serve --attach localhost:5198 --port 5199` boots a forwarder that surfaces no local tools — `tools/list`, `tools/call`, prompts, resources, and resource templates are all marshalled to the parent and the parent's response is relayed verbatim. Useful when an LLM agent on a CI runner / container should drive the workstation Bowire without sharing the parent's MCP socket directly. The parent gains a matching `--token <secret>` bearer-auth gate (`--bind http` only); the child passes the secret with `--attach-token <secret>`.

## Breaking changes

<!-- Add a section per breaking change, with the migration path. -->

## Acknowledgements

<!-- Optional. Names of contributors who exercised rc / reported. -->
