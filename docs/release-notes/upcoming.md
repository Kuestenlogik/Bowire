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

On a 2025-11-25 server, a task's questions are answered as well (#821). That shape sends them as `elicitation/create` on a pending `tasks/result`. Bowire receives them on a second connection that declares elicitation, shows them on the task card, and `Tasks/update` answers them.

### Import from Insomnia, Bruno and .http, export as .http (#673, #674)

Bowire reads the collections of other API clients:
- Insomnia exports, v4 (JSON or YAML) and v5 (YAML);
- Bruno collection folders, in `.bru` files or OpenCollection YAML, and bundled OpenCollection files;
- `.http` files with their `http-client.env.json`.

Requests, folders, environments and the auth that fits a header come across. A report lists what was left out with the reason, what changed shape, and which credentials sit in clear text. In the other direction, a collection exports as a `.http` file plus `http-client.env.json` for Rider, IntelliJ, Visual Studio and VS Code, and the Code tab copies one request as `.http`. Secrets are never written.

All of it runs through a new Interop gRPC service (also JSON at `/api/v1/interop/…`), shared by the workbench, `bowire import` / `bowire export http` and the MCP tools `bowire.import` / `bowire.export.http`. Migration guides per source are under *Migrating* in the docs.

### JSONPath filters in assertions, chaining and mock overrides (#676)

A path can now be JSONPath from `$`: filters (`$.items[?(@.done == true)].id`), wildcards, `..` descent, slices, unions and negative indices. The short form (`response.items.0.id`) stays as it was. One resolver serves:
- assertions in the Tests tab and in `bowire test`;
- flow expectations;
- response chaining;
- mock field overrides and body predicates.

Its browser and CLI implementations are held to one shared test fixture. A path that selects several values gives an array, and a malformed path fails with the position of the first character that does not fit.

### Data-driven runs: `bowire test --data` and the workbench (#675)

`bowire test suite.json --data rows.csv` runs the collection once per row of a CSV or JSON file, with the row's columns as variables over the environment. `{{iteration}}` and `{{iterationCount}}` are available. Every failure names its iteration and source row, on the console and in the HTML, JUnit and SARIF output. JUnit gets one suite per iteration and one case per assertion. `--fail-fast` stops at the first failing iteration. A malformed data file fails before the first request, naming the line. In the workbench, **Data file…** attaches the same kind of file to a collection; **Run All** runs it once per row and lists PASS / FAIL per row with its source line. The file is read by a new DataSets gRPC service (`POST /api/v1/datasets/parse`), so every surface parses it the same way.

### `bowire.test.run`: test runs for agents, as MCP tasks (#802, #945)

Agents can run a test collection or flow through Bowire's MCP server, as `bowire test` does, with an optional data set. A client that declares the MCP tasks extension gets a durable task: the status names the current step, `tasks/cancel` stops the run before its next step, and the result is kept for 24 hours under `~/.bowire/mcp-tasks/`, so a reconnecting agent gets it with the same task id. Clients without the extension get the synchronous answer, as before. For task-capable clients, the confirmation of `bowire.mock.start` and `bowire.record.start` becomes an elicitation inside the task (`input_required`, answered through `tasks/update`) instead of a second call with a token.

## Breaking changes

<!-- Each change has been on a back-compat ramp through the prior minor
and is removed in this release. Add a section per breaking change, with
the migration path. -->

### A JSONPath step is `[0]`, not `.0` (#676)

A path that starts with `$` is now JSONPath, and JSONPath has no `.0` step. Flow expectations used to accept `$.tags.0`. It is now refused with `Invalid JSONPath at position 7: a number after '.' is not a name; write [0], or use the short form without '$'`, rather than read as a property named `0` that matches nothing on an array. Write `$.tags[0]`, or the short form `tags.0`, which is unchanged.

### `exists` / `notexists` tell a JSON `null` from a missing field (#676)

`exists` used to fail on a field whose value is `null`, and `notexists` passed on it. Both now ask whether the path selects anything: `{"deletedAt": null}` makes `deletedAt exists` pass and `deletedAt notexists` fail. Only a missing field is missing. This holds in the Tests tab, in `bowire test` and in flow expectations. To assert that a value is null, use `eq null`. To assert "absent or null", use two assertions, or `type null` where the field is always there.

### Keyring references with a URL service split after the host (#870)

A `{{keyring.*}}` reference that starts with a URL scheme (`https://…`) is now split on the last `/` after the host instead of the first `/`: `{{keyring.https://api.example.com/v1/deploy-bot}}` is service `https://api.example.com/v1`, account `deploy-bot`. Only URL-shaped references are affected; they parsed to service `https:` before and did not resolve. Plain references (`github.com/deploy-bot`) split on the first `/` as before. Where the default split is not the one you need, write a `/` that must not split as `%2F` (and a literal `%2F` as `%252F`): `{{keyring.https://api.example.com/v1/team%2Fbot}}` is account `team/bot`.

### MCP tools read only the store layout of `recordings.json`, `collections.json` and `flows.json` (#882, #887)

`bowire.record.list`, `bowire.record.stop` and the `bowire://recordings`, `/recordings/{id}`, `/collections/{id}` and `/flows/{id}` resources accepted a file holding a bare JSON array. No Bowire store writes that shape — they all write `{"recordings": […]}` (and `collections` / `flows` likewise) — and the mock's recording loader already refused it. The MCP readers now take exactly the layout the stores write; `bowire.record.stop` refuses to close a session onto a file of any other shape instead of overwriting it, so the captured steps are kept. A hand-written bare array: wrap it as `{"recordings": [ … ]}`.

## Acknowledgements

<!-- Optional. Names of contributors who exercised rc / reported. -->
