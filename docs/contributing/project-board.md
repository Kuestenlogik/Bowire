---
uid: contributing.project-board
title: Bowire Project Board
---

# Bowire Project Board

The roadmap, in-flight work, and bug triage all live on the [Bowire Project board](https://github.com/orgs/Kuestenlogik/projects/2). This page explains the fields and views — it doesn't replace `ROADMAP.md`, which still carries the narrative description of each track.

## Fields

Concrete values live on the [Project board's field configuration](https://github.com/orgs/Kuestenlogik/projects/2/settings/fields) — what's documented here is what each field is **for**, not which options happen to exist today. The values drift over time (new areas added as the product grows); the purpose stays the same.

| Field | Used for |
|---|---|
| **Status** | Kanban swim-lane: `Backlog` → `Next up` → `In progress` → `In review` → `Done`. The only field whose values are pinned by convention; everything else is editable. |
| **Release** *(single-select)* | The **cross-repo grouping** the roadmap is bucketed by ([`generate-roadmap.mjs`](../../scripts/ci/generate-roadmap.mjs)) — `vX.Y`, the **product** release the work targets, or empty for work not yet assigned to one. It spans **every repo** on the board, which a native (repo-scoped) `Milestone` can't: a sibling issue (Akka, Dis, …) rides the same product version as main-repo work, even though that repo tags its *own* patch version autonomously via the release cascade. It's a grouping key — it has **no lifecycle** (see `Milestone` for open/closed), and it is **independent of `Status`**: an item can target `v2.6` and still rest in `Backlog`. Required once the item leaves `Backlog` — see [Release is mandatory](#release-is-mandatory). |
| **Milestone** *(built-in)* | The per-repo native milestone. On the **main repo** it is the product release's **lifecycle anchor + definition**: `open` = planned, **`closed` = shipped**, plus the version's *theme + due date*. Closing the main-repo `vX.Y` milestone drops the **whole cross-repo version** (main + siblings, grouped via the field) off the roadmap — one close ships the lot. The generator also reads it as a *fallback* version for any item missing a Release. Siblings need no native milestone (their version lives in the field); add one only where a repo genuinely wants its own native milestone view. |
| **Area** *(single-select, **mandatory**)* | Which component an issue belongs to (`workbench`, `cli`, `security`, `mcp`, `plugin-sdk`, `mock`, `docs`, `site`, `bootcamp`, `multi`). The *primary* axis for "show me everything affecting X" — should be set on every item (use `multi` only for genuinely cross-cutting work). Replaced the old `Track` field, which overlapped with it. |
| **Effort** *(actual)* | `Low` / `Medium` / `High`. Same scale as the org-level `Issue.Effort` so the Project mirror carries the *actual* size of the work next to the *plan*. Used to spot oversized issues (`High` = consider splitting before starting) and to right-size milestones. Not a commitment, just a sanity check. |
| **Start date** *(actual)* | First commit referencing `#N`. Backfilled by the roadmap-sync job from git history. Drives the Roadmap layout's left edge. |
| **Target date** *(actual)* | Issue's `closedAt`. Backfilled by the roadmap-sync job. Drives the Roadmap layout's right edge. |

> **Priority** lives on the org-level **Issue** field (`Urgent` / `High` / `Medium` / `Low`), not on the Project board — it travels with the issue across every project that picks it up. Set it on the issue itself (right sidebar → Fields → Priority).
>
> **Kind** is the native GitHub issue **Type** (`Bug` / `Feature` / `Task`), set on the issue itself — not a label or a Project field. (The old `kind:*` labels were retired in favour of issue Types.)
>
> The `Start date`, `Target date`, and `Effort` Project fields are **mirrored** by org-level Issue fields with the *same scale*. Issue-layer carries the **plan** (estimate / planned start / planned ship); Project-layer carries the **actual** (when work began, when it shipped, what size it turned out to be). Plan-vs-actual divergence is visible per issue.

## Labels

Labels live on the GitHub issue itself (not on the Project board). They're the searchable side of the same information the Project fields carry, so `is:open label:area:security` works from the standard issue list without having to crack open the Project view. The full label list lives at [github.com/Kuestenlogik/Bowire/labels](https://github.com/Kuestenlogik/Bowire/labels) — the namespaces explained below are stable; concrete values come and go.

| Label namespace | Purpose |
|---|---|
| **`roadmap`** | Marks an issue as tracked on the Project board. Throwaway bug reports don't need it. |
| **`community-vote`** | Feature requests where reactions are read as priority signal. Don't comment "+1" — react with 👍. |

## Recommended views

The board ships with the default *All items* view. The four views below are the ones we keep returning to — they need ~30 seconds each to configure in the UI (clone the default view, change layout + grouping):

### 🗺 Roadmap

- **Layout**: Roadmap
- **Group by**: `Release` — **not** the built-in `Milestone`. Grouping by the native `Milestone` puts every sibling issue under *No milestone* (siblings carry no native milestone by design — their version lives in the `Release` field); only `Release` groups main + siblings together.
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
- **Filter**: issue `Type` = `Bug` (the native GitHub issue type)
- **Sort by**: `Status` ↑ then `Updated` ↓
- **Use for**: Triage backlog, regardless of milestone

## Conventions

- **One field per concept**: `Status` is the *where in the flow*, `Release` is the *when* (which product version), `Area` is the *component*, the issue **Type** is the *kind* (Bug / Feature / Task). `Status` and `Release` are independent axes — an item can target `v2.6` and still rest in `Backlog`. `Area` is mandatory from the start; `Release` becomes mandatory when the item is pulled out of `Backlog`. (The old `Track` field was dropped — it overlapped with `Area`.)
- **Labels duplicate fields on purpose**: GitHub issue search needs labels (`is:open label:area:security`). Project filters need fields. The two are kept in sync so an issue is findable from either side.
- **`roadmap` label** flags items that are tracked on the board. Throwaway bug reports don't need it.
- **`community-vote` label** marks feature requests where reactions on the issue are read as priority signal. Don't comment "+1" — react with 👍.
- **PRs close issues via `Closes #N`** so Status flips to `Done` automatically and the item drops off the active views.

## Maintenance

- New issue created via *Convert from Markdown* (in the issue editor) or *Create issue* — the board adds it as `Backlog` by default.
- Status transitions: `Backlog` → `Next up` → `In progress` → `In review` → `Done`. The last two are driven by PR state where possible.
- Milestones are managed in [Settings → Issues → Milestones](https://github.com/Kuestenlogik/Bowire/milestones). When a milestone closes, its issues move out of the `Roadmap` view automatically and the milestone drops out of `ROADMAP.md` (whose changelog moves to GitHub Releases).

### Release is required once an item is pulled

`Release` is the **cross-repo grouping** the roadmap is bucketed by ([`generate-roadmap.mjs`](../../scripts/ci/generate-roadmap.mjs)). Empty means *not assigned to a version yet*, which is the correct and expected state for anything still resting in `Backlog` — those items render in the unscheduled bucket rather than disappearing.

What is **not** acceptable is an item in flight with no version: `Next up`, `In progress` or `In review` while `Release` is empty means the decision about when it ships was skipped.

**Why a field and not a milestone.** A GitHub milestone is **repo-scoped** — it can only hold issues from one repo. The product spans repos (main + siblings), so the cross-repo "which product release is this planned for?" grouping *can't* be a native milestone; the idiomatic Projects v2 answer is a board field. `Release` carries the **product** version — `v2.6`, `v2.7`, … — or `Backlog` when unscheduled. A sibling issue (Akka, Dis, Surgewave, Samples) gets the **Bowire product version** it rides, even though that repo tags its **own** independent version autonomously via the [release cascade](../../.github/workflows/release.yml). Version divergence between repos is expected (different NuGets/plugins, third-party rhythm, security patches) and doesn't matter here — the field is the shared product axis, the repo tag is the artifact version.

**Two axes, kept apart:**
- **`Release` (field)** → planning / roadmap grouping. Spans repos. *No lifecycle* — it's a grouping key.
- **`Milestone` (native, main repo)** → the product release's **lifecycle**: `open` = planned, **`closed` = shipped**, plus theme + due date. **Close the main-repo `vX.Y` milestone to ship the whole cross-repo version** — the generator renders only versions whose milestone is still open, so closing it drops that version (main + siblings) off the roadmap in one action. That's how you "close" a product milestone even though the field itself can't be closed.
- **Repo tag / NuGet version** → the artifact version. The **release scripts use the repo's own version** for packaging + release notes — never the `Release` field.

**Required once an item is pulled**, not before — enforced by [`roadmap-field-guard.yml`](../../.github/workflows/roadmap-field-guard.yml), a daily (and on-demand) check that **fails** for any *open* item whose `Status` has left `Backlog` while its `Release` is still empty. A failing run also files a tracking issue, because a red scheduled run on its own is not a notification.

**When you create an issue/ticket:** leave `Release` empty until you know. Set it when you pull the item to `Next up` — pulling *is* the decision about when it ships.

> **Why not "always set"?** This used to demand a non-empty `Release` at all times, which forced a `Backlog` option onto the field. That was a category error: a release is a version, and "backlog" is a place in the flow — which is what `Status` already says. It also protected nothing (`generate-roadmap.mjs` treated the placeholder and an empty field identically) and made the guard vacuous, since seeding meant nothing was ever empty to catch. The rule now guards the transition, which is the moment the decision is actually due.

**Grouping caveat:** group the board by `Release`, **not** the native `Milestone` — the latter shows every sibling under *No milestone* (siblings carry no native milestone; their version is in the field).

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
- `scripts/ci/generate-roadmap.mjs` renders the full title as the section heading in `ROADMAP.md` so the offline view matches the Project board.
- The milestone description stays free-form for slip context, stakeholder hints, &c. — no machinery parses it.
- If the milestone title is bare (`v2.0` with no ` — <theme>` tail), the release falls back to a bare `vX.Y.Z` title and the roadmap section shows no theme — so missing themes are visible by their absence rather than crashing the pipeline.

**CLI ergonomics caveat:** `gh issue list --milestone v2.0` no longer matches when the milestone is renamed to `v2.0 — <theme>` — `gh` matches the full title verbatim. Either use the full title, or look up by milestone number (`--milestone <N>`).

## Automation

The roadmap is wired to maintain itself once an issue lands with `label:roadmap`:

| Event | What happens |
|---|---|
| New issue with `roadmap` label | The Project's own **Auto-add to project** rule attaches it (Status defaults to `Backlog`, Release stays empty until triage) |
| Issue closed | `roadmap-sync.yml` regenerates `ROADMAP.md` from the Project + commits |
| Issue title / label / milestone change | same — `roadmap-sync.yml` re-renders |
| PR merged that uses `Closes #N` | Status flips to `Done` via Project workflow (UI-side, see below) |
| Daily 05:23 UTC | Safety-net `roadmap-sync.yml` cron |

### One-time setup (single PAT, org-secret)

`roadmap-field-guard.yml`, `roadmap-sync.yml` and `Bowire.Bootcamp/notify-bowire.yml` share **one** organization secret `BOWIRE_DISPATCH_TOKEN`. The default `GITHUB_TOKEN` can't write to org-level Projects nor dispatch into sibling repos, so a PAT is required either way — but only one.

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
