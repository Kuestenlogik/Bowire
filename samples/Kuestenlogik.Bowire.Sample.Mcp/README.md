# Kuestenlogik.Bowire.Sample.Mcp

A Model Context Protocol server over the official C# SDK's
streamable-HTTP transport, demonstrating **both** ways Bowire meets an
MCP service, from one project:

- **Embedded** — the workbench is mounted at `/bowire`, and the bundled
  `mcp-catalogue.json` seeds the Sources rail with this host's `/mcp`
  endpoint.
- **Separate** — it is a real MCP server, so point an external workbench
  or the CLI at it.

## What the server exposes

It covers all three surfaces the Bowire MCP plugin lists and invokes, so
the sidebar shows one service per category:

| Service | Entry | Notes |
| --- | --- | --- |
| **Tools** | `echo` | Echoes the input text. |
| | `add` | Adds two integers. |
| | `record_readings` | The non-trivial one — see below. |
| **Resources** | `bowire://sample/sensors` | Direct resource; the sensor ids the sample knows about. |
| | `bowire://sample/sensors/{sensorId}/readings` | Templated resource; the last five readings for one sensor. |
| **Prompts** | `summarise_sensor` | One required argument (`sensorId`) and one optional (`tone`). |

`record_readings` exists to exercise Bowire's schema → form mapping with
something harder than two text boxes: its `inputSchema` has a **required
array of nested objects** (`readings`, each with a described `sensorId`,
`value` and optional `at`), a **required nested object** (`source`), and
two optional scalars — every property carrying a description.

Everything is stateless: the tool answers from the batch it is handed and
the resources derive their readings from the sensor id, so there is
nothing to reset between runs.

Two details worth knowing if you copy this sample:

- Optional parameters take a **non-null default** (`bool
  dropOutOfRange = true`) rather than a nullable type. For a nullable
  parameter the SDK emits `"type": ["object", "null"]`, and a JSON-Schema
  type *union* is not something every schema reader copes with — Bowire's
  tool mapper reads `type` as a plain string and fails the discovery pass
  on one. Nested properties are free to be nullable; only the top level
  is walked.
- The templated resource is advertised on `resources/templates/list`,
  not `resources/list`, so Bowire's Resources service lists the direct
  resource only. Read the templated one by expanding it, e.g.
  `bowire://sample/sensors/dock-1/readings`.

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.Mcp
```

- Embedded workbench: <http://localhost:5190/bowire> — the sample's
  tools, resources and prompts are already in the Sources rail.
- As a separate target:

  ```pwsh
  bowire --url mcp@http://localhost:5190/mcp
  ```
