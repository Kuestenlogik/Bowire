---
uid: contributing.multi-repo-project-add
title: Linking sibling Bowire.* repos to the Project board
---

# Linking sibling Bowire.* repos to the Project board

The [Bowire Project board](https://github.com/orgs/Kuestenlogik/projects/2) is org-level and can hold issues from any repo in the `Kuestenlogik` org. Every issue of every product repository belongs on it — `Bowire.Bootcamp`, `Bowire.Templates`, `Bowire.VulnDb`, `Bowire.Protocol.*`, `Bowire.Sdk.*`, …; there is no opt-in label (the `roadmap` label was retired on 2026-09-30).

**Nothing has to be wired for that.** The daily [field guard](../../.github/workflows/roadmap-field-guard.yml) sweeps every repository listed in [`release-plan.mjs`](../../scripts/ci/release-plan.mjs) (`PRODUCT_OF_REPO`) and adds any open issue that is missing, with its repository's product. A new product repository is one line in that map. The options below only make the add immediate instead of daily.

## Option A — Project workflow "Auto-add to project"

Configured **inside the Project**, no per-repo workflow file. Recommended for the simple case.

1. Open the project: <https://github.com/orgs/Kuestenlogik/projects/2>
2. **⚙ Settings** (top right) → **Workflows**
3. **Auto-add to project** → **Edit**
4. Add one rule per repo with this filter:
   ```
   repo:Kuestenlogik/Bowire.Bootcamp is:issue
   ```
   No label clause: every issue belongs on the board.
5. **Save and turn on workflow**.

Trigger fires on every *new* issue matching the filter. Existing issues in the sibling repo need a one-shot backfill — see Option C below.

## Option B — Per-repo workflow file (more granular)

When you want it repo-side, drop this into the sibling repo (the Bowire repository's own is [`project-add.yml`](../../.github/workflows/project-add.yml), which also sets the Product):

```yaml
# .github/workflows/project-add.yml
name: Add to Bowire Project

on:
  issues:
    types: [opened, reopened, transferred]

permissions:
  contents: read

jobs:
  add-to-project:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/add-to-project@v1
        with:
          project-url: https://github.com/orgs/Kuestenlogik/projects/2
          github-token: ${{ secrets.PROJECT_ADD_TOKEN }}
```

`PROJECT_ADD_TOKEN` needs `project` + `repo` scope on `Kuestenlogik/Bowire`. Either:
- Create a single PAT and store it as a *repository secret* in every sibling repo, or
- Create one *organization secret* `PROJECT_ADD_TOKEN` and grant it to all `Bowire.*` repos (Settings → Secrets → Actions → "Organization secrets").

## Option C — One-shot backfill of existing issues

For issues that already exist in a sibling repo before either workflow is in place, attach them in one go:

```bash
# Replace <REPO> with the sibling, e.g. Bowire.Bootcamp
for url in $(gh issue list --repo Kuestenlogik/<REPO> --state open --limit 500 --json url --jq '.[].url'); do
    gh project item-add 2 --owner Kuestenlogik --url "$url"
done
```

Run once per repo. Idempotent — items already on the board are silently skipped.

## What the generated `ROADMAP.md` shows

`scripts/ci/generate-roadmap.mjs` already renders multi-repo entries with the repo prefix:

- Local repo issues appear as `#42`
- Sibling-repo issues appear as `Kuestenlogik/Bowire.Bootcamp#42`

So once a sibling-repo issue lands on the board, the auto-generated `ROADMAP.md` picks it up on the next sync (the [`Roadmap sync`](../../.github/workflows/roadmap-sync.yml) workflow fires on every `issues` and `project_v2` event, plus a daily safety-net cron).
