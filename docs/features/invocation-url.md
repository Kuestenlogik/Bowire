---
title: Invocation URL override
summary: 'The URL a schema came from and the URL a call goes to are two different things. Point any discovered method at a different host — the same as the schema, a workspace Source, or a custom one-off — without re-discovering.'
---

# Invocation URL override

Bowire discovers a service from a **schema URL** — where its OpenAPI document, `.proto` reflection endpoint, or GraphQL introspection lives. By default the call you send when you hit Execute goes to that same URL. But the two are not always the same host ([#253](https://github.com/Kuestenlogik/Bowire/issues/253)):

- The OpenAPI document is hosted at `docs.example.com/openapi.json` while the API answers at `api.example.com`.
- You discovered against **staging** and want to run the exact same call against **production**.
- A gateway sits in front of an internal, reflection-enabled gRPC server on a different external hostname.

Without the split, a method saved against the schema host silently calls that host — often a docs server — and 404s or connect-refuses with no obvious cause.

## The override

Every discovered method's request pane carries an **Invocation URL** disclosure, above the request body. It is collapsed by default (the pill reads *schema URL*, signalling the call goes where the schema came from). Open it to choose one of three modes:

| Mode | The call goes to | Use when |
|---|---|---|
| **Same as schema** | `service.originUrl` — where the schema came from | The default; the schema host also serves the API. |
| **From Source** | A workspace Source URL you pick from the dropdown | You want to call another deployment already in your Sources list. Resolved live, so renaming or retiring the Source propagates. |
| **Custom** | A URL you type | A quick one-off against a host that isn't in your Sources. `{{vars}}` are substituted at send time, so `https://{{env}}.api.example.com` works. |

The override is stored **per method, per workspace** — it survives a reload and rides along when you save the method into a collection (so the item replays against the same host, not the schema URL). It applies to unary, streaming and channel calls alike; discovery itself is untouched, so the schema keeps coming from where you pointed it.

Prefer it always open? **Settings → General → Always show invocation URL override** expands the disclosure on every method.

## What it affects

The override redirects the actual wire call and everything that describes it — the per-URL headers applied to the request, the URL a pre-request script signs or inspects, and the host a recording labels its step with — so all of them reflect the URL the call *hits*, not the one the schema came from. A `{{var}}`-templated custom URL is substituted consistently across all of them.

## Not yet

- The freeform / Compose builder still models one URL (its `urlMode` inline / from-Source toggle from #252) — a freeform request is ad-hoc, so its URL is already the invocation URL. A separate schema-source field there is deferred.
- Recording and benchmark surfaces carry the invocation URL on their steps, but a side-by-side view of *both* URLs when they differ is deferred (issue #253 Phase 3).
