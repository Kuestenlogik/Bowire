---
title: Multi-user
summary: 'Running one Bowire for several people — an auth provider gates the door, each identity gets its own slot on disk, and an install that was single-user can bring its data with it.'
---

# Multi-user Deployment

Out of the box Bowire treats every caller as the same person, and on a laptop that is the right answer. On a shared host two things stop being true: anyone who can reach the URL can drive Bowire at your services, and everybody's recordings, environments and tokens are in one pile.

Both have answers today. This page is how they fit together.

## Locking the door

An **auth provider** decides who may use the workbench at all. It is an extension point next to protocol plugins and UI extensions, so the heavyweight identity dependencies only land in installs that use them:

```bash
bowire --url … \
       --auth-provider oidc \
       --auth-oidc-authority https://login.example.com \
       --auth-oidc-client-id bowire \
       --auth-oidc-required-claim "groups=bowire-users"
```

With no provider selected, nothing changes and the endpoints stay open — the laptop-friendly default. With one selected, every Bowire route requires an authenticated caller, and a provider named on the command line that cannot be found is a startup failure rather than a silent fallback to no auth.

`Kuestenlogik.Bowire.Auth.Oidc` is the first concrete provider: any OIDC-compliant IdP — Entra ID, Okta, Keycloak — without provider-specific code paths.

In embedded mode the host's own auth pipeline wins. Bowire only attaches a scheme when you ask it to.

## Giving each person their own state

Authentication answers *who is calling*. It does not move anything on disk: with a provider configured and nothing else, every signed-in person still shares one `~/.bowire/`.

Per-identity storage is a separate switch:

```jsonc
{ "Bowire": { "MultiTenant": { "Enabled": true } } }
```

It is deliberately not implied by configuring a provider. Plenty of installs put a login in front of a workbench that still has one person behind it, and moving their data because they added OIDC would be a surprise rather than a feature.

Switched on, each identity gets a slot under the storage root:

```text
~/.bowire/
├── environments.json                    ← the single-user layout, still here
├── collections.json
├── plugins/                             ← two tiers, admin-managed and yours
└── users/
    ├── ada-example.com-4f2a1c07/
    │   ├── environments.json
    │   ├── collections.json
    │   ├── recordings/
    │   ├── workspaces/
    │   └── .migration.json
    └── grace-example.com-9b3e5d10/
```

The slot name is a readable rendering of the subject plus a fingerprint of it. The readable half means an operator can tell whose directory is whose without a lookup table; the fingerprint is what keeps `a.b@example.com` and `a-b@example.com` — which render identically — from sharing a slot and reading each other's secrets.

Which claim identifies a person is `sub`, then `nameidentifier`, then `oid`. Override with `Bowire:MultiTenant:SubjectClaim`; when you do, there is no fallback, because a fallback would quietly file two identities into one slot.

Details of the layout, the instance segment and the project opt-in are in [Where Bowire stores things](../architecture/storage-locations.md).

## Bringing the existing data with you

The day you turn per-identity storage on, every store starts resolving somewhere new. The first person to sign in sees an empty workbench, and the conclusion they draw is that enabling auth cost them their work.

So Bowire offers to bring it across, once, per identity:

| `Bowire:MultiTenant:Migration` | What happens |
|---|---|
| `Prompt` *(default)* | The person is asked once. The first identity to sign in is not reliably the one the data belongs to — it may be the operator's admin account. |
| `Auto` | Copied into the first identity that signs in, without asking. |
| `Skip` | Never offered. For starting clean, or when you have already moved the data yourself. |

What it does, and does not, do:

- **Copies. Never moves.** The originals stay where they are, so you can switch back to single-user without a second migration, and a migration into the wrong slot is undone by declining it in the right one. Delete the originals when you are satisfied — that timing is yours.
- **Brings everything except what is not a person's**: `plugins`, `certs`, `logs`, `cache`, `state` and `project.json` stay behind. The rule is an exclusion list on purpose — a new store's data comes along by default, because forgetting to include something loses data while forgetting to exclude something copies a cache.
- **Leaves a slot that already holds work alone.** Merging two sets of environments produces one set nobody can separate again.
- **Records the decision** in `.migration.json` inside the slot: what was copied, from where, when, and whether it was accepted or refused. It sits in the slot rather than in a central log, so deleting an identity deletes its record too.

## Plugins in a multi-user install

Installed plugins are not per-identity state. They resolve in two tiers: a machine-wide directory an administrator manages (`%ProgramData%\Bowire\plugins`, `/var/lib/bowire/plugins`) and the running account's own directory on top of it. Uninstalling something from the machine tier is refused and names the elevated command that would work.

Per-*identity* plugin installs are still open — see [#284](https://github.com/Kuestenlogik/Bowire/issues/284). It is not a path question: plugins are assemblies loaded into the host process, so letting each signed-in person add one to a shared server is a privilege decision before it is a storage decision.

## What is still on the roadmap

- **SCIM 2.0 provisioning** (`/scim/v2/Users`, `/scim/v2/Groups`) for IdP-driven user lifecycle — [#96](https://github.com/Kuestenlogik/Bowire/issues/96).
- **A user chip, scoped state copy and admin impersonation** in the workbench — [#98](https://github.com/Kuestenlogik/Bowire/issues/98).

## When this matters

* **A few developers, a laptop each** — single-user is fine; share collections through git-backed workspaces.
* **A team behind a shared host** — an auth provider gates access. Whether you also want per-identity storage depends on whether the state is shared work or personal work.
* **Org-wide** — per-identity storage, provisioning, and per-identity plugin policy.

## Related

* [Where Bowire stores things](../architecture/storage-locations.md) — scopes, the project opt-in, instances, and the slot layout
* [Sidecar deployment](sidecar.md)
* [Standalone CLI](standalone.md)
* [Embedded mode](embedded.md) — the host owns the auth pipeline; Bowire inherits it
