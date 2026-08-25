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
- **A name Bowire already uses for a directory** — `plugins`, `workspaces`, `recordings`, `flows`, `cache`, `certs`, `logs`, `presets`, `mocks`. With no instance set the root *is* the scope, so an instance named `plugins` would share state with an unnamed one, which is the opposite of what setting it was for.

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
