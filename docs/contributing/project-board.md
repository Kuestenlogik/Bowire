---
uid: contributing.project-board
title: Bowire Project Board
---

# Bowire Project Board

The roadmap, in-flight work, and bug triage all live on the [Bowire Project board](https://github.com/orgs/Kuestenlogik/projects/2). This page explains the fields and views — it doesn't replace `ROADMAP.md`, which still carries the narrative description of each track.

## Fields

Concrete values live on the [Project board's field configuration](https://github.com/orgs/Kuestenlogik/projects/2/settings/fields) — what's documented here is what each field is **for**, not which options happen to exist today. The values drift over time (new areas added, new tracks spun up, finished tracks closed); the purpose stays the same.

| Field | Used for |
|---|---|
| **Status** | Kanban swim-lane: `Backlog` → `Next up` → `In progress` → `In review` → `Done`. The only field whose values are pinned by convention; everything else is editable. |
| **Milestone** *(built-in)* | Version-targeting — the same Milestone the GitHub issue carries. Unset = unscheduled / no concrete release yet (rendered as "Backlog (not yet scheduled)" in `ROADMAP.md`). |
| **Area** | Which component an issue belongs to (the workbench UI, the CLI, the security surface, …). Use it as the *primary* axis for "show me everything affecting X". Stable enough that the value list barely changes between releases. |
| **Track** | Groups a multi-release initiative that spans several milestones. Use when an issue is part of a long-running theme — examples that have lived as tracks: the auth-provider rebuild, the protocol-plugin wave, the security-tier ladder, the AI integration. Leave blank when the issue is one-shot. New tracks get added when a new long-running theme starts; tracks close when the theme ships. |
| **Effort** | T-shirt estimate. Used to spot oversized issues (XL = split it before starting) and to right-size milestones. Not a commitment, just a sanity check. |
| **Start date** | When work actively began (typically set on the `Next up` → `In progress` transition). Drives the Roadmap layout's left edge. |
| **Target date** | Soft commitment date. The Milestone is the hard one; Target date is the "we'd like it by" that drives the Roadmap layout's right edge. |

> Priority and Kind used to be Project fields. Both retired: **Priority** wasn't pulling its weight (the Milestone + Status combination already answered "should we do this now") and **Kind** is carried on the issue as a `kind:*` label, no need for a duplicate field on the board. See the [Labels](#labels) section below for the kind taxonomy.

## Labels

Labels live on the GitHub issue itself (not on the Project board). They're the searchable side of the same information the Project fields carry, so `is:open label:area:security` works from the standard issue list without having to crack open the Project view. The full label list lives at [github.com/Kuestenlogik/Bowire/labels](https://github.com/Kuestenlogik/Bowire/labels) — the namespaces explained below are stable; concrete values come and go.

| Label namespace | Purpose |
|---|---|
| **`area:*`** | Mirror of the Project's `Area` field — same purpose, but searchable from `gh issue list` without the Projects API. Use one. |
| **`track:*`** | Mirror of the Project's `Track` field. Issues without a track don't get a `track:*` label — there's no `track:none`. |
| **`kind:*`** | What kind of work, where `bug` / `feature` is the default unspoken kind that doesn't need a label. Use `kind:concept` for ADR / design discussion, `kind:debt` for refactor / cleanup, `kind:docs` for documentation work. `kind:rfc` is retired — use `kind:concept`, both meant the same thing. |
| **`roadmap`** | Marks an issue as tracked on the Project board. Throwaway bug reports don't need it. |
| **`community-vote`** | Feature requests where reactions are read as priority signal. Don't comment "+1" — react with 👍. |

## Recommended views

The board ships with the default *All items* view. The four views below are the ones we keep returning to — they need ~30 seconds each to configure in the UI (clone the default view, change layout + grouping):

### 🗺 Roadmap

- **Layout**: Roadmap
- **Group by**: `Milestone`
- **Filter**: `Status` ≠ `Done`
- **Use for**: "What is targeted for the next few releases?" — the public-facing release plan

### 📋 Board

- **Layout**: Board
- **Group by**: `Status`
- **Filter**: `Milestone` = current (the milestone we're actively shipping)
- **Use for**: Operational kanban — what's currently moving

### 🧩 By Area

- **Layout**: Board
- **Group by**: `Area`
- **Filter**: `Status` ≠ `Done`
- **Use for**: Drill-down per component ("show me everything `security`")

### 🐛 Bugs

- **Layout**: Table
- **Filter**: `label:kind:bug` (labels are queryable in the Project filter)
- **Sort by**: `Status` ↑ then `Updated` ↓
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
- Milestones are managed in [Settings → Issues → Milestones](https://github.com/Kuestenlogik/Bowire/milestones). When a milestone closes, its issues move out of the `Roadmap` view automatically and the milestone drops out of `ROADMAP.md` (whose changelog moves to GitHub Releases).

### Milestone title = release theme

Every milestone's **title** carries the release headline directly: `vX.Y[.Z] — <theme>`. The theme is the same one that lands on the GitHub Release once the milestone tags, and it shows in the Project board's Roadmap view as the group heading (since Projects v2 reads the milestone title verbatim).

Current milestones (as of v2.0 RC prep):
- `v2.0 — Re-architected workbench shell + workspace = project folder`
- `v2.1 — Scripting, variable resolver, throughput surface`
- `v2.2 — Test pillar: assertions, CI runner, regression coverage`
- `v2.3 — Security pillar: shift-left scanner, OWASP coverage, auth recording`
- `v2.4 — Dev pillar: schema watch diff, mock-from-schema, side-by-side`
- `v2.5 — Continuous integration: PR bot, project file, org dashboard`

**One concept per release.** Themes are 2-5 words, concrete enough that a reader knows what the cycle is about (`gRPC Connect` beats `protocol expansion`). Bundling two themes with `+` is allowed if both are equally weighted (v2.0 carries the shell refactor AND the workspace-as-project-folder pivot, both major) — but the default is one theme so the cycle has an obvious anchor.

**Why pre-commit a theme at planning time:** the headline defines what the cycle is *about* — what we'd be embarrassed to ship without. It anchors the milestone discussion ("does this issue serve the theme?"), avoids the retrospective scramble of summarising whatever happened to land, and gives the team a one-line elevator pitch through the cycle. Mid-cycle pivots are fine — rename the milestone (GitHub keeps the audit trail).

**Mechanical consequences:**
- `release.yml` parses the matching milestone's title when creating the GitHub Release and uses the `<theme>` tail as `vX.Y.Z — <theme>`. No hand-editing of the release title required.
- `scripts/generate-roadmap.mjs` renders the full title as the section heading in `ROADMAP.md` so the offline view matches the Project board.
- The milestone description stays free-form for slip context, stakeholder hints, &c. — no machinery parses it.
- If the milestone title is bare (`v2.0` with no ` — <theme>` tail), the release falls back to a bare `vX.Y.Z` title and the roadmap section shows no theme — so missing themes are visible by their absence rather than crashing the pipeline.

**CLI ergonomics caveat:** `gh issue list --milestone v2.0` no longer matches when the milestone is renamed to `v2.0 — <theme>` — `gh` matches the full title verbatim. Either use the full title, or look up by milestone number (`--milestone <N>`).

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
