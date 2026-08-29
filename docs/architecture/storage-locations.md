# Where Bowire stores things

Bowire writes collections, environments, recordings, plugins, certificates and caches to disk. This page is the one place that says where, and why the answer is not simply `~/.bowire`.

## Two scopes

| Scope | Windows | Linux / macOS | For |
|---|---|---|---|
| **Data** | `~\.bowire` (or the project's `.bowire\`) | `~/.bowire` (or the project's `.bowire/`) | A person's own work: collections, environments, recordings, presets |
| **Machine** | `%ProgramData%\Bowire` | `/var/lib/bowire` | State a service instance must find regardless of which account it runs as |

**Data** is the default and the one almost everything uses. It follows the project opt-in below.

**Machine** exists because `~` is the *user's* home. A Bowire that runs as a service resolves it to the service account's profile, not to the operator's — so an admin can install a plugin, be told it worked, and have the service never see it, because the two were looking at different profiles with the same name. Anything a service reads needs a location that does not depend on the account.

> `SpecialFolder.CommonApplicationData` is not used for this: on .NET for Unix it maps to `/usr/share`, which is for static data shipped by a package manager rather than for state a service writes. The platform branch is written explicitly.

## The project opt-in

A repository can keep its Bowire data beside its code by saying so in `.bowire/project.json`:

```jsonc
{ "version": 1, "storage": "project" }
```

The **Data** scope then resolves to that `.bowire/` directory, so collections commit, diff and review like any other file, and two repos open in two windows keep separate sets.

This is deliberately read from the repository rather than passed as a CLI flag: the answer must not depend on who launched the process. The same checkout resolves to the same store from a terminal, from CI and from the VS Code extension.

It is opt-in — a manifest that says nothing keeps the machine-wide store, so nobody's existing data moves under them.

## Running several instances on one machine

`BOWIRE_INSTANCE` adds one path segment under every scope:

```bash
BOWIRE_INSTANCE=staging bowire      # ~/.bowire/staging/…
BOWIRE_INSTANCE=prod    bowire      # ~/.bowire/prod/…
```

Unset means the root itself, so nothing moves for the single-instance case — the same idea as `PGDATA` or `GEOSERVER_DATA_DIR`.

Two names are refused rather than accepted quietly, because both failures are invisible:

- **Anything that is not a single segment** — a separator, `..`, an absolute path. It would write outside the storage root and look like it worked.
- **A name Bowire already uses for a directory** — `plugins`, `workspaces`, `recordings`, `flows`, `cache`, `certs`, `logs`, `presets`, `mocks`, `users`, `scim`, `audit`. With no instance set the root *is* the scope, so an instance named `plugins` would share state with an unnamed one, which is the opposite of what setting it was for.

## Several people on one instance

One Bowire serving several signed-in people puts each of them in their own slot under the storage root:

```
~/.bowire/
├─ environments.json        ← the single-user layout, still here
├─ collections.json
├─ plugins/                 ← two tiers, an admin's business, not a person's
└─ users/
   ├─ ada-example.com-4f2a1c07/
   │  ├─ environments.json
   │  ├─ workspaces/
   │  └─ .migration.json    ← what was decided, and when
   └─ grace-example.com-9b3e5d10/
```

Turn it on with `Bowire:MultiTenant:Enabled`. It is off by default and is **not** implied by configuring an identity provider: plenty of single-user installs put a login in front of a workbench that still has one person behind it, and moving their data because they added OIDC would be a surprise, not a feature.

The slot name is a readable rendering of the subject with a fingerprint of it appended. The readable half is so an operator can tell whose directory is whose without a lookup table. The fingerprint is not decoration: `a.b@example.com` and `a-b@example.com` render identically, and two identities sharing a slot would read each other's environments — secrets included. It is taken over the untouched subject, so distinct subjects always land in distinct directories.

Which claim identifies a person is `sub`, then `nameidentifier` (ASP.NET maps the first onto the second unless the host disabled inbound claim mapping, so both are tried), then `oid` for Entra ID. `Bowire:MultiTenant:SubjectClaim` overrides that, and when it is set there is no fallback — an operator who named a claim and silently got e-mail addresses instead would be filing two identities into one slot.

### What happens to the data that was already there

Switching an existing install on moves where every store reads. Without a migration the person who signs in first sees an empty workbench, and the obvious conclusion — that turning on auth cost them their work — is the one people draw.

So Bowire offers it once, per identity:

| `Bowire:MultiTenant:Migration` | |
|---|---|
| `Prompt` *(default)* | Offer the copy and let the person decide. The first identity to sign in is not reliably the one the data belongs to — it might be the operator's admin account. |
| `Auto` | Copy into the first identity that signs in. For installs the operator knows have one person. |
| `Skip` | Never offer. For starting clean, and for an operator who already moved the data by hand. |

Three things are worth knowing about how it runs:

- **It copies; it never moves.** The legacy files stay put. That costs disk and buys a switch back to single-user without a second migration, plus a way out if the data lands in the wrong slot — decline it in the right one. Deleting the originals is the operator's call to time.
- **Everything comes along except what is named as not personal** — `plugins`, `certs`, `logs`, `cache`, `state`, `project.json`, the provisioned user list in `scim`, and `users` itself. An inclusion list would have to grow with every new store, and forgetting one would be silent data loss; forgetting to exclude one merely copies a cache.
- **A slot that already holds work is left alone.** Merging two sets of environments produces one set nobody can take apart again.

The decision is recorded in `.migration.json` inside the slot — what was copied, from where, and when. It lives there rather than in a central log so that deleting an identity deletes its receipt too: an index of people who used to exist is not state Bowire should keep on its own initiative.

The copy is staged next to the slot and moved into place as the last step, so a migration that fails halfway leaves no slot at all rather than half a one. If the process is killed mid-copy, a `users/.staging-…` directory is left behind; it is safe to delete, and nothing reads it.

A decision can be reversed from Settings → Data, or with `bowire users migrate <subject> --undo`. Undoing an acceptance moves that slot to `users/.undone-…` rather than deleting it — the receipt records counts, not a manifest, so there is no way to tell which files came from the migration and which the person made afterwards, and the safe answer is to destroy nothing. Neither `.staging-…` nor `.undone-…` is a slot: both are dot-prefixed, and a slug never is.

## Redirecting everything (tests)

`BOWIRE_DATA_DIR` points every scope at one directory:

```bash
BOWIRE_DATA_DIR=/tmp/bowire-fixture bowire test ./suite.json
```

A fixture can then create one tree and delete one tree, rather than hunting for state beside whichever output directory the run happened to use. It is read in exactly one place, which is what makes that promise hold for *everything* rather than for the stores that remembered to check.

## For contributors

Never build a storage path by hand. `Path.Combine(GetFolderPath(UserProfile), ".bowire", …)` was written in fourteen files across six assemblies before #616, and each of those was a place the project opt-in, the machine scope and the instance segment did not reach.

Take the resolver as a dependency:

```csharp
public sealed class MyStore(IBowirePathResolver paths)
{
    private string File => paths.Resolve(BowireStorageScope.Data, "my-store.json");
}
```

For a static member that runs before any container exists, use the facade:

```csharp
public static string DefaultDirectory => BowirePaths.Resolve(BowireStorageScope.Data, "plugins");
```

Two things are worth knowing:

- **Prefer a property over a static field.** `BowireStorageRoot.Apply()` runs when the host is built, which can be *after* a type is first touched. A field captured before that pins the pre-`Apply` path and silently ignores a project that opted into `.bowire/` storage.
- **Do not add a guard for a missing home directory.** The resolver returns a usable absolute root when the platform reports no user profile — a locked-down service account, a scratch container. Call sites used to carry their own guards, and they disagreed: two fell back to a temp directory and one returned an empty string, which turned into a store that never persisted anything and never said so.

## Related

- [File formats](file-formats.md) — what is *in* those files
- [VS Code integration](../integrations/vscode.md#storage) — how the extension participates
