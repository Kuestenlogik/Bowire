---
title: <fill in before the tag>
version: 2.3.0
---

<One-sentence frame for what 2.3 is about. Replaces this placeholder
the moment the first 2.3 work lands.>

## Highlights

<!-- Add a section per landed feature as the work merges. Pattern:
### <headline> (#issue)
<2-4 sentences>
-->

### Discovery tells you what it actually tried (#534)

When discovery came back empty, Bowire said "HTTP 502 Bad Gateway" and
stopped there — the per-plugin detail sat in an `application/problem+json`
body the UI never read, and that body only listed the plugins which
*threw*, so a plugin that ran cleanly and simply didn't recognise the URL
was invisible. `/bowire/api/services` now reports one structured attempt per
probed plugin (`ok` / `empty` / `error` / `timeout`, with the probe duration
and a one-line message), and the workbench renders it as a disclosure:
collapsed to `12 plugins probed · 3 failed` on the discovery-failed card,
the per-URL status rows and the topbar connection popover; always expanded
under **Discovery diagnostics** in the Sources detail pane, with a **Copy
diagnostics** button for bug reports.

The same fan-out now backs a new `bowire discover --url <url>` command —
which always prints the full attempt table and exits `1` when nothing was
found, so CI can gate on it — and the `bowire.discover` MCP tool, which
previously swallowed every per-plugin exception into a debug log and handed
the agent an unexplained empty list. One `BowireDiscoveryProbe` behind all
three means the UI, the terminal and an agent can no longer disagree about
what happened.

Also fixed: an embedded host whose own `/api/services` probe failed used to
fall through to the first-run welcome hero, hiding the failure completely.
It now lands on the discovery-failed card like any other target.

### A half-broken server contributes what still works (#544)

`IBowireProtocol.DiscoverAsync` is all-or-nothing: a plugin returns a list
or it throws. A plugin whose probe half-worked therefore had to hide either
the fault or the results — and the MCP plugin picked the fault, so an MCP
server with a single malformed tool stopped contributing its perfectly good
resources and prompts as well.

Plugins can now implement the optional `IBowireDiscoveryDiagnostics`
alongside `IBowireProtocol` and hand back both: the services they found and
a `BowireDiscoveryDiagnostic` describing what broke while they found them.
`BowireDiscoveryProbe` pairs the diagnostic's severity with the number of
services returned and records a new `partial` outcome — its own state, so a
dashboard can tell "populated but incomplete" from a clean `ok`. A plugin
that does not implement the interface behaves exactly as before, and
`IBowireProtocol` itself is unchanged, so third-party plugins keep compiling.

Two plugins use it today. **MCP** returns the surfaces that answered plus a
fault naming the one that did not, so the malformed-tool server keeps its
resources and prompts. **REST** finally says out loud what it always knew:
"no OpenAPI document found at `http://localhost:5181`" — with every
well-known path the sweep tried behind it — instead of the generic "returned
no services", and it names a *missing optional package* as such rather than
letting `bowire plugin install …Rest.OpenApi3` look like "your URL is not a
REST API".

The workbench counts degraded plugins separately from failed ones
(`12 plugins probed · 1 degraded · 0 failed`), marks the source in the
Sources rail, labels it *Connected — discovery incomplete* in the Sources
detail pane, and shows the diagnostics disclosure on the Discover landing
even when discovery succeeded. `bowire discover` gets a `· N partial` term
and a trailer sentence; its exit code is unchanged, so an existing CI gate
does not start failing.

### An embedded Bowire is useful on first paint (#535)

Mounting `MapBowire()` in your own app used to drop you on an empty
Home: no workspace, a "Create your first workspace" card, and the
Continue / Favorites / Recent bands all blank — even though the host's
own API had already been discovered in-process and was sitting one rail
away. The first thing every embedded user did was dismiss a dialog about
a concept they did not need yet.

An embedded first run now seeds a single workspace named after the host
app (`Title` if you set one, else the entry assembly, else the request
origin) and lands on **Discover**, with your services already listed.
Nothing else changes — the topbar workspace chip and the Workspaces rail
still switch / create / rename / delete, and `options.AutoCreate
InitialWorkspace = false` restores the old empty-Home behaviour.

Standalone is untouched: `bowire` still opens on Home with the
Create-Workspace CTA. The `--auto-create-initial-workspace` flag that
the docs have referenced since 2.0 now actually exists and opts a
standalone install in (also bindable as
`Bowire:AutoCreateInitialWorkspace` / `BOWIRE_Bowire__AutoCreate
InitialWorkspace`).

One behaviour change worth knowing about in both modes: the seed now
gates on the `bowire_workspaces` key never having been written, not on
the list merely being empty. Deleting the last workspace on purpose used
to bring it back on the next reload; now it stays deleted.

### A response you can do something with (#536)

Every follow-up the workbench offers for a response already existed as a
feature — mocks, flows, test assertions, benchmark envelopes — but none
of them had a path *from* the response you were looking at. You read a
result, then walked back to the method header, or the Recordings rail,
or retyped the request in another surface.

Both response surfaces (the Discover response pane and the Compose
request builder's response viewer) now carry a single **Use this…**
button that appears once a call has succeeded. It opens one menu with
four handoffs: **Save as mock** (freezes request + response into a
recording step and boots a mock host when
`Kuestenlogik.Bowire.Mock` is installed), **Add to flow…** (appends the
request as a step in a new or existing flow, with a status assertion),
**Keep as test** (saves status + body as assertions and jumps to the
Test results tab), and **Add to benchmark envelope…**.

Handoffs whose package is absent stay visible and disabled with a
tooltip naming the package, so a `Bundle.Minimal` host learns what it is
missing instead of seeing a silently shorter menu.

Two long-standing rough edges went with it. The freeform pane's **Save
as Mock Step** button used to report success even when `startRecording`
had refused to start (no active workspace) — capture and the success
toast now share one helper that re-checks. And the execute split-button's
**Run as benchmark…** item, which used to synthesise a click on an
unrelated header menu, now opens the envelope picker directly.

### The service catalogue is something you can actually browse (#537)

The catalogue seam has been fully built server-side since #136 —
providers for a local file, an HTTP document, Consul, Kubernetes and an
agent hub, all behind `AddBowireCatalogue()` — and it was invisible.
The workbench fetched the entries and never rendered them; the standalone
`bowire` tool never registered the seam at all, so `/api/catalogue/info`
always answered `available: false` in CLI mode; and there was no way to
inspect a catalogue without starting a browser.

A catalogue is now the primary "add a source" affordance wherever there
is one. The Sources node's `+` button and context menu open a picker with
search, tag chips and per-row **Add**; the Sources detail pane grows a
**Catalogue** section above `+ Add URL`; and first-run Discover and the
Home hero lead with **Browse catalogue (N)**. With no catalogue
configured — still the default — every one of those surfaces is byte-for-byte
what it was, and manual URL entry stays one click away from the picker
even when a catalogue is present.

Three fixes make that worth using:

- **Protocol hints are composed.** An entry declaring
  `"protocols": ["graphql"]` is now discovered as
  `graphql@http://host/graphql`, not as a bare host that finds nothing.
  CI's smoke job had been doing this composition for its own probes,
  which is why the gap never showed up there.
- **The catalogue loads before the first discovery run**, instead of in
  parallel with it. Merged entries used to appear in the Sources rail
  already marked `Disconnected · 0 svcs`, because the fan-out had gone
  out over the list as it was *before* they arrived.
- **Merged rows no longer leak into browser storage.** A provider row
  belongs to the provider and disappears when the entry does; clicking
  **Add** adopts it into the workspace, and that is what makes it stick.

New CLI surface: `bowire catalogue list | providers | use | clear`, all
in-process (no running server), plus `--catalogue-provider`,
`--catalogue-path`, `--catalogue-url` and `--catalogue-consul` on the
root command. `list` prints the composed URLs, so the terminal and the
workbench can't disagree about what will be probed.

`GET /api/catalogue/info` gained a `providers` array naming the provider
implementations actually loaded in the host, so Settings → Discovery
providers greys out `kubernetes` / `agent` with the package to install
instead of offering a row that fails at save time. It also gained an
`error` string: a provider id that doesn't resolve now degrades to a
`200` explaining itself rather than a `500` that leaves the workbench
with no catalogue and no reason.

Two adjacent bugs went with it. The persisted
`~/.bowire/catalogue-config.json` override is now applied on the first
`/info` or `/entries` request — it previously stayed dormant until
someone opened the Settings tab, so a restarted workbench reported "no
catalogue" to an operator who had configured one. And in the standalone
host, a bare boolean flag swallowed the following token in the
switch-mapped command-line source, which made `bowire --no-browser
--catalogue-provider local` (and `--oast-server` before it)
order-dependent.

### `bowire call` speaks every protocol, and the workbench can hand you the line (#538)

`bowire call` was a grpcurl clone. It reached `GrpcReflectionClient` and
`GrpcInvoker` directly, which meant that every REST, GraphQL, SSE,
WebSocket, SignalR, MQTT, NATS or Socket.IO request you could build in the
workbench had no terminal equivalent at all — a large hole in a tool whose
premise is that the UI and the CLI do the same things.

It now invokes through the protocol registry. Pin the plugin with the
`protocol@url` hint form the sidebar and `bowire discover` already accept,
or with a new `--protocol`:

```bash
bowire call --url rest@https://petstore3.swagger.io/api/v3/openapi.json \
  pet/getPetById -d '{"petId":1}'
```

Three more additions round it out: `--stream` follows a server-streaming,
SSE, WebSocket or broker subscription and prints one JSON document per
frame until Ctrl+C; `--var KEY=VALUE` / `--env-file` run the same
`{{name}}` / `${name}` resolver `bowire test` uses over the body, the URL
and every header; and the discovery half shares `BowireDiscoveryProbe`
with `/api/services` and `bowire discover`, so a URL that finds nothing
reports which plugin said what instead of a bare transport error.

gRPC keeps a fast path — no `--protocol`, no `--stream` means the original
code runs unchanged and never pays for the plugin assembly scan.

The workbench side is the point of all of it: the Code tab and the
response pane's **Copy ▾** dropdown now offer **Bowire CLI** on every
protocol, rendering the request you are looking at as a runnable
`bowire call …` line, with a bash/zsh ⇄ PowerShell toggle and a **Keep
{{variables}}** pill that emits `--var` pairs instead of baking values in.
`{{secret.*}}` and `{{keyring.*}}` references are deliberately left
unresolved — unlike the existing curl / fetch / Python generators, which
still put live credentials on the clipboard. A `#` note block names what
the CLI cannot reproduce (browser-side OAuth / session / JWT exchanges,
query-string API keys, duplex methods).

A golden fixture keeps that honest from both ends: a Node test pins what
the generator emits, and an xUnit test replays the same argv arrays
through the real System.CommandLine `call` command. Rename a flag and CI
fails rather than the copied line quietly ceasing to run.

Two smaller fixes ride along. MQTT, NATS and Socket.IO used to fall
through the code-export table's REST fallback and be offered a curl
command that could never reach a broker; they now offer the CLI export
instead. And `--url grpc@https://host` combined with `-plaintext` never
downgraded to `http://`, because the hint prefix was still on the string
when the check ran — the hint is now split off once, in one place, for
every subcommand.

### A recording of eight protocols now reads as one transaction (#539)

A recording that walked one business transaction across gRPC, OData,
REST, GraphQL, WebSocket, SignalR, SSE and MQTT looked, in the step
list, like eight unrelated rows. The Recordings detail pane gained a
second tab, **Correlated timeline**: one lane per protocol on a shared
time axis, one bar per step, per-frame ticks for streaming steps, and a
per-step verdict against a correlation key.

Bowire recordings carry no trace id — nothing in the `.bwr` format ever
had one — so the key is resolved in three tiers and the view says which
one it used: a correlation header on `step.metadata` (`traceparent`'s
trace-id, `x-correlation-id`, `x-request-id`, …), otherwise an
id-shaped JSON leaf shared by two or more steps, otherwise nothing, in
which case the lanes still render and the banner admits there is no
signal. Matches are **strong** (the key's own name *and* value are in
the payload — so `shipId` also ties `onShipId` and `OccupiedByShipId`)
or **weak** (the value turned up on some other id-shaped field). The
weak tier is not decoration: in a harbour capture `portCallId = 1`,
`craneId = 1` and dock `1` are three different things wearing the same
number, and a value-only match would fuse them.

The analysis lives once, in C#, and both surfaces call it — so the new
`bowire recording correlate <path> [--key name=value] [--json]` and the
browser cannot disagree about what correlates. The chosen key persists
onto the recording as an optional `correlation` field, which is
additive and does **not** bump `recordingFormatVersion`.

Two things ride along. **Import .bwr** now sits next to Import HAR: the
rail could import someone else's format but not its own, so a `.bwr`
from a colleague or from `bowire import har` was openable everywhere
except the workbench that writes it. And a recording whose `capturedAt`
stamps are all zero now falls back to cumulative durations rather than
collapsing every step onto the same offset.

### The timeline follows a transaction that renames its id (#545)

#539 keyed a recording on **one** value, so a transaction that changes
its identifier as it crosses services lit only the lanes that happened to
speak the chosen key. On the harbor recording, `shipId = 101` reached six
of eight lanes — GraphQL stayed dark not for want of a correlation key
but because it calls the same transaction `portCall.id = 1`.

The analyzer now walks a **second edge**. A step the key left unmatched
is joined to the transaction when it shares a distinctive id-shaped value
with a step the key *did* match. On the harbor recording that lights the
GraphQL lane through the container ids it shares with the REST step
(`id = MSCU1234567`), taking it to **seven of eight**:

| Protocol | Before | Now |
|---|---|---|
| `odata`, `rest`, `websocket`, `signalr`, `sse` | strong | strong |
| `grpc` | weak | weak |
| `graphql` | unmatched | **derived** — `id = MSCU1234567`, shared with `rest` |
| `mqtt` | unmatched | unmatched |

**Which shared values count is the whole problem, and the rule is
deliberately strict.** A bridge value must be id-shaped on *both* steps,
at least six characters long, and carried by a single family of field
names across the recording. That rejects `1` (one character, and it is
simultaneously a dock `Number`, a `Seq`, a `portCallId` and a `craneId`),
`true`, a repeated status string that doubles as a plain `status` label,
and a shared timestamp. The bar is higher than for the seed key on
purpose: a seed is corroborated by two steps agreeing on the field
*name*, a bridge gets no such corroboration, so the value has to carry
its own weight. A short id can still be promoted to the seed by hand.

**The eighth lane stays dark, and now says why.** The MQTT crane
telemetry shares exactly one value with the rest of the capture — the
number `1` — and joining on it would fuse four unrelated entities. The
run reports `mqtt (craneId = 1)` as a rejected bridge instead of leaving
the lane silently missing.

**Depth is two edges, fixed.** A step the key matched directly may bridge
one hop further; a step reached *through* a bridge never bridges onward.
An unbounded walk over an id-rich recording relates everything to
everything, which is worse than no join.

Every derived lane names the value that linked it — an unexplained match
is worse than no match, because an operator cannot tell a real
correlation from a coincidence. The timeline tab gains an always-visible
strip under the lanes (`graphql · Query / portCall · via id =
MSCU1234567 · shared with rest step 3`), a fourth legend swatch, and a
`linked via` row in the pinned inspect strip; derived bars render as
qualified evidence rather than as a full match. `bowire recording
correlate` gains a **VIA** column — an unjoined step reads `–` in both
`MATCH` and `VIA`, a directly matched step names its tier and shows `–`
for `VIA` — plus a `derived links` block naming the bridge and its source
step.

The persisted `correlation` field is **unchanged**: it still holds the
single seed key exactly as #539 wrote it. Derived edges are recomputed on
read, so there is no format change and no `recordingFormatVersion` bump.
`matchedStepCount` now counts derived steps too (7 on the harbor file,
where it was 6), with `derivedStepCount` / `derivedProtocolCount` keeping
the split visible.

One correction to a #539 note rides along. The feature doc said the
GraphQL lane contributes nothing because its id lives inside the query
string. That is only half right, and the wrong half sounded general: the
GraphQL **response** is ordinary JSON (`data.portCall.id`) and the
scanner has always walked it. What the sample query is missing is
`variables` — a query written as `{"query":"query($id:Int!){…}",
"variables":{"id":1}}` puts the id in a plain JSON leaf with no scanner
change at all. It is a property of that one query, not of GraphQL.

### MCP moves to SDK 2.0 and the 2026-07-28 revision

Bowire's three MCP surfaces — workbench-as-server, Bowire-as-client, and
the adapter that fronts other protocols — now run on ModelContextProtocol
2.0.0.

**Nothing to do on your side.** The revision made the wire busier while
leaving the calling code identical: clients probe `server/discover` before
falling back to the legacy `initialize` handshake, and the standardized
`MCP-Protocol-Version` / `Mcp-Method` / `Mcp-Name` headers are added by the
transport itself. There is no session id to carry any more — 2026-07-28
dropped `Mcp-Session-Id`, and per-request identity moved into `params._meta`,
which the SDK injects. If you point Bowire at an older server, the client
falls back automatically.

**What did change is the transport topology.** SDK 2.0 flipped
`HttpServerTransportOptions.Stateless` to `true`, so a mount that inherited
the default silently switched behaviour without a line of source changing.
Every `WithHttpTransport` call site in this repo — the sample, the adapter,
and `bowire mcp serve` — now states `Stateless = true` explicitly rather
than tracking an SDK default. On a stateless mount the standalone SSE `GET`
is gone and the server cannot send unsolicited requests; Bowire never used
either. Embedded hosts that mount MCP themselves should pin the flag too.
Do not pin it to `false` to recover the old shape: a stateful server answers
a 2026-07-28 request with `UnsupportedProtocolVersion` to force the client
back onto the legacy handshake, costing every modern client a wasted round
trip.

**One new failure mode is now visible instead of silent.** 2.0 requires
`inputSchema` when deserializing a tool, so a single malformed tool on a
third-party server throws for the whole `tools/list` page. Bowire used to
swallow that and render an empty Tools node — indistinguishable from "this
server has no tools". It now reports through the per-plugin discovery
diagnostics from #534, naming the surface and the payload complaint. The
first cut of this fix failed the whole probe on a diagnosable fault, so a
server whose tools were broken stopped contributing its working resources
and prompts too; #544 removed that trade-off in the same release. Such a
server now reports `partial` and keeps everything that still works.

### Plugin management stops being process-global (#546)

`bowire mock --plugin-dir X` never used X. The mock command rebuilt its
configuration from an **empty** argument list, so only `appsettings.json`
and `BOWIRE_PLUGIN_DIR` could reach it — the flag you typed was parsed,
accepted, and then structurally unreachable. Threading a plugin loader
through the CLI instead of resolving one per call fixes it.

That thread is the visible half of a larger cleanup. Plugin loading kept
its state in three static fields, including `s_loadedSubdirs` — a
hand-maintained record of what had been loaded, sitting next to the
context list that already knew. Two records of one truth can drift, and
they made testing miserable: issue #543 took four failed fixes, every one
defeated by process-global state coordinated through an environment
variable with several test classes as competing writers.

There is now one `BowirePluginLoader` per Bowire instance, built at the
composition root and passed down, and one `BowirePluginOptions` answering
"which directory" instead of two code paths that could disagree. A test
constructs plugin management with an explicit directory and touches
nothing ambient.

**What this does not claim.** Assembly loading is process-wide. Two
loaders keep separate ledgers and separate load contexts, but once either
has run, the assemblies stay visible to the whole process — an
`AssemblyLoadContext` cannot be scoped to a container or a test. The goal
was one owner for that global state, reached through an interface, not the
absence of global state. Collectible contexts would allow unloading and
bring their own sharp edges; that stays a separate decision.

Two defects surfaced on the way. A whitespace-only `BOWIRE_PLUGIN_DIR`
used to shadow a working `Bowire:PluginDir` from `appsettings.json`,
because the layer that admitted the value and the one that read it back
disagreed on what "empty" means. And `PluginManifestProbe.HostVersionOverride`
— a mutable static test seam with two writers — made a suite failure
depend on scheduling; the contract version is now an option on the loader.

Nothing here changes a public API. `IBowireProtocol` is untouched, plugins
keep compiling, and `PluginManager` was internal to the CLI.

### Schema watch tells you what changed since you last looked (#185)

Schema Watch (#48) could already re-discover on a timer and mark what
moved in the sidebar — but the delta was ephemeral: dismissed, reloaded,
gone. The "I came back from lunch, what's new in this API?" workflow
didn't exist.

Every detected change now lands in a per-workspace **change log**, kept
for **7 days** on the server, so it survives reloads and reaches every
client of the workspace. Three surfaces feed off it: a statusbar pill
next to the watch toggle ("3 changes since 14:30", decaying to a quiet
"12 changes · 7d" once read), a gently pulsing dot on the Discover rail
icon while anything is unread, and the pill's dropdown — the
chronological log itself, where clicking a change navigates straight to
the affected method in Discover. Opening the log marks it read; the
read watermark is server-side, so it holds across browsers.

The diff itself got sharper on the way. A changed method is now
classified instead of being a bare `~`: a **signature** change names the
facet that moved ("route GET /pets → POST /pets", "request shape
changed"), a **deprecation** flip is its own type, and prose-only edits
— which #48 deliberately refused to alert on, because descriptions move
constantly under development — are recorded as the quiet `±`
**annotation** type: in the log, never in a toast or a sidebar marker.

The watch interval finally stops being global-only: a workspace can
override it in its General tab, read the next time the watch starts.
New endpoints: `GET`/`POST /api/schema-changes` +
`POST /api/schema-changes/read`, workspace-scoped like the preset
endpoints, backed by `workspaces/<id>/schema-changes/log.json`. The
server is the clock authority (entries are re-stamped on append, so a
skewed browser clock can't produce changes that are born read or can
never be read), duplicate observations from two watching clients
collapse into one entry, the log caps at 500 entries, and a
browser-only (#212) workspace keeps its log session-local — nothing
touches the server's disk. In a git-backed workspace the file lands
inside the checkout; it's a rolling log, so gitignore it unless you
want teammates on the same clone to share the pill.

### Side-by-side service version diff (#182)

Set two versions or deployments of a service against each other and see
what moved. A **Compare** button in the Discover toolbar opens a
full-pane surface: pick a source and a service on each side —
*Baseline* and *Target* — and Bowire aligns their methods (matching
version markers, so `GetUser` pairs with `GetUser_v2` and `GET
/v1/users` with `GET /v2/users`), then splits them into added /
removed / signature-changed, reusing the same AST diff as the schema
watch (#185).

For any aligned unary method, **Diff response** invokes it on both
sides and diffs the two bodies **field by field, type-aware** — `$.total:
type number → string`, `$.items.0.sku: added` — not a line diff; a new
`diffJsonStructured` ports the Flows snapshot comparer's walk into a
pure client helper. **Export markdown** writes the whole thing as a
report ready for a PR comment, which is exactly what the v2.5 PR bot
will post.

Each side discovers its chosen URL independently, which is the point:
discovery de-dupes services by name, so the *same* service at two
deployments collapses to one row in the tree — the compare surface
reaches both anyway, and the toolbar button shows whenever there are
two services OR two discovery URLs to set against each other.

### The contract matrix — consumer × provider at a glance (#364)

`bowire contract verify` told you about one contract at a time. With a
handful of consumers against a handful of providers, "is anything broken
right now?" meant reading a pile of CI logs. Every verify run now stores
its verdict under `.bowire/contract-results/`, and that store backs a
matrix: rows are consumers, columns providers, each cell carries
pass/fail, how many interactions held, and when it last ran. Pairs nobody
verified show as blanks rather than silently missing.

The rollup is available from all four surfaces off one shared engine: the
**Contracts** rail in the workbench (grid plus drill-in to a failing
interaction's shape diff), `bowire contract matrix` (text grid, `--json`,
and `--fail-on-failures` as a CI gate), `GET /api/contracts/matrix`, and
the `bowire.contract.matrix` MCP tool, which hands an agent only what
broke per failing cell. Reading the matrix never contacts a provider — it
reports what verify already stored, so opening the rail cannot trigger an
outbound call.

Getting there meant lifting the verification engine out of the CLI
assembly, where it was `internal` and unreachable for the endpoint and
MCP, into a new **`Kuestenlogik.Bowire.Contracts`** package. It sits above
`Kuestenlogik.Bowire.Flows` (verification reuses the structural snapshot
comparer), ships the rail descriptor and its JS fragment, and is bundled
into the standalone tool — so `bowire contract` is unchanged and the rail
is simply there. Embedded hosts opt in by referencing the package, like
every other rail.

### Latency budgets that fail a pipeline (#360)

Benchmarks were informational: the workbench rail measured p50/p95/p99 and
drew the graphs, but the numbers lived in the browser, so no pipeline could
fail on a latency regression — the one thing k6 gets reached for. `bowire
bench run` puts the same measurement on the command line and adds budgets:

```bash
bowire bench run Weather/getCurrent -url rest@http://localhost:6000 \
  -n 500 -c 8 --warmup 20 \
  --threshold "p95 < 200" --threshold "error-rate < 0.01" \
  --fail-on-threshold
```

A breached budget is marked in the summary with what it actually measured,
and `--fail-on-threshold` turns that into a non-zero exit. Budgets read
`metric operator value` over p50/p90/p95/p99/avg/min/max/error-rate/
throughput; k6's own `p(95)` spelling parses too, so a threshold can be
copied straight out of a k6 script.

`--k6-summary` writes the run in the shape the rail already exports (#234),
with each budget attached the way k6 reports its own — keyed by source text
inside the metric it constrains, with an `ok` flag — so a dashboard that
ingests k6 summaries finds Bowire's budgets where it looks for k6's.

The measurement itself is deliberately not a second implementation: the
runner drives `IBowireProtocol.InvokeAsync`, the same path `bowire call` and
`bowire test` take, percentiles use the rail's nearest-rank method, and
success/failure follows the same rule the workbench history uses. A p95 read
in the rail is the p95 CI compares against.

### One view over a portfolio of services (#587)

Per-service findings answer "is this service healthy?" — the rollup answers
it for a whole portfolio, and it needs nothing new to be produced. Every
signal it shows is an artefact some Bowire command already writes: lint
findings, contract-verification results, benchmark runs and k6 summaries,
scan SARIF, test JUnit.

```bash
bowire report rollup --from reports/ --fail-on high
```

```
  SERVICE      WORST    LINT (H/M/L)   CONTRACTS   TESTS       P95       LAST
  billing-api  HIGH     —              0/1         —           312ms     2026-08-20
  gateway      HIGH     —              —           —           —         —
  orders-api   MEDIUM   0/1/1          1/1         42/42       —         2026-08-19
```

An em dash means **there is no such report**, never zero: a service nobody
has linted must not read as a clean bill of health. In JSON the same
distinction is `null` versus a number.

Reports are attributed to services without configuration — a contract files
under its **provider** (the service under test, not the consumer), and
otherwise the first path segment that isn't storage layout decides, so a CI
job that collects reports into `reports/<service>/` just works.

The same rollup is in the workbench as the **Rollup** rail (click a row to
see which files fed it) and as the `bowire.report.rollup` MCP tool, all three
emitting the same JSON. Reading it never contacts a service; it reports what
is already on disk.

This is the read-and-aggregate half of the org dashboard. The hosted platform
— upload endpoint, retained history, org login, admin actions — remains #188.

## Breaking changes

<!-- Each change has been on a back-compat ramp through the prior minor
and is removed in this release. Add a section per breaking change, with
the migration path. -->

### The standalone `bowire` tool now wires the catalogue seam (#537)

`bowire` calls `AddBowireCatalogue()` unconditionally. With no provider
configured this is a no-op — the accessor resolves to null and the
endpoints short-circuit to an empty list, exactly as before.

The one behaviour change: the `local` provider defaults to
`~/.bowire/catalogue.json`. An operator who has that file left over from
an earlier experiment **and** selects the `local` provider (via
`--catalogue-provider local`, appsettings, or a persisted
`bowire catalogue use`) will now see those entries merged into their
workspace's sources. Merged entries are not persisted to browser storage,
so removing the file or the configuration removes them again.

### `BowireOptions.AutoCreateInitialWorkspace` is now `bool?` (#535)

The property changed from `bool` to `bool?` so that "the host has no
stance" is expressible and distinct from an explicit opt-out. `null`
(the new default) resolves from `BowireOptions.Mode` — Embedded seeds a
workspace, Standalone does not — and leaves the per-browser Settings →
General toggle in control. `true` / `false` remain an explicit host
stance that locks that toggle read-only.

This is source-breaking for readers, not writers:

```csharp
options.AutoCreateInitialWorkspace = true;          // unchanged
bool seeded = options.AutoCreateInitialWorkspace;   // no longer compiles
bool seeded = options.AutoCreateInitialWorkspace ?? false;  // migration
```

Hosts that never touched the property need no change, but note that
leaving it unset now means "seed" in embedded mode where it previously
meant "don't". Pass `false` to keep the 2.2 behaviour.

`window.__BOWIRE_CONFIG__.autoCreateInitialWorkspace` follows the same
shape and can now be `null`; a new `hostName` key next to it carries the
resolved host display name.

### `/api/services` — `attempts` changes from `string[]` to `object[]` (#534)

The `attempts` extension on the `urn:bowire:discovery:no-match`
ProblemDetails body used to be an array of pre-formatted strings
(`"gRPC: connection refused"`). It is now an array of objects:

```jsonc
{ "pluginId": "grpc", "plugin": "gRPC", "outcome": "error",
  "servicesFound": 0, "durationMs": 2011, "message": "connection refused" }
```

`outcome` is one of `ok` / `empty` / `partial` / `error` / `timeout` — see
below for `partial`, which is additive on top of #534's four. The array now
covers **every** probed plugin, not only the failing ones, and it is also
present (empty) on the `urn:bowire:discovery:no-plugins` body so clients can
render one code path.

**Migration** — scripts that string-matched the old entries should read
`plugin` + `message` instead, and filter on `outcome` rather than assuming
every entry is a failure. Bowire's own workbench accepts both shapes, so a
newer workbench pointed at an older embedded host keeps working.

Two smaller wire-adjacent changes ride along:

- The `hint` extension (``Add a `protocol@` prefix …``) is now omitted when
  there is no server URL to prefix. It previously emitted the nonsense text
  ``rest@`` for an embedded host with no configured URL.
- The `bowire.discover` MCP tool's JSON result gained an `attempts` field
  next to `url` and `services`.

### `/api/services` — additive `partial` outcome, `details`, and an opt-in success envelope (#544)

Three additive changes on top of the #534 shape above. Nothing moves for a
client that ignores all three.

`outcome` gains a fifth value, `partial`: the plugin returned services
**and** reported a fault while producing them, so its contribution is
incomplete. It is deliberately not folded into `ok` — a dashboard has to be
able to tell a populated-but-incomplete tree from a clean one. Only plugins
implementing `IBowireDiscoveryDiagnostics` can produce it.

An attempt may carry an optional `details` array: the per-step breakdown
behind `message` — one line per faulted MCP surface, one per well-known path
a REST sweep tried. The field is omitted entirely when there is nothing to
break down.

```jsonc
{ "pluginId": "mcp", "plugin": "MCP", "outcome": "partial",
  "servicesFound": 2, "durationMs": 431,
  "message": "2 services, but tools/list returned a payload this MCP revision rejects — …",
  "details": ["tools/list returned a payload this MCP revision rejects — …"] }
```

`partial` implies `servicesFound > 0`, which means it arrives on a **200**,
where the body has always been a bare `BowireServiceInfo[]` with nowhere to
put a diagnostic. `GET /api/services?includeAttempts=1` switches the success
body to `{ "services": [...], "attempts": [...] }`. Without the flag the
bare array ships byte-for-byte as before, so no existing consumer moves;
Bowire's own workbench sends the flag and accepts both shapes
(`Array.isArray(body) ? body : body.services`), so a newer workbench pointed
at an older embedded host keeps working. There is no "fetch the attempts
afterwards" endpoint on purpose — `BowireDiscoveryProbe` is stateless, so it
would have to probe twice.

Plugin authors: build the diagnostic from locals of the call that produced
it. The channel is a return value so that two concurrent probes of two URLs
through one plugin instance cannot read each other's diagnosis; stashing it
in a field or a static ring buffer re-creates exactly that bug.

### Telemetry: `bowire.discover.count` outcome vocabulary widened (#534, #544)

The `outcome` dimension now takes `ok` / `empty` / `partial` / `error` /
`timeout`. `canceled` is gone (it reports as `timeout`), and a probe that
succeeded with zero results now reports `empty` instead of `ok`. Dashboards
or alerts filtering on `outcome="ok"` will see counts drop with no change in
behaviour — sum `ok` + `empty` to recover the old total, and add `partial`
if you are counting "probes that produced something".

## Acknowledgements

<!-- Optional. Names of contributors who exercised rc / reported. -->
