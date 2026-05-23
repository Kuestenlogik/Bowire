#!/usr/bin/env node
// Build a JSON snapshot of repo activity (releases + last push) for the
// Community page's "Activity on deck" section. Runs in CI before the Jekyll
// build (see .github/workflows/docs.yml) so the marketing site ships with
// fresh-at-build-time activity data baked in — no client-side calls to
// api.github.com at page load, no per-visitor rate-limit risk.
//
// Output: site/_data/activity.json
// Liquid consumer: site/community.html via {{ site.data.activity.* }}
//
// Auth: GITHUB_TOKEN env var when present (CI default — 1000 req/h);
// unauthenticated otherwise (60 req/h, fine for local re-runs).
//
// Failure mode: if the API is unreachable / rate-limited / returns junk,
// the script writes a snapshot with `available: false` so the page renders
// the static-fallback path instead of broken values.

import { writeFile, mkdir } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO = 'Kuestenlogik/Bowire';
const DISPLAY = 4;                              // releases rendered in the list
const FETCH = 10;                               // releases pulled for cadence math
const THIRTY_DAYS_MS = 30 * 24 * 60 * 60 * 1000;

const __dirname = dirname(fileURLToPath(import.meta.url));
const OUT_PATH = join(__dirname, '..', 'site', '_data', 'activity.json');

const headers = { 'Accept': 'application/vnd.github+json', 'User-Agent': 'bowire-activity-snapshot' };
if (process.env.GITHUB_TOKEN) headers['Authorization'] = `Bearer ${process.env.GITHUB_TOKEN}`;

function relativeTime(iso, now = Date.now()) {
  if (!iso) return null;
  const then = Date.parse(iso);
  if (Number.isNaN(then)) return null;
  let diff = now - then;
  if (diff < 0) diff = 0;
  const mins = Math.round(diff / 60000);
  if (mins < 60) return mins <= 1 ? 'just now' : `${mins} min ago`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return `${hours} h ago`;
  const days = Math.round(hours / 24);
  if (days < 30) return `${days} d ago`;
  const months = Math.round(days / 30);
  if (months < 12) return `${months} mo ago`;
  const years = Math.round(months / 12);
  return `${years} yr ago`;
}

// Strip GitHub's auto-generated boilerplate so the released-page activity
// pane shows actual content, not "_See the auto-generated change list below._"
// for every release that didn't carry a hand-written description.
function summariseBody(body, releaseName) {
  if (!body) return { header: null, lede: '' };
  const BOILERPLATE = [
    /^_See the auto-generated change list below\._\s*$/i,
    /^_The full commit list[^_]*_\s*$/i,
    /^\*\*Full Changelog\*\*:.*$/i,
    /^---+\s*$/,
  ];
  const headerMatch = body.match(/^#{1,6}\s+(.+)$/m);
  let header = headerMatch ? headerMatch[1].trim() : null;
  // The release name already shows above the body, so drop the body's heading
  // when it just repeats the tag or starts the release name (common in
  // auto-generated release notes).
  if (header && releaseName) {
    const h = header.toLowerCase();
    const n = releaseName.toLowerCase();
    if (h === n || n.startsWith(h + ' ') || n.startsWith(h + '—') || n.startsWith(h + '-')) {
      header = null;
    }
  }
  const lede = body
    .replace(/^#{1,6}\s+.*$/gm, '')
    .replace(/```[\s\S]*?```/g, '')
    .replace(/!\[[^\]]*\]\([^)]*\)/g, '')
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
    .split(/\r?\n/)
    .map(l => l.trim())
    .filter(l => l && !/^[-*]\s*$/.test(l))
    .filter(l => !BOILERPLATE.some(re => re.test(l)))
    .slice(0, 4)
    .join(' ')
    .replace(/^[-*]\s+/g, '')
    .replace(/\s+/g, ' ');
  const trimmed = lede.length > 220 ? lede.slice(0, 217).replace(/\s+\S*$/, '') + '…' : lede;
  return { header, lede: trimmed };
}

async function fetchJson(url) {
  const res = await fetch(url, { headers });
  if (!res.ok) throw new Error(`GitHub API ${res.status} ${res.statusText} for ${url}`);
  return res.json();
}

async function main() {
  const generatedAt = new Date();
  let snapshot;
  try {
    const [releases, repo] = await Promise.all([
      fetchJson(`https://api.github.com/repos/${REPO}/releases?per_page=${FETCH}`),
      fetchJson(`https://api.github.com/repos/${REPO}`),
    ]);

    if (!Array.isArray(releases)) throw new Error('releases response was not an array');

    const cutoff = generatedAt.getTime() - THIRTY_DAYS_MS;
    const releases30d = releases.filter(rel => {
      const t = Date.parse(rel.published_at || rel.created_at || '');
      return !Number.isNaN(t) && t >= cutoff;
    }).length;

    const items = releases.slice(0, DISPLAY).map(rel => {
      const { header, lede } = summariseBody(rel.body, rel.name || rel.tag_name);
      const publishedAt = rel.published_at || rel.created_at || null;
      return {
        name: rel.name || rel.tag_name,
        tag: rel.tag_name,
        url: rel.html_url,
        published_at: publishedAt,
        published_date: publishedAt ? publishedAt.slice(0, 10) : null,
        body_header: header,
        body_lede: lede,
      };
    });

    const lastRelease = releases[0] || null;
    snapshot = {
      available: true,
      generated_at: generatedAt.toISOString(),
      releases30d,
      last_release_relative: lastRelease ? relativeTime(lastRelease.published_at || lastRelease.created_at, generatedAt.getTime()) : null,
      last_release_at: lastRelease ? (lastRelease.published_at || lastRelease.created_at) : null,
      last_push_relative: relativeTime(repo.pushed_at, generatedAt.getTime()),
      last_push_at: repo.pushed_at || null,
      releases: items,
    };
  } catch (err) {
    console.error(`[activity-snapshot] fetch failed: ${err.message}`);
    snapshot = {
      available: false,
      generated_at: generatedAt.toISOString(),
      error: err.message,
      releases30d: null,
      last_release_relative: null,
      last_release_at: null,
      last_push_relative: null,
      last_push_at: null,
      releases: [],
    };
  }

  await mkdir(dirname(OUT_PATH), { recursive: true });
  await writeFile(OUT_PATH, JSON.stringify(snapshot, null, 2) + '\n', 'utf8');
  console.log(`[activity-snapshot] wrote ${OUT_PATH} (available=${snapshot.available}, releases30d=${snapshot.releases30d}, last_push=${snapshot.last_push_relative})`);
}

main().catch(err => {
  console.error(err);
  process.exit(1);
});
