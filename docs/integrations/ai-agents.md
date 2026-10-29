---
title: AI agents
summary: 'Let an MCP agent (Claude Desktop, Cursor and others) drive Bowire: discover and call services, record and mock them, and run test suites as long-running tasks.'
---

# AI agents

Bowire is an MCP server for its own work. An agent connected to it can discover a service, call it, record the traffic, start a mock from the recording, and run a test collection, through the same operations the workbench and the CLI use. This page is the agent's side; [MCP: Bowire as an MCP server](../protocols/mcp.md#bowire-as-an-mcp-server--controlling-bowire-from-an-ai-agent) has the full tool and resource list.

## Connect an agent

For an agent that starts its tools as processes (Claude Desktop's `command` entries, Cursor's stdio mode), run the CLI over stdio:

```json
{
  "mcpServers": {
    "bowire": {
      "command": "bowire",
      "args": ["mcp", "serve", "--allow-arbitrary-urls"]
    }
  }
}
```

`--allow-arbitrary-urls` lets the agent call any URL it names. Leave it off to limit calls to the URLs of your environments. For an agent that connects over the network, `bowire mcp serve --bind http --token "$MCP_SECRET"` serves the same tools at `POST /mcp`. Put it behind TLS whenever it leaves the machine ([details](../protocols/mcp.md#standing-the-same-server-up-over-http---bind-http)).

## What an agent can do

- **Discover and call:** `bowire.discover` lists a service's methods with their schemas, and `bowire.invoke` / `bowire.subscribe` call them.
- **Record and mock:** `bowire.record.start` / `stop` capture traffic, and `bowire.mock.start` serves a recording or a schema as a local API.
- **Test:** `bowire.test.run` runs a test collection or flow, as `bowire test` does, optionally once per row of a CSV or JSON data set.
- **Move collections:** `bowire.import` reads Insomnia, Bruno and `.http` collections; `bowire.export.http` writes `.http` files.

## Long runs: MCP tasks

A test run takes minutes, longer than an agent host or a proxy keeps a call open. Bowire's server supports the MCP tasks extension (`io.modelcontextprotocol/tasks`). An agent whose client declares it on the call gets a task handle at once:

1. `bowire.test.run` answers with a task id and keeps running in the server.
2. The task's status message names the current step, for example `step 3/12 · Get user (iteration 2/5)`. The client reads it with `tasks/get` or receives it through `notifications/tasks`.
3. When the run ends, `tasks/get` returns the result: `{ exitCode, passed, output, errors }`, with every failure naming its test and, in a data-driven run, its row.
4. `tasks/cancel` stops the run before its next step.

The task is kept for 24 hours in `~/.bowire/mcp-tasks/`. An agent that disconnects or restarts gets the result with the same task id. A task that was still running when the server stopped is reported as failed, with that reason.

A client without the extension calls `bowire.test.run` as an ordinary tool and waits for the answer.

## Confirmations

Starting a mock or a recording changes something on your machine, so `bowire.mock.start` and `bowire.record.start` ask first. Set `RequireConfirmationForMutations`, or pass `--no-confirm` to `bowire mcp serve`, to turn the question off.

- **With tasks**, the tool runs as a task and asks through an elicitation: the task turns `input_required` with a yes/no question that names what it is about to do. The agent's client puts the question to you and answers with `tasks/update`. The tool then goes ahead, or ends with `{ "confirmed": false }` without starting anything.
- **Without tasks**, the first call returns `{ pending: true, confirmationToken, plan }`. The agent shows the plan and calls again with the token, or with `confirm: true`, once you agree. A token confirms only the plan it was issued for.
