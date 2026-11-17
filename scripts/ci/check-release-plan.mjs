#!/usr/bin/env node
// After a tag: does the board say what this release shipped? (docs/contributing/project-board.md.)
// Shared by every repository that carries the release tool (see release-plan.mjs).
//
// The release is planned, every ticket carrying the Release it is planned for, so there is nothing to
// stamp. What can still be off, and what this reports:
//
//   A ticket planned for this release is still open. `release.mjs cut` refuses that; a tag pushed by
//   hand does not. It did not ship, and the log says so.
//   A ticket closed since the previous release without being planned. It went out in this one, so on a
//   board with a Release field it gets this release (`2.8` for v2.8.0, `2.8.1` for v2.8.1), the one
//   case where the field is written after the fact, and the log lists each such ticket so the plan can
//   learn. A board that plans by the milestone alone is only told: the token of a pipeline need not
//   edit issues.
//
// A prerelease claims nothing: its tickets are claimed by the release it previews. Never fails the
// release: problems are printed as warnings.
//
// Usage: node scripts/ci/check-release-plan.mjs <tag>
// Needs a token with project scope (GH_TOKEN).
import { fileURLToPath } from 'node:url';
import { planByField, planFor, previousRelease, productOfRepo, REPO, releaseOfVersion } from './release-plan.mjs';
import { board, boardItems, gh, publishedReleases } from './release-board.mjs';

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const [tag] = process.argv.slice(2);
  if (!tag) { console.error('usage: check-release-plan.mjs <tag>'); process.exit(2); }
  try {
    const product = productOfRepo(REPO);
    const release = releaseOfVersion(tag);
    const prerelease = /-/.test(tag);
    const since = previousRelease(tag, publishedReleases());
    const { shipped, stillOpen, unplanned } = planFor(boardItems(), product, tag, since?.at);
    console.log(`${product} ${tag}: ${shipped.length} planned ticket(s) shipped${since ? ` (previous release ${since.tag})` : ''}.`);
    for (const it of stillOpen) console.log(`::warning::${it.repo}#${it.number} is planned for ${release} but still open: it did not ship in ${tag}.`);
    if (unplanned.length && !prerelease) {
      const b = planByField ? board() : null;
      for (const it of unplanned) {
        if (b) gh('project', 'item-edit', '--project-id', b.id, '--id', it.id, '--field-id', b.release, '--text', release);
        console.log(`::warning::${it.repo}#${it.number} closed since ${since?.tag ?? 'the start'} without a planned release: ${b ? `set to ${release}` : `give it the milestone of v${release}`}.`);
      }
    }
  } catch (err) {
    console.log(`::warning::Release plan not checked: ${err.message}`);
  }
}
