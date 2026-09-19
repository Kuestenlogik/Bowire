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
| **Release** *(single-select)* | The **version a ticket shipped in** — `v2.7`, … — stamped by the release pipeline (`scripts/ci/stamp-release-field.mjs`) on every item of the milestones the tag names, and **empty until then**: the version is chosen at the cut, not before (see [Milestones and releases](#milestones-and-releases)). It spans every repo on the board, so a sibling issue shows the product version it went out with. No lifecycle, independent of `Status`. |
| **Milestone** *(built-in)* | The per-repo native milestone. On the **main repo** it is the section's **lifecycle anchor + definition**: `M<n> — <theme>`, `open` = planned, **`closed` = shipped**, plus the section's scope statement and due date. Closing the main-repo milestone drops the **whole cross-repo section** (main + siblings, grouped via the field) off the roadmap — one close ships the lot. The generator also reads it as a *fallback* for any item missing a Release. A sibling repo carries a **mirroring milestone with the same title** (`M1 — …` open, `v2.7` closed once shipped), so every ticket in every repo has a native milestone and the field and the milestone say the same thing. |
| **Area** *(single-select, **mandatory**)* | Which component an issue belongs to (`workbench`, `cli`, `security`, `mcp`, `plugin-sdk`, `mock`, `docs`, `site`, `bootcamp`, `multi`). The *primary* axis for "show me everything affecting X" — should be set on every item (use `multi` only for genuinely cross-cutting work). Replaced the old `Track` field, which overlapped with it. |
| **Effort** *(actual)* | `Low` / `Medium` / `High`. Same scale as the org-level `Issue.Effort` so the Project mirror carries the *actual* size of the work next to the *plan*. Used to spot oversized issues (`High` = consider splitting before starting) and to right-size milestones. Not a commitment, just a sanity check. |
| **Start date** *(actual)* | First commit referencing `#N`. Backfilled by the roadmap-sync job from git history. Drives the Roadmap layout's left edge. |
| **Target date** *(actual)* | Issue's `closedAt`. Backfilled by the roadmap-sync job. Drives the Roadmap layout's right edge. |

> **Priority** lives on the org-level **Issue** field (`Urgent` / `High` / `Medium` / `Low`), not on the Project board — it travels with the issue across every project that picks it up. Set it on the issue itself (right sidebar → Fields → Priority).
>
> **Kind** is the native GitHub issue **Type** (`Bug` / `Feature` / `Task`), set on the issue itself — not a label or a Project field. (The old `kind:*` labels were retired in favour of issue Types.)
>
> The `Start date`, `Target date`, and `Effort` Project fields are **mirrored** by org-level Issue fields with the *same scale*. Issue-layer carries the **plan** (estimate / planned start / planned ship); Project-layer carries the **actual** (when work began, when it shipped, what size it turned out to be). Plan-vs-actual divergence is visible per issue.

## Milestones and releases

Since 2026-09-19 (rolled out from here to the other Küstenlogik repositories) milestones and release versions are decoupled:

- A **milestone is an ordered work section** — `M1 — Localisation, layout and the test pillar`, `M2 — MCP completion + agent hub`, … Its description is one short statement of the themes it contains and ends with the scope rule: *"Umfang festgelegt am <date>: ein Ticket gehört hierher, wenn es in eines dieser Themen fällt — sonst in den nächsten Abschnitt."* A section does not grow: a ticket outside its themes goes to the next section, or a new section is opened behind the last (`M8 — Benchmark …` was opened that way). Every ticket gets a milestone (the main repo's section, or its mirror in a sibling repo) when it is created; the [field guard](../../.github/workflows/roadmap-field-guard.yml) lists every open item without one.
- A **release gets its version number when it is cut**, by content (SemVer: a breaking cut bumps the major, a contract change the minor). A finished section *is* the release as a rule; a second section that finished at the same time may ride along. The tag names the sections it ships in its message — `git tag -a v2.8.0 -m "Bowire v2.8.0 — M1 — Localisation, layout and the test pillar"` — and [`scripts/ci/resolve-milestone.mjs`](../../scripts/ci/resolve-milestone.mjs) reads them for the release title and the drafted notes (falling back to a `v<base> — …` milestone for old tags, then to the frontmost complete section). After the cut: the milestone is closed with *"Ausgeliefert in v2.8.0"* and the pipeline stamps `Release = v2.8` on every item of the shipped milestones, so the history reads as versions and the generator drops the shipped section from the roadmap.
- **Release due** = the frontmost open milestone has no open ticket, CI is green, no pull request is open. `node scripts/ci/resolve-milestone.mjs v<next>` on a clean checkout says which section that is.

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
- **Group by**: `Milestone` (the section; the siblings mirror it as their own milestones). `Release` groups shipped work by version.
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

### Every ticket names its milestone; `Release` is stamped at the cut

The roadmap is bucketed by **milestone** ([`generate-roadmap.mjs`](../../scripts/ci/generate-roadmap.mjs)): the ordered work section a ticket belongs to (`M1 — …`, `M2 — …`). A GitHub milestone is repo-scoped, and the product spans repos (main + siblings) — so a sibling repo **mirrors** the main repo's sections as its own milestones with the same titles, and a sibling issue (Akka, Dis, Surgewave, Samples, the SDKs) carries the mirror of the section it rides. That is what lets every ticket in every repo have a milestone, and the board group by it without a *No milestone* bucket.

`Release` is the **product version a ticket shipped in** — `v2.6`, `v2.7`, … — stamped by the release pipeline on every item of the milestones the tag names ([`stamp-release-field.mjs`](../../scripts/ci/stamp-release-field.mjs)), and **empty until then**, because the version is chosen at the cut, not when the work is planned. A sibling issue shows the **Bowire product version** it went out with, even though that repo tags its own artifact version autonomously via the [release cascade](../../.github/workflows/release.yml); the two differ by design.

**Three axes, kept apart:**
- **`Milestone` (native, mirrored into siblings)** → the section: `open` = planned, **`closed` = shipped** (*"Ausgeliefert in vX.Y.Z"*), plus themes, scope rule and due date. Closing the main-repo milestone (and its mirrors) drops the section off the roadmap.
- **`Release` (field)** → the version a ticket shipped in, stamped at the cut; empty while planned. Spans repos. *No lifecycle*.
- **Repo tag / NuGet version** → the artifact version. The **release scripts use the repo's own version** for packaging + release notes — never the `Release` field.

**Enforced** by [`roadmap-field-guard.yml`](../../.github/workflows/roadmap-field-guard.yml), a daily (and on-demand) check that **fails** for any *open* item without a milestone and files a tracking issue, because a red scheduled run on its own is not a notification.

**When you create an issue/ticket:** give it its milestone — the section whose scope statement it falls into, otherwise the next section or a new one behind the last. Leave `Release` alone; the cut fills it.

> **Why the field is no longer the planning axis.** Until 2026-09-19 `Release` carried the planned product version (`v2.8`, …) because siblings had no milestone to carry it. Naming the version ahead of the cut is exactly what let "v0.4 done while v0.2 open" happen elsewhere in the organisation; with mirrored milestones the section lives where it belongs, and the field says only what has actually shipped.



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
