# Sibling cascade templates

These three workflow files together implement the **Bowire release cascade**: when the main Bowire repo tags a new release, every sibling that consumes `Kuestenlogik.Bowire*` NuGets gets its dependency bumped (PR), tested (CI), auto-tags its own next patch version on merge, and — once that tag's release pipeline succeeds — raises its own `Directory.Build.props` version floor to `<released>-dev`.

| File | Purpose | Drop at |
|---|---|---|
| `bowire-released.yml` | Listens for `repository_dispatch: bowire-released`, bumps the `Kuestenlogik.Bowire*` packages that release published, opens PR | `.github/workflows/bowire-released.yml` |
| `auto-tag-on-bowire-merge.yml` | On merge of `bowire-cascade`-labelled PRs, patch-bumps the sibling's own version and pushes the tag | `.github/workflows/auto-tag-on-bowire-merge.yml` |
| `post-release-floor-bump.yml` | On a successful `Release` run (tag push), raises the sibling's own `Directory.Build.props` `<Version>`/`<AssemblyVersion>`/`<FileVersion>` floor to the just-released version + `-dev` via an auto-merging PR | `.github/workflows/post-release-floor-bump.yml` |

## Wiring

1. **Bowire main repo** — already wired: `release.yml` discovers cascade siblings dynamically by querying the `Kuestenlogik` org for repos carrying the `bowire-cascade` GitHub topic, then fans `repository_dispatch` to each after a successful `nuget.org` push.
2. **Each sibling** — three things, all in the sibling repo:
   - Drop the three workflow files above into `.github/workflows/`. No per-repo edits needed; all three are sibling-agnostic.
   - Add the GitHub topic `bowire-cascade` via the repo's **About → ⚙ → Topics**. This is the opt-in marker — without it, Bowire's release.yml won't dispatch to this repo.
   - Nothing else. The bump walks every `*.csproj` and every `Directory.Packages.props` in the repo (`bin/`, `obj/` excluded), so CPM repos, per-project `PackageReference` repos, and mixtures all work without configuration.
3. **Secrets** — both files use the org-secret `BOWIRE_DISPATCH_TOKEN` (Contents R/W + Pull requests R/W). Already in place from the consolidation step.
4. **Auto-merge** — handled by the sibling's existing `dependabot-auto-merge.yml`; the cascade PRs are labelled `dependencies` (matching that workflow's filter) and `bowire-cascade` (so the auto-tag step can identify them).

### Adding a new sibling later

Open the new repo on GitHub → **About** (right column on the repo's main page) → ⚙ → **Topics** → add `bowire-cascade` → save. Drop in the two workflow files. Done — no PR against Bowire main needed.

## Which packages get bumped

The dispatch carries the exact list of package ids the release pushed to nuget.org, derived from the `.nupkg` filenames themselves (`release.yml`, step *Collect published package ids*). The sibling bumps those ids and nothing else.

Prefix matching cannot do this job, in either direction:

- Too narrow, which is what shipped until #548: the regex allowed one dot-segment past `Kuestenlogik.Bowire`, so `Kuestenlogik.Bowire.Interceptor` moved and `Kuestenlogik.Bowire.Protocol.Mcp` did not. Bowire.Samples sat three minors behind on five protocol packages for months, and the PRs merged green because nothing checked.
- Too wide: `Kuestenlogik.Bowire.Protocol.Amqp` is owned by the Bowire.Protocol.Amqp sibling on its own version line (0.2.1 while main is 2.3.0). Bumping it to the main repo's version pins something that was never published, and the restore fails with `NU1102`.

The question is never *how many dots* — it is *did this release publish that id*.

Two shapes are deliberately left alone, and the guard that protects them is the `Version="[0-9]…"` match: values that do not start with a digit are not versions. That covers `dotnet new` template parameters (Bowire.Templates ships `Version="MY_BOWIRE_VERSION"`) and MSBuild indirections like `Version="$(BowireVersion)"`.

## Template parameters

Skipping the placeholder is only half of Bowire.Templates' story. Its plugin template holds the Bowire version twice — as that placeholder in `Directory.Packages.props`, and as the `defaultValue` of the `template.json` symbol whose `replaces` is the placeholder. The bump step correctly refuses the first; nothing resolved the second, so the default sat at `1.6.0` from May while the same template's non-CPM `.csproj`, which the bump step *does* reach, tracked every release to `2.6.2`. `dotnet new bowire-plugin` with no `--BowireSdkVersion` generated a plugin against a Bowire five minors old, and every cascade run was green.

The **Resolve dotnet-new template version parameters** step closes that hop. For each `*/.template.config/template.json` it collects the non-numeric `Version="…"` tokens that a *published* id carries inside that same template directory, finds the symbol whose `replaces` is one of them, and rewrites its `defaultValue`. Same question as the bump step — *did this release publish that id* — asked one indirection further in. A template naming no Bowire id is untouched, which is every sibling but this one.

A default that is not a concrete version is left alone: `2.*` or `[2.0,3.0)` is a deliberate NuGet range (this template enables `CentralPackageFloatingVersionsEnabled` precisely so an operator can choose one), and overwriting it with a pin would silently change what a generated project resolves.

## The postcondition

A final step asserts it: every published id this repo pins at a numeric version, **and every template default behind one of its placeholders**, must now read the new version, or the job fails. Half-done silently is the failure this cascade already shipped twice — once as the one-dot-segment regex, once as the unresolved template default.

The logic is covered by `tests/Kuestenlogik.Bowire.Tests/ci/cascade-bump.test.mjs` in the main repo, which extracts these step scripts out of the YAML and runs them against fixtures of every sibling's real reference shapes — so edit the template, run `npm run test:ci-workflows`, then propagate.

## Versioning model

Async: each sibling bumps its **own** patch version, independent of which Bowire version triggered the cascade. So Bowire v1.7.0 might trigger `Bowire.Protocol.Kafka` from v1.0.4 → v1.0.5. The cascade doesn't enforce Bowire-sibling version parity.

If a sibling needs a minor or major bump for unrelated reasons (new feature, breaking change), tag that one by hand. The auto-tag workflow only fires on `bowire-cascade`-labelled merges; it stays out of your way otherwise.

## Manual test

Each sibling's `bowire-released.yml` also accepts `workflow_dispatch` with a `version` input — useful to dry-run the cascade without retagging Bowire main.

```bash
gh workflow run bowire-released.yml -R Kuestenlogik/Bowire.Protocol.Kafka -f version=1.7.0
```

With no `packages` input the workflow resolves the id list itself: it takes every `Kuestenlogik.Bowire*` id the repo references and keeps the ones nuget.org actually has at that version. That is the same guarantee the dispatch payload gives, so a manual run bumps the same set a real cascade would. Pass `-f packages=Kuestenlogik.Bowire,Kuestenlogik.Bowire.Protocol.Rest` to pin the list explicitly instead.
