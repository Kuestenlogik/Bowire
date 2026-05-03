<!--
Thanks for sending a PR to Bowire! A few notes to make review easy:
- Keep one logical change per PR. Mechanical refactors and behaviour
  changes belong in separate PRs.
- The CI run will build + run all tests; please make sure both are
  green locally before pushing (`dotnet build -c Release` /
  `dotnet test`).
- The repository follows Conventional Commits — see CONTRIBUTING.md
  for the full list (feat / fix / docs / refactor / test / chore).
-->

## Summary

<!-- 1-3 sentences: what changes and why. -->

## Changes

<!-- Optional bullet list. Skip for trivial PRs. -->

-

## Test plan

<!-- How did you verify the change? Tests added, manual smoke, both. -->

- [ ] Unit / integration tests added or updated
- [ ] `dotnet build Kuestenlogik.Bowire.slnx -c Release` clean (0 warnings)
- [ ] `dotnet test Kuestenlogik.Bowire.slnx` green
- [ ] Manual UI / CLI verification (where applicable)

## Roadmap / Changelog

<!-- New user-visible features should land in CHANGELOG.md and ROADMAP.md
     under Completed. Keep these in sync with the PR. -->

- [ ] `CHANGELOG.md` updated (for user-visible changes)
- [ ] `ROADMAP.md` updated (if a planned item shipped)

## Notes for the reviewer

<!-- Anything that doesn't fit above: trade-offs, follow-ups, screenshots,
     before/after numbers, links to issues/discussions. -->
