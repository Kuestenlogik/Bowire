# Release notes — pre-tag editorial body

This directory holds the **editorial body** of each upcoming release
*before* the version is tagged and published. The convention is
file-driven so we never ship a half-empty Draft release sitting in the
GitHub Releases list — the page should only show real, published
releases with attached files.

## Convention

- **`upcoming.md`** — body of the next unreleased version. Grows
  alongside the work that lands in `main` (PRs / commits add their
  headlines as they go). Always carries front-matter with the
  intended `title:` and (optional) `version:`. Required for the
  release pipeline — the `Verify curated release notes` step gates
  on it.
- **`vX.Y.Z.md`** — preserved body of a published release. Created
  by renaming `upcoming.md` at release time so the historical text is
  in git, not just in the GitHub Release.

## Tag-time flow

1. **Before the tag.** Make sure `upcoming.md` is curated — every
   real highlight has a paragraph, breaking changes are listed under
   their own section, the front-matter `title:` reads well as the
   release title (the `v1.6.1` style: `OIDC auth + MCP resources/prompts`).
2. **Push the tag.** `release.yml` reads `upcoming.md` as the
   editorial body when present, falls back to the legacy "Draft
   release on GitHub" path otherwise (v2.0 used the legacy path).
3. **After the publish.** Open a small PR that:
   - Renames `upcoming.md` → `v<tag>.md` (preserves the body in git).
   - Recreates `upcoming.md` from `_template.md` for the next round.

The post-release rename is intentionally a separate PR so the release
itself doesn't carry a `main` commit that depends on `release.yml`
output. (Versioning itself needs no post-publish bookkeeping — MinVer
derives every version from the git tag at build time; see
`Directory.Build.props`.)

> **Step 3 is the one that slips.** `v2.3.0.md` and `v2.4.0.md` were both
> written by hand as short summaries while `upcoming.md` was never reset,
> so it grew to 750 lines carrying highlights from two already-shipped
> versions. Nothing broke — the tag-time chain prefers
> `docs/release-notes/<tag>.md` and that file existed both times — but the
> fallback to `upcoming.md` would have published them again. If you skip
> the rename, at least reset the file.

## The change list under the body

The curated body is only the top half. Below the `---`, `release.yml`
appends a generated change list ([`scripts/ci/generate-changelog.mjs`](../../scripts/ci/generate-changelog.mjs))
built from `git log <previous-tag>..<tag>`.

It is generated from **commits, not pull requests**. GitHub's own
`generate_release_notes` lists merged PRs, and since work is committed
straight to `main`, that list was 100% Dependabot — all 41 entries in
v2.4.0, with none of the ~200 commits of real work in it.

The generator consumes the Conventional Commit prefix rather than printing
it: `feat` / `fix` / `refactor` / `docs` become the section a line sits
under, and the prefix itself is stripped. The scope stays as a bold lead-in
because the heading says *what kind* of change it is and only the scope says
*where*. Chores, CI/test plumbing (by scope as well as by type — a
`fix(test):` is not a fix anyone reading a release has seen) and dependency
bumps collapse into a single `<details>` block, so the visible list is the
work. Rules are pinned by
[`tests/…/ci/generate-changelog.test.mjs`](../../tests/Kuestenlogik.Bowire.Tests/ci/generate-changelog.test.mjs).

## Front-matter

```
---
title: re-architected workbench + git-backed workspaces
version: 2.0.0
---
```

- `title` — splices into the GitHub Release title as
  `v<version> — <title>`. Same style as the `v1.6.1 — OIDC auth + MCP
  resources/prompts` convention.
- `version` — optional sanity check; release.yml warns if the tag
  doesn't match.

Body follows after a blank line. Sections expected:
- **Highlights** — what's new, written for users.
- **Breaking changes** — wire / API / SKU shifts with migration paths.
- **Acknowledgements** — short, optional.

## Why not the GitHub Draft Release UI?

Until v2.0 we held the editorial body in a Draft release on
github.com/Kuestenlogik/Bowire/releases. That works but visually
suggests "v2.0 already exists" while the tag hasn't been pushed —
users hitting the page can't tell what's real. Moving the body into
the repo lets us keep that page clean.

The pipeline still supports the legacy path; the gate just prefers
the file when present.
