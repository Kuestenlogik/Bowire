---
title: Service catalogue
summary: 'Point Bowire at a file, an HTTP endpoint, Consul, Kubernetes or an agent hub and browse the services your organisation already knows about instead of typing URLs.'
---

# Service Catalogue

Auto-discovery answers "what can I call at this URL?". The catalogue
answers the question before it: **which URLs are there in the first
place?**

A catalogue provider hands Bowire a list of targets — name, URL,
protocols, tags — and the workbench turns that into a browsable picker
wherever it would otherwise ask you to type a URL from memory. Nothing
is mandatory: a host with no catalogue configured behaves exactly as it
always has, and manual URL entry stays one click away even when a
catalogue is present.

## The document shape

Every provider ultimately produces the same rows. The `local`, `http`
and `agent` providers read them as JSON:

```jsonc
{
  "version": 1,
  "entries": [
    {
      "url": "https://staging-payments.example.com",
      "name": "Staging Payments",
      "protocols": ["rest", "grpc"],
      "tags": ["env:staging", "team:payments"],
      "schema": "https://staging-payments.example.com/openapi.json"
    }
  ]
}
```

Only `url` is required.

- **`name`** — what the picker shows. Falls back to the URL.
- **`protocols`** — discovery hints. The **first** entry becomes a
  `protocol@` prefix on the URL Bowire probes, so
  `{ "url": "http://host:5183/graphql", "protocols": ["graphql"] }`
  is discovered as `graphql@http://host:5183/graphql`. Without that
  prefix a service living under a path is probed as a bare host and
  finds nothing. A URL that already carries its own hint
  (`grpcweb@http://…`) is left alone — it is never double-prefixed.
- **`tags`** — free-form, conventionally `key:value`. The picker turns
  them into filter chips.
- **`schema`** — an OpenAPI / SDL / `.proto` document that pins the wire
  shape so Bowire can skip reflection.

## Providers

| Id | Reads from | Ships in |
| --- | --- | --- |
| `local` | A JSON file on disk. Defaults to `~/.bowire/catalogue.json`; `BOWIRE_CATALOGUE_PATH` overrides it. | core |
| `http` | A remote URL returning the document above. Optional `Authorization` header. | core |
| `consul` | A Consul agent's `/v1/catalog` API. Optional ACL token, datacenter and tag filter. | core |
| `kubernetes` | A Kubernetes API server's `Service` objects. Auto-picks in-cluster service-account or kubeconfig credentials. | `Kuestenlogik.Bowire.Catalogue.Kubernetes` |
| `agent` | A Bowire Agent hub aggregating several networks. | `Kuestenlogik.Bowire.Catalogue.Agent` |

At most one provider is active per process — mixing two catalogues
invites confusion about which one owns a row. To aggregate, put a small
relay in front of Bowire and point the `http` provider at it.

The two sibling providers are **not** referenced by the workbench
bundle, so a stock install offers `local` / `http` / `consul` until you
run:

```bash
bowire plugin install Kuestenlogik.Bowire.Catalogue.Kubernetes
```

Settings → Plugins → **Discovery providers** greys out any provider
whose package is absent and names the package to install, rather than
letting you pick a row that silently fails at save time.

## Configuring it

### appsettings.json

```jsonc
{
  "Bowire": {
    "Discovery": {
      "Catalogue": {
        "Provider": "local",
        "RefreshInterval": "00:05:00",
        "Visibility": "Editable",
        "Local":  { "Path": "/etc/bowire/catalogue.json" },
        "Http":   { "Url": "https://catalogue.example.com/bowire.json", "Authorization": "Bearer …" },
        "Consul": { "Address": "http://localhost:8500", "Token": "…", "Datacenter": "dc1", "Tag": "bowire" }
      }
    }
  }
}
```

Embedded hosts opt in with one call next to `AddBowire()`:

```csharp
builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);
```

### CLI flags

The standalone `bowire` tool registers the catalogue for you; these
flags select and configure the provider:

| Flag | Maps to |
| --- | --- |
| `--catalogue-provider <id>` | `Bowire:Discovery:Catalogue:Provider` |
| `--catalogue-path <file>` | `Bowire:Discovery:Catalogue:Local:Path` |
| `--catalogue-url <url>` | `Bowire:Discovery:Catalogue:Http:Url` |
| `--catalogue-consul <addr>` | `Bowire:Discovery:Catalogue:Consul:Address` |

```bash
bowire --catalogue-provider local --catalogue-path ./team-services.json
```

### `bowire catalogue`

The verb works in-process — there is no server to start and no running
workbench to talk to.

```bash
# Which provider implementations does this install have?
bowire catalogue providers

# Read a catalogue without persisting anything.
bowire catalogue list --provider local --path ./team-services.json
bowire catalogue list --json            # scriptable snapshot

# Persist a provider choice to ~/.bowire/catalogue-config.json — the same
# file Settings → Discovery providers writes, so the workbench picks it up.
bowire catalogue use consul --consul http://consul.internal:8500 --token …
bowire catalogue clear
```

`list` prints the **composed** URLs — exactly the strings that end up in
the Sources rail — so what you see on the terminal is what the workbench
will probe. It exits `1` when no provider is configured and `78` when a
configured provider isn't loaded, so CI can gate on either.

With no flags, `list` reads whatever the workbench would: the persisted
override first, then nothing. With `--provider` (or a bare `--path` /
`--url` / `--consul`, which imply one) it reads that provider for this
invocation only.

## What it changes in the workbench

On boot Bowire fetches the catalogue **before** the first discovery run,
merges its entries into the workspace's source list, and probes them all
in one fan-out. Catalogue rows carry a `catalogue` chip in the Workspaces
tree so they are distinguishable from URLs you typed.

Wherever the workbench used to say "type a URL", it now offers the
catalogue first when there is one:

| Surface | With a catalogue | Without one |
| --- | --- | --- |
| Workspaces → Sources, the `+` button and its context menu | Opens the catalogue picker | Today's URL prompt |
| Workspaces → Sources detail pane | A **Catalogue** section with search, tag chips and per-row **Add**; `+ Add URL` moves below it | Unchanged, plus a *Configure a catalogue…* link |
| Discover, first run | **Browse catalogue (N)** as the primary action | Unchanged |
| Home welcome hero | A **Browse your service catalogue** tile first | Unchanged |

The picker's search matches name, URL, protocol and tag; the tag chips
filter by exact membership. **Add all** sits in the header as a
secondary action and asks for confirmation past 25 entries, because
fanning discovery across a large Consul catalogue is a foot-gun.

### Merged vs. adopted

A catalogue entry that Bowire merged on boot is **not** written to
browser storage. It belongs to the provider: if the entry disappears
upstream, the row disappears with it on the next refresh. Clicking
**Add** on a row *adopts* it — it becomes an ordinary workspace URL and
persists like any other, surviving the entry's removal upstream.

That distinction is why a URL you typed by hand is never silently
converted into a catalogue row, even if the catalogue happens to name
the same target.

### Visibility

`Bowire:Discovery:Catalogue:Visibility` controls how much URL management
the operator gets:

- **`Editable`** (default) — catalogue entries *and* ad-hoc URLs.
- **`Readonly`** — the catalogue list renders, but every add affordance
  is gone. For shared or hardened deployments with a fixed in-scope
  target list.
- **`Hidden`** — URL management is suppressed entirely: no catalogue
  block, no `+ Add URL`, just the read-only list of whatever the
  catalogue provides. For embedded hosts where the surrounding org's
  registry *is* the authoritative source.

## Degradation

- **No provider configured** — `GET /api/catalogue/info` reports
  `available: false`, `/entries` returns an empty list, and every
  catalogue affordance stays hidden. This is the default.
- **Provider configured but not loaded** (typo, or a sibling package
  that isn't installed) — `/info` still answers `200` and carries an
  `error` string naming the problem, which Settings → Discovery
  providers renders. It deliberately does **not** 500: a broken
  catalogue must not cost you the workbench.
- **Provider loaded but returning nothing** — the Sources pane says so
  by name and offers a **Refresh**.
- **Upstream unreachable** — `/entries` returns
  `urn:bowire:catalogue:fetch-failed` problem-details naming the
  provider; the workbench keeps whatever it already had.

See also: [Auto-discovery](auto-discovery.md) for what happens once a
URL is in the list, and [CLI mode](cli-mode.md) for the rest of the
`bowire` verb surface.
