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
output. The post-release-floor-bump workflow already handles other
post-publish bookkeeping; the rename can ride on it later if we
automate it.

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
