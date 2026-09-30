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
| **Product** *(single-select, **mandatory**)* | The **artifact that ships the ticket**, and so the version line it counts in: `Bowire` (NuGet packages, tool, container — the 2.x line; the site and the docs travel with it), `VS Code` (the extension, its own 1.x line from the same repository), `Protocol.Akka`, `Protocol.Dis`, … `Sdk.Node`, … `Templates`, `Samples`, `Bootcamp`, `bowire-action`. A repository's own product is the default; the field matters where one repository ships more than one. See [Products, releases and milestones](#products-releases-and-milestones). |
| **Release** *(text, **mandatory**)* | The **release the ticket is planned for**, in its product's count: `2.8`, `2.8.1`, `1.2`. Set when the ticket is planned — at the latest at triage — not at the cut. `2.8` means the minor release 2.8.0; a patch is planned as `2.8.1`. With `Product` it reads "Bowire 2.8", "Protocol.Akka 1.2". A text field on purpose: every product counts on its own, and one list of options for all of them was the confusion it replaces. (Boards cannot group by a text field; the roadmap groups by milestone, and `release:2.8` filters.) |
| **Milestone** *(built-in)* | The **release** itself, in the ticket's repository: `v2.8 — <theme>`, or `<Product> v1.1 — <theme>` for a second product of the same repository (`VS Code v1.1 — …`). `open` = planned, **`closed` = shipped** (*"Ausgeliefert in v2.8.0"*), with the theme in the title and the due date on it. It must name the same release as the `Release` field. A milestone with nothing open is a release that is due. |
| **Area** *(single-select, **mandatory**)* | Which component an issue belongs to (`workbench`, `cli`, `security`, `mcp`, `plugin-sdk`, `mock`, `docs`, `site`, `bootcamp`, `multi`). The *primary* axis for "show me everything affecting X" — should be set on every item (use `multi` only for genuinely cross-cutting work). Replaced the old `Track` field, which overlapped with it. |
| **Effort** *(actual)* | `Low` / `Medium` / `High`. Same scale as the org-level `Issue.Effort` so the Project mirror carries the *actual* size of the work next to the *plan*. Used to spot oversized issues (`High` = consider splitting before starting) and to right-size milestones. Not a commitment, just a sanity check. |
| **Start date** *(actual)* | First commit referencing `#N`. Backfilled by the roadmap-sync job from git history. Drives the Roadmap layout's left edge. |
| **Target date** *(actual)* | Issue's `closedAt`. Backfilled by the roadmap-sync job. Drives the Roadmap layout's right edge. |

> **Priority** lives on the org-level **Issue** field (`Urgent` / `High` / `Medium` / `Low`), not on the Project board — it travels with the issue across every project that picks it up. Set it on the issue itself (right sidebar → Fields → Priority).
>
> **Kind** is the native GitHub issue **Type** (`Bug` / `Feature` / `Task`), set on the issue itself — not a label or a Project field. (The old `kind:*` labels were retired in favour of issue Types.)
>
> The `Start date`, `Target date`, and `Effort` Project fields are **mirrored** by org-level Issue fields with the *same scale*. Issue-layer carries the **plan** (estimate / planned start / planned ship); Project-layer carries the **actual** (when work began, when it shipped, what size it turned out to be). Plan-vs-actual divergence is visible per issue.

## The ticket vocabulary, the same in every Küstenlogik repository

| Where | What | Values |
|---|---|---|
| **Type** *(built-in issue type)* | What the ticket is | `Bug` · `Feature` · `Task` — every issue carries exactly one; the former `kind:*` labels are retired |
| **Priority** *(org issue field, on the issue)* | The order within a section | `Urgent` (now — blocks or burns) · `High` (this section) · `Medium` (the next section) · `Low` (someday) |
| **Effort** *(org issue field)* | Planned size | `Low` (a day or less) · `Medium` (days) · `High` (a week or more — split it) |
| **Effort (actual)** *(board field)* | What it turned out to be, set at Done | the same scale — plan next to actual, per issue (Bowire's design) |
| **Product** *(board field)* | The artifact that ships it | `Bowire`, `VS Code`, `Protocol.Akka`, … — the version line the ticket counts in |
| **Release** *(board field)* | The release it is planned for | `2.8`, `2.8.1`, `1.2` — in the product's count, set when planned |
| **Milestone** *(built-in)* | That release | `v2.8 — <theme>`; `<Product> v1.1 — <theme>` for a second product of one repository |
| **Area** *(board field)* | The component | per product (`terrain`, `workbench`, `broker`, …); the `area:*` label says the same for issue search |
| **Status** *(board field)* | Where the work sits in the flow | `Backlog` · `Next up` · `In progress` · `In review` · `Done` |
| **Assignee** | Who holds it | never empty — unassigned means nobody decided |
| **Parent issue** *(built-in sub-issues)* | Belongs to / depends on | an epic's slices are its sub-issues; "Folge von #N" in the text is not a link |

**Order inside a release** is the `Priority` field, read by the roadmap generator; manual sorting on a board is the order of one view and nothing else — not on the ticket, not in the API, gone when the view is regrouped. Use it for the last fine ordering within a priority, never instead of one.

## Products, releases and milestones

Since 2026-09-29 a release is **planned**, not reconstructed after the fact.

- **Every ticket names three things, and they agree:** its `Product` (the artifact that ships it), the `Release` it is planned for in that product's count (`2.8`), and the milestone of that release (`v2.8 — <theme>`). The rules are code: [`scripts/ci/release-plan.mjs`](../../scripts/ci/release-plan.mjs).
- **Each product counts on its own.** Bowire is at 2.x, the VS Code extension at 1.x, Protocol.Akka at 1.1, the SDKs at 0.x. A plugin repository's milestones are that plugin's releases (`v1.2 — …` in Bowire.Protocol.Akka); the main repository's milestones are Bowire's, plus a prefixed milestone for a second product it ships (`VS Code v1.1 — …`). The site and the docs have no version of their own and travel with Bowire.
- **One release, one milestone.** The theme is in its title and becomes the heading of the release notes' story; the due date is on it. A ticket that does not fit the release it would land in moves to the next one.
- **A release is due** when its milestone has nothing open. `node scripts/ci/release.mjs status` shows each open release of the repository's product, lowest first, and says which one is due.
- **Patch releases** are planned like any other: a milestone `v2.8.1 — <theme>` and `Release = 2.8.1` on the tickets it fixes.

The daily [field guard](../../.github/workflows/roadmap-field-guard.yml) runs [`check-board.mjs`](../../scripts/ci/check-board.mjs): every open issue of every product repository has to be on the board, with the three fields filled in and in agreement. It fails, and files a tracking issue, for any that is not.

> **Why this changed back.** From 2026-09-19 milestones were ordered work sections (`M1 — …`) and `Release` was stamped at the cut, empty until then. Nothing then said when the next release was due: 2.8 waited three weeks behind 43 finished tickets, and the tickets of one release were only found after it. The sections were renamed into the releases they became (M1 and M2 → v2.8, M3 → v2.9, M4 → v2.10, M6 → v2.11, M7 → v2.12, M8 → v2.13, M5 → v3.0 — the breaking cut) and every ticket got its planned release; the old values were kept in a `Release (alt)` field until 2026-09-30, and the release notes of each version remain their record.

### Releasing

```bash
node scripts/ci/release.mjs status          # which release is due
node scripts/ci/release.mjs notes 2.8.0     # drafts docs/release-notes/v2.8.0.md from the plan
# write the notes: a title that says what this release is, prose per ### section
node scripts/ci/release.mjs cut 2.8.0 --dry-run
node scripts/ci/release.mjs cut 2.8.0
```

`notes` lists the tickets planned for the release and done, grouped by area, and the ones that closed since the last tag without a plan. `cut` refuses while a planned ticket is open or the notes are still a draft; it gives the unplanned-but-shipped tickets this release, tags with a message naming the milestone, and closes the milestone. The [release pipeline](../../.github/workflows/release.yml) publishes on the tag with the notes as the body, and [`check-release-plan.mjs`](../../scripts/ci/check-release-plan.mjs) checks the board against the tag once more.

## Labels

Labels live on the GitHub issue itself (not on the Project board). They're the searchable side of the same information the Project fields carry, so `is:open label:area:security` works from the standard issue list without having to crack open the Project view. The full label list lives at [github.com/Kuestenlogik/Bowire/labels](https://github.com/Kuestenlogik/Bowire/labels) — the namespaces explained below are stable; concrete values come and go.

| Label namespace | Purpose |
|---|---|
| **`community-vote`** | Feature requests where reactions are read as priority signal. Don't comment "+1" — react with 👍. |

## Recommended views

The board ships with the default *All items* view. The four views below are the ones we keep returning to — they need ~30 seconds each to configure in the UI (clone the default view, change layout + grouping):

### 🗺 Roadmap

- **Layout**: Roadmap
- **Group by**: `Milestone` (the release). `Product` groups by artifact; filter `release:2.8` for one release across repositories.
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

- **One field per concept**: `Status` is the *where in the flow*, `Product` the *what ships it*, `Release` the *when* (in that product's count), `Area` the *component*, the issue **Type** the *kind* (Bug / Feature / Task). `Status` and `Release` are independent axes — an item can be planned for `2.9` and still rest in `Backlog`. `Area`, `Product`, `Release` and the milestone are set when the ticket is planned. (The old `Track` field was dropped — it overlapped with `Area`.)
- **Labels duplicate fields on purpose**: GitHub issue search needs labels (`is:open label:area:security`). Project filters need fields. The two are kept in sync so an issue is findable from either side.
- **Every issue is on the board.** There is no label to opt in: a new issue in this repository is added the moment it is filed ([`project-add.yml`](../../.github/workflows/project-add.yml)), and the daily guard adds any open issue of a product repository that is still missing. (The `roadmap` label did that job until 2026-09-30 and was retired: 20 issues filed without it had never reached a plan.)
- **`community-vote` label** marks feature requests where reactions on the issue are read as priority signal. Don't comment "+1" — react with 👍.
- **PRs close issues via `Closes #N`** so Status flips to `Done` automatically and the item drops off the active views.

## Maintenance

- New issue created via *Convert from Markdown* (in the issue editor) or *Create issue* — the board adds it as `Backlog` by default.
- Status transitions: `Backlog` → `Next up` → `In progress` → `In review` → `Done`. The last two are driven by PR state where possible.
- Milestones are managed in [Settings → Issues → Milestones](https://github.com/Kuestenlogik/Bowire/milestones). When a milestone closes, its issues move out of the `Roadmap` view automatically and the milestone drops out of `ROADMAP.md` (whose changelog moves to GitHub Releases).

### When you create an issue

It lands on the board on its own, with its repository's product. At triage give it:

- **Product** — the artifact that ships it;
- **Release** — the release it is planned for, in that product's count;
- **the milestone of that release** — or open one (`v<next> — <theme>`) when it belongs in a release not planned yet.

A ticket that is not planned for a release yet goes into the furthest open one and moves forward when it is picked up; "no release" is not a resting state — that is how releases stop being visible.

### Milestone title = release theme

Every milestone's **title** carries the release headline directly: `vX.Y[.Z] — <theme>`. The theme is the same one that lands on the GitHub Release once the milestone tags, and it shows in the Project board's Roadmap view as the group heading (since Projects v2 reads the milestone title verbatim).

The open releases live on the [milestones page](https://github.com/Kuestenlogik/Bowire/milestones) and in [`ROADMAP.md`](../../ROADMAP.md); the shipped ones on [GitHub Releases](https://github.com/Kuestenlogik/Bowire/releases).

**One concept per release.** Themes are 2-5 words, concrete enough that a reader knows what the cycle is about (`gRPC Connect` beats `protocol expansion`). Bundling two themes with `+` is allowed if both are equally weighted (v2.0 carries the shell refactor AND the workspace-as-project-folder pivot, both major) — but the default is one theme so the cycle has an obvious anchor.

**Why pre-commit a theme at planning time:** the headline defines what the cycle is *about* — what we'd be embarrassed to ship without. It anchors the milestone discussion ("does this issue serve the theme?"), avoids the retrospective scramble of summarising whatever happened to land, and gives the team a one-line elevator pitch through the cycle. Mid-cycle pivots are fine — rename the milestone (GitHub keeps the audit trail).

**Mechanical consequences:**
- `resolve-milestone.mjs` finds the release's milestone by its version; the GitHub Release is named by the version alone, and the notes' front-matter title is its heading.
- `scripts/ci/generate-roadmap.mjs` renders the full title as the section heading in `ROADMAP.md` so the offline view matches the Project board.
- The milestone description stays free-form for slip context, stakeholder hints, &c. — no machinery parses it.
- If the milestone title is bare (`v2.0` with no ` — <theme>` tail), the release falls back to a bare `vX.Y.Z` title and the roadmap section shows no theme — so missing themes are visible by their absence rather than crashing the pipeline.

**CLI ergonomics caveat:** `gh issue list --milestone v2.0` no longer matches when the milestone is renamed to `v2.0 — <theme>` — `gh` matches the full title verbatim. Either use the full title, or look up by milestone number (`--milestone <N>`).

## Automation

The roadmap maintains itself; every issue of a product repository takes part:

| Event | What happens |
|---|---|
| New issue in this repository | [`project-add.yml`](../../.github/workflows/project-add.yml) puts it on the board with `Product = Bowire` (Status defaults to `Backlog`); Release and milestone are set at triage — the field guard lists it until they are |
| New issue in another product repository | The daily field guard adds it (`check-board.mjs --add`) with its repository's product, then lists it until it is planned |
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
4. **Auto-add to project** → optional. The field guard adds every open issue of a product repository daily, so the rule only makes that immediate for the sibling repositories. If you enable it, use one filter per repository **without a label clause**: `repo:Kuestenlogik/<RepoName> is:issue`.

Sibling-repo wiring options (Project-side vs Action-side vs back-fill) are documented separately in [`multi-repo-project-add.md`](multi-repo-project-add.md).
