---
# The release's `#` heading. A sentence about this delivery, not the version.
title: A Bowire that follows long-running MCP tools, takes in other clients' collections, and has a logo of its own
version: 2.10.0
---

2.10 is the release for moving work into Bowire and for the tools agents run for minutes, not milliseconds. Bowire follows MCP tasks from call to result, imports Insomnia, Bruno and `.http` collections, runs a collection once per data row, filters with JSONPath, and sends a one-off HTTP request from the command line. Along the way a sweep through every protocol plugin fixed about a hundred defects — one of them sent the mTLS private key to the target as a header — and Bowire got its new logo, a blog and a lighter site.

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

A recorded MCP task call keeps its whole lifecycle, including the answers given to its input requests (`input` frames). The mock server answers MCP on the recorded server's path, from `tools/list` and `tools/call` to `tasks/get`, `tasks/update` and `tasks/cancel`. It replays a recorded task in its recorded order and pace, and waits at `input_required` for the client's answer. An MCP client can thus be tested against tasks without the real server (#803).

A flow or `bowire test` checks a server's task lifecycle in one step with `Tasks/run` (#803). It calls the tool as a task, answers its input requests with given responses, cancels at a given status if asked, and waits for the end or a timeout. It then reports every status the task went through, the keys it answered, and its result or error. The flow's expectations assert on these.

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

### `bowire send http`: one request, URL first, in HTTPie's notation (#678)

`bowire send http post api.example.com/pets name=ada age:=3` sends `{"name":"ada","age":3}` as JSON, with no discovery and no schema. Items are `Header:value`, `param==value`, `field=value`, `field:=json` and `field@file` (a multipart upload). The method is optional, any token works, and `:3000/x` means localhost. `{{variables}}`, `--env-file`, `--compact`, the proxy and CA settings and `--negotiate` / `--ntlm` / `--digest` work as on `call`. `--offline` prints the request with every variable resolved and opens no connection. `-i` shows the response headers, and `-o <file>` saves the body as the server sent it, so a download works too (#975). `send` loads no protocol plugin: against one local endpoint it answers in about 270 ms where `call` takes about 650 ms.

### A new Bowire logo, generated in every format (#964, #965)

Bowire has a new mark, the "bw", in the frame and the navy ink the Küstenlogik products share, and an emblem: the mark in its ring with a bowline knot (Bowire is *bowline* and *wire*). Every logo file now comes from those two drawings through the shared Küstenlogik branding tool:
- marks, emblem and lockups in colored, black, white and theme-following SVG, plus PNG and WebP;
- the favicons and the touch icon;
- the package, Windows, macOS and Linux icons;
- the link preview;
- the brand kit;
- the inline copies on the site, in the docs and in the workbench.

So the site, the docs, the workbench, the NuGet packages, the installers and the VS Code extension all show the same logo. The [brand page](https://bowire.io/brand.html) and the brand guide describe the forms and colours.

## Fixes along the way

### A security fix: REST ad-hoc requests sent internal markers as headers (#899)

A request sent from the freeform REST builder carried Bowire's internal `__bowire*` metadata to the target as HTTP headers — the mTLS marker among them, which holds the client's private key. Every internal marker is now stripped on that path too, as on every other. If you sent ad-hoc REST requests with mTLS configured to a server you do not control, rotate that client certificate.

### A sweep through every protocol plugin (#816–#924)

Each plugin was read against its protocol's specification, and what did not match was fixed:
- **gRPC:** packed repeated scalars, `sint32`/`sint64` zigzag, the fixed-width types and map fields encode and decode correctly, and `int64` values given as JSON strings are accepted (#889–#891, #894, #895). Connect keeps the base path and no longer hangs on an early end of stream (#897, #898).
- **REST and OpenAPI:** self-referencing schemas, `application/json; charset=utf-8` keys and duplicate operation keys no longer break discovery, a DELETE keeps its body, a 3xx is OK, and SigV4 signs repeated query parameters as AWS expects (#828, #844, #845, #900, #904–#907). The OpenAPI export and the sample generator write the types the schema has (#859, #861, #902, #903).
- **GraphQL, AsyncAPI, SignalR, SSE, WebSocket, MQTT, NATS, Socket.IO, JSON-RPC, OData, Pulsar:** cycles, wrong nullability, envelopes and payload decoding, channel URLs, stream ends and settings that were read nowhere (#826, #827, #843, #846–#858, #862–#867, #888, #893, #909–#924).
- **MCP server tools:** the URL allowlist matches hosts and ports, not text prefixes; a confirmation token is bound to its tool and plan; and the recording tools read and keep the stores as they are written (#877–#887, #892, #896, #908).
- **SCIM, OIDC, Nuclei, OAST, Scaffold, keyring, workspaces, monitoring, contracts, telemetry and the CLI:** among them a lock file that never recovered, a monitoring window across midnight, and contract results of different pairs that overwrote each other (#816–#818, #829–#842, #848, #849, #860, #868–#876, #881).

### Intercept shows what the reverse proxy captured (#978, #979)

A reverse proxy started from the workbench forwarded requests but recorded them where the Captured tab never looked, and the detail pane showed every header as `undefined:`. Both work now.

### German: technical terms the way developers say them (#980, #983, #984)

The German interface had translated technical terms word by word — "Stellvertreter" for proxy, "Attrappenwirt" for mock server, "Beiwagen" for sidecar. It now says Proxy, Mock-Server, Sidecar, Host, Port, Header and Token, following a glossary in the translation guide that a test holds. More than a hundred sentences that stayed English in every language are translated, and the check that finds them sees the shapes it missed.

## The site

### A blog, a Resources menu, and a phone menu that works (#926–#930)

bowire.io has a blog, with a feed and a canonical home for posts that are cross-posted. Documentation, Bootcamp, Blog and Community sit under **Resources**, and the site, the docs, the API reference and the Bootcamp have a phone menu that stays readable.

### A home page of 410 KiB instead of 5.2 MB (#969)

Every image on the site is served as AVIF or WebP at the size it is shown, only the screenshot of the current theme is loaded, and the demo video loads when it comes into view. The Lighthouse audit passes.

## Breaking changes

<!-- Each change has been on a back-compat ramp through the prior minor
and is removed in this release. Add a section per breaking change, with
the migration path. -->

### Response files are `@file.rsp` only (#974, #976)

The parser used to expand every `@file` token into the file's arguments. That had broken the documented `bowire call … -d @request.json`, whose body reached the command split on whitespace with its quotes stripped. Now only `@<path>.rsp` is a response file: one argument per line, as it stands, with `#` comments. It is expanded before anything reads the command line, so network and plugin options in it take effect. Every other `@file` reaches the command, which reads the file. A response file with another extension: rename it to `.rsp`. One that relied on several arguments per line: put each on its own line.

### Logo files have new names (#964)

The files in `images/` carry the product prefix and say what they are: `bowire_mark_colored.svg`, `bowire_lockup_h_white.png`, `bowire_emblem_adaptive.svg`, …; `icon.png` is the package icon. `bowire_logo.svg`, `bowire_logo_small.svg` and their PNG/WebP exports, `og-image.svg` and the brand files `bowire-logo-*`, `bowire-lockup-*` and `bowire-mark-*` are gone. A link to one of those files on GitHub or on the site needs the new name.

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
