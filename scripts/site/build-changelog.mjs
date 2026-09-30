#!/usr/bin/env node
// Build the changelog page's data from the curated release notes (#671).
//
// docs/release-notes/v<version>.md is written once per release (release.mjs `notes`) and is what
// the GitHub Release is published with. The site's /changelog.html shows the same text, newest
// first, one anchor per version — so the release cadence is visible without leaving the site,
// and nobody maintains a second copy. Runs in CI before Jekyll (.github/workflows/docs.yml),
// next to the activity snapshot.
//
// Output: site/_data/changelog.json — [{ version, anchor, title, date, url, body }]
//   body     the notes without front-matter, headings demoted one level (the page's own <h2> is
//            the version), Markdown for Jekyll's `markdownify`.
//   date     the GitHub Release's publish date when the API answers, else the tag's commit date,
//            else null.
//
// Run: node scripts/site/build-changelog.mjs
import { readdirSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = join(__dirname, '..', '..');
const NOTES = join(ROOT, 'docs', 'release-notes');
const OUT = join(ROOT, 'site', '_data', 'changelog.json');
const REPO = 'Kuestenlogik/Bowire';

/** `---\ntitle: …\nversion: …\n---\nbody` → { title, version, body }. */
export function parseNotes(text) {
  const s = String(text ?? '');
  const meta = {};
  let body = s;
  if (s.startsWith('---')) {
    const end = s.indexOf('\n---', 3);
    if (end > 0) {
      for (const line of s.slice(3, end).split('\n')) {
        const m = /^(\w+):\s*(.*)$/.exec(line.trim());
        if (m) meta[m[1]] = m[2].replace(/^["']|["']$/g, '').trim();
      }
      body = s.slice(s.indexOf('\n', end + 1) + 1);
    }
  }
  return { title: meta.title ?? null, version: meta.version ?? null, body: body.trim() };
}

/** One heading level down, so `##` sections sit under the version's `<h2>`. */
export function demoteHeadings(md) {
  let fenced = false;
  return md.split('\n').map(line => {
    if (/^```/.test(line)) fenced = !fenced;
    if (fenced) return line;
    return /^#{1,5} /.test(line) ? '#' + line : line;
  }).join('\n');
}

/** Newest first: 2.10.0 before 2.9.0. */
export function compareDesc(a, b) {
  const pa = a.split('.').map(Number), pb = b.split('.').map(Number);
  for (let i = 0; i < 3; i++) if ((pa[i] ?? 0) !== (pb[i] ?? 0)) return (pb[i] ?? 0) - (pa[i] ?? 0);
  return 0;
}

async function releaseDates() {
  const headers = { Accept: 'application/vnd.github+json', 'User-Agent': 'bowire-changelog' };
  if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
  try {
    const res = await fetch(`https://api.github.com/repos/${REPO}/releases?per_page=100`, { headers });
    if (!res.ok) return new Map();
    return new Map((await res.json()).map(r => [r.tag_name, (r.published_at ?? r.created_at ?? '').slice(0, 10) || null]));
  } catch { return new Map(); }
}

function tagDate(tag) {
  try { return execFileSync('git', ['log', '-1', '--format=%cs', tag], { cwd: ROOT, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim() || null; }
  catch { return null; }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const dates = await releaseDates();
  const entries = readdirSync(NOTES)
    .filter(f => /^v\d+\.\d+\.\d+\.md$/.test(f))
    .map(f => {
      const version = f.slice(1, -3);
      const notes = parseNotes(readFileSync(join(NOTES, f), 'utf8'));
      const tag = `v${version}`;
      return {
        version,
        anchor: tag.replace(/\./g, '-'),
        title: notes.title,
        date: dates.get(tag) ?? tagDate(tag),
        url: `https://github.com/${REPO}/releases/tag/${tag}`,
        body: demoteHeadings(notes.body),
      };
    })
    .sort((a, b) => compareDesc(a.version, b.version));
  mkdirSync(dirname(OUT), { recursive: true });
  writeFileSync(OUT, JSON.stringify(entries, null, 2) + '\n');
  console.log(`[changelog] ${entries.length} release(s) → ${OUT}`);
}
