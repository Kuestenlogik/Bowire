---
title: Header library
summary: 'Named, scoped header sets that ride along with matching requests instead of being retyped into each one.'
---

# Header library

Some headers belong to a *target*, not to a request. `Accept: application/vnd.example+json` is what that API speaks. `X-Api-Version: 2` is which version you are testing against. `User-Agent: my-tester/1.0` is how your gateway's access log tells your calls apart from production traffic.

The Metadata tab is per-request, so without somewhere else to put them these headers get retyped into every method, every environment, every workspace &mdash; or wrapped in a pre-request script, which is a lot of machinery for three static strings. Environments do not solve it either: they give you variables that a header *value* can reference, not a place to define the header.

A **header set** is a name, a scope, and a list of header rows. Sets whose scope matches the request apply automatically; you can flip any of them off &mdash; or a non-matching one on &mdash; for a single call.

## Where it lives

**Settings &rarr; Workspace &rarr; Header library.** The library belongs to the workspace, not to your browser profile: a team's API conventions travel with the project, so the library round-trips through `.bww` export / import alongside collections and environments.

Above any header or metadata editor you get a chip strip: one chip per set, a count of the headers that will actually be sent, and a **Manage** link back into the editor. The strip is hidden entirely when the library is empty &mdash; an empty strip costs vertical space to say nothing.

## Scopes

| Scope | Applies when | Example |
|---|---|---|
| `Everywhere` | always | a `X-Trace` header you want on every call while debugging |
| `URL host` | the request URL's **host** matches | `api.example.com` |
| `Service` | the discovered service matches | `UserService` |
| `Method` | one specific method matches | `UserService.GetUser` |

Host matching ignores scheme, port, path and credentials, so `api.example.com`, `https://api.example.com` and `https://api.example.com:8443/v1/pets` all name the same host. Service and method names compare case-insensitively, and a package-qualified service name (`acme.users.v1.UserService`) parses correctly in a method scope &mdash; the **last** dot is the separator.

A scope Bowire cannot read falls back to `Everywhere` rather than disappearing. A set nobody can reach is worse than one that shows up in the wrong place: at least you can see it and fix the scope.

## Precedence

Lowest first:

1. **Library sets, in library order.** A later set overwrites an earlier one that names the same header. Reorder the sets to change who wins.
2. **The request's own rows.** A header typed into the Metadata tab always wins, so a library can never silently override what you are looking at.

Header names compare case-insensitively &mdash; HTTP says they are, and `Accept` beside `accept` is a bug every time &mdash; but the first spelling seen is the one that ships.

An **unticked row contributes nothing and shadows nothing.** Turning off a request's `Accept` row lets the library's `Accept` through; it does not mean "send no `Accept`". To suppress a library header for one call, turn its chip off.

## The effective-headers preview

Layered headers create a question the moment they start working: *why is my request sending that?* The **N effective** button on the chip strip expands the merged result &mdash; every header in send order, its value after variable substitution, and where it came from. A header that beat someone else is marked, and the tooltip names who lost.

Chips carry the same information from the other direction: a set that is on but is being overridden further down shows a warning glyph, so it says so rather than quietly not mattering.

## Variables

Header values in a library set go through the same `{{name}}` substitution as anywhere else, resolved against the active environment at send time &mdash; so a set can carry `Authorization: Bearer {{token}}` and follow you across environments. Header **names** are not substituted.

See [Variables and environments](environments.md) for the resolution order.

## Toggling for one request

Clicking a chip sets a per-request answer that overrides the scope. Setting it back to what the scope already says removes the override again, so a request that agrees with its scope stores nothing &mdash; and a later scope edit still reaches it. A chip switched by hand is drawn with a dashed edge, so a deliberate one-off is distinguishable from the automatic behaviour.

## What it does not do

- **It does not reach the CLI yet.** `bowire call` and `bowire test` do not read the library, so a request run from the command line sends only its own headers. Follow the tracking issue before relying on the library in CI.
- **It is not per-team sharing.** The library travels with the workspace; sharing it is a workspace-sharing concern.
- **Saving a request to a collection stores the request's own rows**, not the merged result. The library re-applies when the collection item runs, which is what you want when the set later changes &mdash; but it does mean a collection is not a frozen record of every header that went out.

Tracked in [#95](https://github.com/Kuestenlogik/Bowire/issues/95).
