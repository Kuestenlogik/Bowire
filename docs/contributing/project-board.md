---
uid: contributing.project-board
title: Bowire Project Board
---

# Bowire Project Board

The roadmap, in-flight work, and bug triage all live on the [Bowire Project board](https://github.com/orgs/Kuestenlogik/projects/2). This page explains the fields and views — it doesn't replace `ROADMAP.md`, which still carries the narrative description of each track.

## Fields

| Field | Values | Used for |
|---|---|---|
| **Status** | `Backlog` · `Next up` · `In progress` · `In review` · `Done` | Kanban swim-lane |
| **Milestone** *(built-in)* | `v1.5` · `v1.6` · `v2.0` · `Later` | Version-targeting — the same Milestone the GitHub issue carries |
| **Area** | `workbench` · `cli` · `security` · `mcp` · `plugin-sdk` · `mock` · `docs` · `site` · `bootcamp` · `multi` | Component filter |
| **Track** | `auth` · `protocols` · `marketing-ia` · `observability` · `security-tiers` · `bootcamp-content` · `none` | Multi-phase initiatives that span releases |
| **Effort** | `XS` · `S` · `M` · `L` · `XL` | T-shirt estimate |
| **Priority** | `P0` · `P1` · `P2` · `P3` | Triage |
| **Kind** | `feature` · `bug` · `debt` · `docs` · `rfc` | What it is |

## Recommended views

The board ships with the default *All items* view. The four views below mirror how Akka.NET's [v1.6 project](https://github.com/orgs/akkadotnet/projects/11/views/1) is structured — they need ~30 seconds each to configure in the UI (clone the default view, change layout + grouping):

### 🗺 Roadmap

- **Layout**: Roadmap
- **Group by**: `Milestone`
- **Filter**: `Status` ≠ `Done`
- **Use for**: "What is targeted for v1.6?" — the public-facing release plan

### 📋 Board

- **Layout**: Board
- **Group by**: `Status`
- **Filter**: `Milestone` = current (v1.5)
- **Use for**: Operational kanban — what's currently moving

### 🧩 By Area

- **Layout**: Board
- **Group by**: `Area`
- **Filter**: `Status` ≠ `Done`
- **Use for**: Drill-down per component ("show me everything `security`")

### 🐛 Bugs

- **Layout**: Table
- **Filter**: `Kind` = `bug`
- **Sort by**: `Priority` ↑ then `Status` ↑
- **Use for**: Triage backlog, regardless of milestone

## Conventions

- **One field per concept**: `Milestone` is the *when*, `Track` is the *grouped initiative across releases*, `Area` is the *component*. They overlap deliberately — Milestone is enforced (the bar for shipping), Track is editorial (Auth Phase A / B / C).
- **Labels duplicate fields on purpose**: GitHub issue search needs labels (`is:open label:area:security`). Project filters need fields. The two are kept in sync so an issue is findable from either side.
- **`roadmap` label** flags items that are tracked on the board. Throwaway bug reports don't need it.
- **`community-vote` label** marks feature requests where reactions on the issue are read as priority signal. Don't comment "+1" — react with 👍.
- **PRs close issues via `Closes #N`** so Status flips to `Done` automatically and the item drops off the active views.

## Maintenance

- New issue created via *Convert from Markdown* (in the issue editor) or *Create issue* — the board adds it as `Backlog` by default.
- Status transitions: `Backlog` → `Next up` → `In progress` → `In review` → `Done`. The last two are driven by PR state where possible.
- Milestones are managed in [Settings → Issues → Milestones](https://github.com/Kuestenlogik/Bowire/milestones). When a milestone closes, its issues move out of the `Roadmap` view automatically.

## Automation

The roadmap is wired to maintain itself once an issue lands with `label:roadmap`:

| Event | What happens |
|---|---|
| New issue with `roadmap` label | `.github/workflows/add-to-project.yml` attaches it to the Project (Status defaults to `Backlog`) |
| Issue closed | `roadmap-sync.yml` regenerates `ROADMAP.md` from the Project + commits |
| Issue title / label / milestone change | same — `roadmap-sync.yml` re-renders |
| PR merged that uses `Closes #N` | Status flips to `Done` via Project workflow (UI-side, see below) |
| Daily 05:23 UTC | Safety-net `roadmap-sync.yml` cron |

### One-time setup (single PAT, org-secret)

`add-to-project.yml` and `Bowire.Bootcamp/notify-bowire.yml` share **one** organization secret `BOWIRE_DISPATCH_TOKEN`. The default `GITHUB_TOKEN` can't write to org-level Projects nor dispatch into sibling repos, so a PAT is required either way — but only one.

1. Create a fine-grained PAT — Settings → Developer settings → Personal access tokens → Fine-grained.
   - Resource owner: `Kuestenlogik`
   - Repository access: `Kuestenlogik/Bowire` + all sibling Bowire.* repos (Bootcamp, Templates, VulnDb, Protocol.*, Sdk.*)
   - **Repository permissions**: `Contents: R/W`, `Issues: Read`, `Pull requests: Read`
   - **Organization permissions**: `Projects: Read and write`
2. Save as organization secret **`BOWIRE_DISPATCH_TOKEN`** in `Kuestenlogik` org settings → Secrets → Actions → New organization secret. Repository access: "Selected repositories" → tick every Bowire.* repo.
3. Both workflows pick it up automatically; nothing per-repo to configure.

### Project-side workflows (UI-only)

Configure once in the Project UI — these aren't exposed via API yet, so they live alongside the GitHub Action workflow files.

1. Open https://github.com/orgs/Kuestenlogik/projects/2 → **⚙ Settings** → **Workflows**
2. **Item closed** → enable → Set status to `Done`.
3. **Pull request merged** → enable → Set status to `Done`.
4. **Auto-add to project** → leave **disabled** for `Kuestenlogik/Bowire` (the GitHub Action above handles that with the label filter). For sibling Bowire.* repos that don't carry the workflow file, enable Auto-add with filter `repo:Kuestenlogik/<RepoName> label:roadmap is:issue,pr`.

Sibling-repo wiring options (Project-side vs Action-side vs back-fill) are documented separately in [`multi-repo-project-add.md`](multi-repo-project-add.md).

## Migration from `ROADMAP.md`

The pilot batch lives on the board now (6 items from *In progress* + *Next up*). `ROADMAP.md` is unchanged for the moment. Once the board's daily feel is validated, the plan is:

1. Migrate the rest of `ROADMAP.md` to issues, one per `###` heading.
2. Trim `ROADMAP.md` to a ~30-line index that points at the board and the **In progress** + **Next up** highlights, regenerated periodically from the board.
3. The marketing site's `roadmap.html` pulls from the Project's GraphQL API (cached as `site/_data/roadmap.yml` via a GitHub Action) so visitors see live data without the page making a network call.

Rollback plan: closing the issues and deleting the project + milestones restores the previous "ROADMAP.md is the only source" state. The labels stay — they're useful regardless.
