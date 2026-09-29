#!/usr/bin/env node
// Which milestone a release ships (docs/contributing/project-board.md, "Products, releases and
// milestones"). A milestone is a release again — `v2.8 — <theme>` — so the version finds it: the
// milestone of this repository's product whose version is the tag's (`v2.8` for v2.8.0,
// `v2.8.1` for v2.8.1). Nothing is inferred; a tag without its milestone resolves to nothing and
// says so, because that is a release nobody planned.
//
// Usage: node scripts/ci/resolve-milestone.mjs <tag-or-version>
// Prints GitHub Actions outputs: milestones (titles, `|`-separated), milestone (the first),
// theme (the themes joined with " + "), numbers (milestone numbers, space-separated).
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { parseMilestone, productOfRepo, releaseMatches } from './release-plan.mjs';

export const theme = t => parseMilestone(t)?.theme ?? '';

/** The milestones of `repo`'s product that `tag` delivers. */
export function resolve(tag, milestones, repo) {
  const product = productOfRepo(repo);
  return milestones.filter(m => {
    const p = parseMilestone(m.title);
    return p && (p.product ?? productOfRepo(repo)) === product && releaseMatches(p.version, tag);
  });
}

/** The four GitHub Actions outputs, empty when nothing resolved. */
export function outputs(chosen) {
  return chosen.length
    ? {
        milestones: chosen.map(m => m.title).join('|'),
        milestone: chosen[0].title,
        theme: chosen.map(m => theme(m.title)).filter(Boolean).join(' + '),
        numbers: chosen.map(m => String(m.number)).join(' '),
      }
    : { milestones: '', milestone: '', theme: '', numbers: '' };
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const raw = process.argv[2];
  if (!raw) { console.error('usage: resolve-milestone.mjs <tag-or-version>'); process.exit(2); }
  const tag = raw.startsWith('v') ? raw : 'v' + raw;
  const gh = (...a) => execFileSync('gh', a, { encoding: 'utf8' });
  const repo = process.env.GITHUB_REPOSITORY ?? gh('repo', 'view', '--json', 'nameWithOwner', '-q', '.nameWithOwner').trim();
  const all = JSON.parse(gh('api', `repos/${repo}/milestones?state=all&per_page=100`));
  const chosen = resolve(tag, all, repo);
  const out = outputs(chosen);
  console.error(chosen.length ? `Resolved for ${tag}: ${out.milestones}` : `::warning::No milestone for ${tag}: no milestone of ${productOfRepo(repo)} is titled v${tag.slice(1).replace(/-.*$/, '').replace(/\.0$/, '')} — a release nobody planned.`);
  if (process.env.GITHUB_OUTPUT) {
    execFileSync('sh', ['-c', `printf '%s\\n' "$LINES" >> "$GITHUB_OUTPUT"`], { env: { ...process.env, LINES: Object.entries(out).map(([k, v]) => `${k}=${v}`).join('\n') } });
  }
  console.log(JSON.stringify(out));
}
