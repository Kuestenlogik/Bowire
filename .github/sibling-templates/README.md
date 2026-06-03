# Sibling cascade templates

These two workflow files together implement the **Bowire release cascade**: when the main Bowire repo tags a new release, every sibling that consumes `Kuestenlogik.Bowire*` NuGets gets its dependency bumped (PR), tested (CI), and auto-tags its own next patch version on merge.

| File | Purpose | Drop at |
|---|---|---|
| `bowire-released.yml` | Listens for `repository_dispatch: bowire-released`, bumps `Kuestenlogik.Bowire*` PackageVersion entries, opens PR | `.github/workflows/bowire-released.yml` |
| `auto-tag-on-bowire-merge.yml` | On merge of `bowire-cascade`-labelled PRs, patch-bumps the sibling's own version and pushes the tag | `.github/workflows/auto-tag-on-bowire-merge.yml` |

## Wiring

1. **Bowire main repo** — already wired: `release.yml` discovers cascade siblings dynamically by querying the `Kuestenlogik` org for repos carrying the `bowire-cascade` GitHub topic, then fans `repository_dispatch` to each after a successful `nuget.org` push.
2. **Each sibling** — three things, all in the sibling repo:
   - Drop the two workflow files above into `.github/workflows/`. No per-repo edits needed; both are sibling-agnostic.
   - Add the GitHub topic `bowire-cascade` via the repo's **About → ⚙ → Topics**. This is the opt-in marker — without it, Bowire's release.yml won't dispatch to this repo.
   - Make sure `Directory.Packages.props` exists at the repo root with the `Kuestenlogik.Bowire*` `<PackageVersion>` entries — the bump step's `sed` operates on that file.
3. **Secrets** — both files use the org-secret `BOWIRE_DISPATCH_TOKEN` (Contents R/W + Pull requests R/W). Already in place from the consolidation step.
4. **Auto-merge** — handled by the sibling's existing `dependabot-auto-merge.yml`; the cascade PRs are labelled `dependencies` (matching that workflow's filter) and `bowire-cascade` (so the auto-tag step can identify them).

### Adding a new sibling later

Open the new repo on GitHub → **About** (right column on the repo's main page) → ⚙ → **Topics** → add `bowire-cascade` → save. Drop in the two workflow files. Done — no PR against Bowire main needed.

## Versioning model

Async: each sibling bumps its **own** patch version, independent of which Bowire version triggered the cascade. So Bowire v1.7.0 might trigger `Bowire.Protocol.Kafka` from v1.0.4 → v1.0.5. The cascade doesn't enforce Bowire-sibling version parity.

If a sibling needs a minor or major bump for unrelated reasons (new feature, breaking change), tag that one by hand. The auto-tag workflow only fires on `bowire-cascade`-labelled merges; it stays out of your way otherwise.

## Manual test

Each sibling's `bowire-released.yml` also accepts `workflow_dispatch` with a `version` input — useful to dry-run the cascade without retagging Bowire main.

```bash
gh workflow run bowire-released.yml -R Kuestenlogik/Bowire.Protocol.Kafka -f version=1.7.0
```
