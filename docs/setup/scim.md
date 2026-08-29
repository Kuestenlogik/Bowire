---
title: SCIM provisioning
summary: 'Hand the Bowire user list to your identity provider — Okta, Entra ID or Google Workspace create, update and deprovision identities over SCIM 2.0, and a deactivation actually takes effect.'
---

# SCIM provisioning

Once Bowire knows who is calling ([an auth provider](multi-user.md#locking-the-door)) and gives each identity its own slot ([per-identity storage](multi-user.md#giving-each-person-their-own-state)), one thing is still manual: the list of who exists. Somebody joins, somebody leaves, somebody changes team — and an operator has to do something about it by hand, or not at all.

SCIM 2.0 ([RFC 7644](https://www.rfc-editor.org/rfc/rfc7644)) is what every identity provider speaks for exactly that. Point yours at Bowire and the list keeps itself.

> **The half that matters.** Provisioning is easy to build as bookkeeping — a flag written to disk that nothing reads. Bowire enforces it: a deactivated identity is refused at the door, and their state is moved out of reach. An install where deactivating in Okta leaves someone working until a person notices has not deprovisioned anybody.

## Turning it on

```jsonc
{
  "Bowire": {
    "MultiTenant": { "Enabled": true },
    "Scim": {
      "Enabled": true,
      "Token": "…a long random secret…"
    }
  }
}
```

Enabling without a token is **refused at startup**, not served open: a provisioning endpoint reachable by anyone who can route to the host is a way to create identities.

The endpoints mount at `/scim/v2` — outside the workbench's own route group, and on purpose. Those routes are gated by whatever auth provider you configured, and a provisioning connector holds a shared secret rather than a user session; it could never pass that gate. SCIM authenticates itself, with its own token, on its own path.

| Key | Default | |
|---|---|---|
| `Bowire:Scim:Enabled` | `false` | Mounts the endpoints. |
| `Bowire:Scim:Token` | — | The bearer token the IdP presents. Required. |
| `Bowire:Scim:BasePath` | `/scim/v2` | Where they mount. |
| `Bowire:Scim:PurgeAfter` | `30.00:00:00` | How long a deprovisioned identity's state is kept. |
| `Bowire:Scim:EnforceActive` | `true` | Refuse a deactivated identity at the door. |
| `Bowire:Scim:RequireProvisioned` | `false` | Refuse an identity the directory has never heard of. |
| `Bowire:Scim:AdminGroup` | `bowire-admins` | The group whose members count as administrators. |
| `Bowire:Scim:DefaultPageSize` / `MaxPageSize` | `100` / `500` | List paging. |

## Pointing an identity provider at it

Both connectors need two things: the base URL and the token.

- **Okta** — *Applications → your app → Provisioning → Configure API Integration*. Base URL `https://bowire.example.com/scim/v2`, API token as configured above. Enable *Create Users*, *Update User Attributes* and *Deactivate Users*.
- **Entra ID** — *Enterprise applications → your app → Provisioning*. Tenant URL `https://bowire.example.com/scim/v2`, Secret Token as configured above. *Test Connection* reads `/ServiceProviderConfig` before it will save.

### Which claim has to line up

This is the part that quietly does not work if it is wrong.

Provisioning identifies a person by `userName` and `externalId`. A **token** identifies them by whatever claim `Bowire:MultiTenant:SubjectClaim` names. Bowire ties the two together on the person's first request, matching the token's subject against the record's `externalId`, then its `userName`. So one of those has to be what your tokens actually carry:

- **Entra ID** — the connector sends the object id as `externalId`, and tokens carry it as `oid`. Set `SubjectClaim` to `oid`.
- **Okta** — the connector sends the Okta user id as `externalId` and the login as `userName`; the default `sub` claim usually matches the latter, so the fallback covers it.

Once matched, the subject is written onto the record and used from then on — which is what keeps a rename from orphaning somebody's work.

## What happens when someone is deprovisioned

Deactivating (`PATCH … active: false`) or deleting (`DELETE`) does three things:

1. The record is marked inactive. It is **not** removed — deprovisioning is routinely undone, and a hard delete makes those recoverable only from a backup.
2. Their slot is **moved aside**, under `users/.deprovisioned-…`. They cannot reach it, and neither can anyone else.
3. Their next request is refused with `403` and `urn:bowire:scim:deprovisioned`.

Reactivating puts the slot back exactly where they left it. That is what makes "deactivate" reversible rather than a polite word for delete.

After `Bowire:Scim:PurgeAfter` — 30 days by default — a daily sweep deletes the record and the archived slot for good. Set it to `00:00:00` to delete immediately, if your retention policy says so.

Every one of these decisions is appended to `scim/events.jsonl` under the storage root: what happened, to whom, and when. The record files only ever show the current answer, and "who removed this person" gets asked months later.

## What is implemented, and what is not

Said plainly, because a connector that is told something works and then gets a 404 retries the whole sync instead of falling back. `/ServiceProviderConfig` advertises exactly this list.

| | |
|---|---|
| **Users** | `GET` (list + by id), `POST`, `PUT`, `PATCH`, `DELETE` |
| **Groups** | `GET` (list + by id), `POST`, `PUT`, `PATCH`, `DELETE` |
| **Discovery** | `/ServiceProviderConfig`, `/ResourceTypes`, `/Schemas` |
| **Filtering** | `eq` and `pr`, joined with `and` / `or` |
| **Paging** | `startIndex` (1-based) and `count` |
| **Not implemented** | Bulk, sorting, ETags, password change |

The filter subset is deliberate. The full grammar has ten operators, complex attribute paths and value sub-filters; the connectors that matter send one shape between them, `userName eq "someone@example.com"`. Anything outside the subset is refused with `400` and `invalidFilter` rather than half-evaluated — a parser that ignores the part it did not understand answers a different question, and the caller has no way to tell.

Attributes Bowire does not model — the Enterprise User extension, whatever your directory maps — are stored verbatim and returned on the next `GET`. A connector that reads back a resource missing what it just wrote concludes the write failed, and retries forever.

## Groups and the administrator role

Group membership is stored and synced. Its one consumer today is `Bowire:Scim:AdminGroup`: members of that group are the identities Bowire treats as administrators. The surfaces that act on that — a user chip, scoped state copies, admin impersonation — are [#98](https://github.com/Kuestenlogik/Bowire/issues/98) and not shipped yet, so provisioning the group now is preparation rather than an effect you will see in the workbench.

The group name is configuration rather than a constant, because an IdP's group for this is called whatever your directory calls it.

## Checking it by hand

```bash
TOKEN='…'
BASE='https://bowire.example.com/scim/v2'

curl -s -H "Authorization: Bearer $TOKEN" "$BASE/ServiceProviderConfig"

curl -s -X POST "$BASE/Users" \
     -H "Authorization: Bearer $TOKEN" \
     -H 'Content-Type: application/scim+json' \
     -d '{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],
          "userName":"ada@example.com","externalId":"8f14e45f","active":true}'

curl -s -H "Authorization: Bearer $TOKEN" \
     --get --data-urlencode 'filter=userName eq "ada@example.com"' "$BASE/Users"
```

`bowire users list` on the host shows the identity slots and what each one decided about the [single-user migration](multi-user.md#bringing-the-existing-data-with-you); the SCIM record list is under `scim/` in the storage root.

## Related

* [Multi-user deployment](multi-user.md) — the auth gate and per-identity storage this builds on
* [Where Bowire stores things](../architecture/storage-locations.md) — the layout `scim/` and `users/` live in
