---
summary: 'Multi-user / multi-tenant Bowire — the plan and the seam. Single-user is shipped today; per-user storage + SCIM provisioning land alongside the auth-provider extension SPI (roadmap Phase B).'
---

# Multi-user Deployment

> **Status:** Single-user mode is shipped today. **Per-user state separation is roadmap Phase B**, gated on the auth-provider extension SPI shipping first (Phase A). This page describes both the current single-user shape and the planned multi-user model so you can plan migration ahead.

## Single-user today

Out of the box, `bowire` (standalone or embedded) treats every caller as the same identity. State lives in a single directory:

```text
~/.bowire/
├── environments.json
├── recordings/
├── collections/
├── flows/
└── plugins/
```

This is fine for laptops and dev sandboxes. The moment Bowire goes on a shared host, two things become necessary:

1. **An auth gate** — so not every visitor of the host's URL can poke at services through Bowire.
2. **Per-user state** — so colleague A doesn't see colleague B's recordings, environments, or auth tokens.

Both land via the **auth-provider extension SPI** (roadmap Phase A → B). Until then, the safe shape for a shared host is the [sidecar pattern](sidecar.md) behind your own service mesh / ingress auth.

## Phase A — locking the door (planned)

A third extension type next to `IBowireProtocol` and `IBowireUiExtension`: **`IBowireAuthProvider`**. The contract:

* `AddAuthentication(IServiceCollection, BowireAuthOptions)` — wires up the authentication scheme.
* `BuildDefaultPolicy(AuthorizationPolicyBuilder)` — endpoints `RequireAuthorization()` against it.

Activated by a CLI flag:

```bash
bowire --url … \
       --auth-provider oidc \
       --auth-oidc-authority https://login.example.com \
       --auth-oidc-client-id bowire \
       --auth-oidc-required-claim "groups=bowire-users"
```

When unset, behaviour stays identical to today. When set, the named provider must be discoverable in the plugin load path or `bowire` fails fast.

First concrete provider plugin: **`Kuestenlogik.Bowire.Auth.Oidc`** — Microsoft.Identity.Web-based, so Azure AD, Okta, Keycloak, and any OIDC-compliant IdP work without provider-specific code paths. Ships as a separate NuGet so the heavy `Microsoft.Identity.Web` dependency only lands in installs that actually use OIDC.

In embedded mode, when the host has its own auth pipeline configured, the host's policy wins — Bowire's hook is opt-in only.

## Phase B — per-user storage (planned, blocked on Phase A)

Once Bowire knows who's calling, the next ceiling is "everyone shares one `~/.bowire/`". Phase B replaces the flat layout with per-user slices:

```text
~/.bowire-server/
└── users/
    ├── <sub-1>/
    │   ├── environments.json
    │   ├── recordings/
    │   ├── collections/
    │   ├── flows/
    │   └── plugins-overlay/
    └── <sub-2>/
        └── …
```

A new seam — `IBowireUserStore` — resolves storage paths against the active identity. Every store consumer (`EnvironmentStore`, `RecordingStore`, `CollectionStore`, `FlowStore`, `PluginManager`) routes through it. The single-user standalone mode keeps the flat layout by binding the store to a synthetic "default" user, so the migration path stays simple.

### SCIM 2.0 provisioning

Multi-tenant installs typically want IdP-driven user lifecycle. Phase B adds **SCIM 2.0** endpoints per RFC 7644: `/scim/v2/Users` + `/scim/v2/Groups`. A compliance test suite verifies Okta and Azure AD's provisioning sync round-trip correctly.

### Per-user plugin installs

`~/.bowire/plugins/` splits into a system-wide tier (admin-managed) plus a per-user overlay so users can install workflow-specific plugins without admin help.

### Migration from single-user

Single-user installs upgrading into multi-tenant get a one-shot migration command that promotes the existing flat `~/.bowire/` into the calling user's slot.

## When this matters

* **Small teams (≤ 5 devs on a laptop each)** — single-user is fine; share recordings via Git.
* **Mid-sized team behind a shared host** — Phase A gates access; everyone still shares one state slice. Acceptable for read-heavy workflows.
* **Org-wide deployment** — Phase B is the real shape: per-user state, SCIM provisioning, per-user plugin installs.

## Roadmap reference

* [Auth-provider extension SPI (Phase A)](https://github.com/Kuestenlogik/Bowire/blob/main/ROADMAP.md#auth-provider-extension-spi-phase-a--core-seam)
* [OIDC plugin (Phase A first concrete impl)](https://github.com/Kuestenlogik/Bowire/blob/main/ROADMAP.md#auth-oidc-provider-plugin-phase-a--first-concrete-impl)
* [Multi-tenant data model + SCIM (Phase B)](https://github.com/Kuestenlogik/Bowire/blob/main/ROADMAP.md#multi-tenant-data-model--scim-phase-b--blocked-on-phase-a)

## Related

* [Sidecar deployment](sidecar.md) — the recommended shape for shared hosts until Phase A ships
* [Standalone CLI](standalone.md) — single-user mode, today
* [Embedded mode](embedded.md) — host owns the auth pipeline; Bowire inherits it
