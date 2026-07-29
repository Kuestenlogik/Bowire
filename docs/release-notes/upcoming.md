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

## Breaking changes

<!-- Each change has been on a back-compat ramp through the prior minor
and is removed in this release. Add a section per breaking change, with
the migration path. -->

### `/api/services` — `attempts` changes from `string[]` to `object[]` (#534)

The `attempts` extension on the `urn:bowire:discovery:no-match`
ProblemDetails body used to be an array of pre-formatted strings
(`"gRPC: connection refused"`). It is now an array of objects:

```jsonc
{ "pluginId": "grpc", "plugin": "gRPC", "outcome": "error",
  "servicesFound": 0, "durationMs": 2011, "message": "connection refused" }
```

`outcome` is one of `ok` / `empty` / `error` / `timeout`. The array now
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

### Telemetry: `bowire.discover.count` outcome vocabulary widened (#534)

The `outcome` dimension now takes `ok` / `empty` / `error` / `timeout`.
`canceled` is gone (it reports as `timeout`), and a probe that succeeded
with zero results now reports `empty` instead of `ok`. Dashboards or alerts
filtering on `outcome="ok"` will see counts drop with no change in
behaviour — sum `ok` + `empty` to recover the old total.

## Acknowledgements

<!-- Optional. Names of contributors who exercised rc / reported. -->
