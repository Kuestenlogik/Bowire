#!/usr/bin/env node
// Which milestone(s) a release ships — and therefore its theme (docs/contributing/project-board.md,
// "Milestones and releases"). Milestones are ordered work sections `M<n> — <theme>`; a release
// gets its version number only when it is cut, so the number cannot find the milestone. The
// release names it instead, and this script reads that in three places, in order:
//
//   1. The annotated tag's message (`git tag -a v2.8.0 -m "Bowire v2.8.0 — M1 — …"`): every
//      `M<n>` token in it is a shipped milestone.
//   2. A milestone whose title starts with the version (`v2.8 — …`), for tags cut under the
//      old convention.
//   3. The frontmost open milestone — the section being delivered from. A release is cut at the
//      end of a section and may be cut in between, so the section is not required to be finished;
//      when it is not, the resolution is an inference and says so.
//
// Usage: node scripts/ci/resolve-milestone.mjs <tag-or-version>
// Prints GitHub Actions outputs: milestones (titles, `|`-separated), milestone (the first),
// theme (the titles' tails joined with " + "), numbers (M-numbers, space-separated). Needs
// GH_TOKEN / the gh CLI signed in and the tag fetched (fetch-depth: 0).
import { execFileSync } from 'node:child_process';

const raw = process.argv[2];
if (!raw) { console.error('usage: resolve-milestone.mjs <tag-or-version>'); process.exit(2); }
const tag = raw.startsWith('v') ? raw : 'v' + raw;
const version = tag.slice(1);
const base = version.replace(/-.*$/, '');
const majMin = base.split('.').slice(0, 2).join('.');

const gh = (...a) => execFileSync('gh', a, { encoding: 'utf8' });
const repo = process.env.GITHUB_REPOSITORY ?? gh('repo', 'view', '--json', 'nameWithOwner', '-q', '.nameWithOwner').trim();
const all = JSON.parse(gh('api', `repos/${repo}/milestones?state=all&per_page=100`));
const order = t => { const m = /^M(\d+)/.exec(t); return m ? Number(m[1]) : Number.POSITIVE_INFINITY; };
const theme = t => t.replace(/^(?:M\d+|v[\w.-]+)\s*(?:[—-]\s*)?/, '').trim();

let chosen = [];
let how = '';

// 1. the tag message
let message = '';
try { message = execFileSync('git', ['tag', '-l', '--format=%(contents)', tag], { encoding: 'utf8' }); } catch { /* no such tag locally */ }
const numbers = [...new Set([...message.matchAll(/\bM(\d+)\b/g)].map(m => Number(m[1])))];
if (numbers.length) {
  chosen = numbers.map(n => all.find(m => order(m.title) === n)).filter(Boolean);
  how = `tag message names ${numbers.map(n => 'M' + n).join(', ')}`;
}

// 2. the old convention: version in the title
if (chosen.length === 0) {
  for (const candidate of [`v${base}`, `v${majMin}`]) {
    const hit = all.find(m => new RegExp(`^${candidate.replace(/\./g, '\\.')}(\\s+[—-]\\s+.+)?$`).test(m.title));
    if (hit) { chosen = [hit]; how = `title starts with ${candidate}`; break; }
  }
}

// 3. the frontmost section — the one being delivered from
//
// An in-between cut ships from a section that still has open tickets, so requiring the frontmost
// section to be finished would make this fall through for exactly that case and hand the release
// no theme at all. It resolves either way; only the confidence differs, and the log says which.
if (chosen.length === 0) {
  const open = all.filter(m => m.state === 'open' && Number.isFinite(order(m.title))).sort((a, b) => order(a.title) - order(b.title));
  const front = open[0];
  if (front) {
    chosen = [front];
    const name = front.title.split(' ')[0];
    how = front.open_issues === 0
      ? `frontmost milestone ${name} is complete`
      : `frontmost milestone ${name} is the one in progress — inferred, ${front.open_issues} ticket(s) still open`;
  }
}

const out = chosen.length
  ? {
      milestones: chosen.map(m => m.title).join('|'),
      milestone: chosen[0].title,
      theme: chosen.map(m => theme(m.title)).filter(Boolean).join(' + '),
      numbers: chosen.map(m => m.title.split(' ')[0]).join(' '),
    }
  : { milestones: '', milestone: '', theme: '', numbers: '' };

console.error(chosen.length ? `Resolved for ${tag}: ${out.milestones} (${how})` : `::warning::No milestone resolved for ${tag}: the tag message names none, no title starts with v${base}/v${majMin}, and there is no open section to deliver from.`);
if (process.env.GITHUB_OUTPUT) {
  execFileSync('sh', ['-c', `printf '%s\\n' "$LINES" >> "$GITHUB_OUTPUT"`], { env: { ...process.env, LINES: Object.entries(out).map(([k, v]) => `${k}=${v}`).join('\n') } });
}
console.log(JSON.stringify(out));
