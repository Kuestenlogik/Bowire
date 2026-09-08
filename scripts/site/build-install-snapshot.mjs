#!/usr/bin/env node
// Build a JSON snapshot of how often Bowire has been pulled, across every
// channel that publishes a number. Runs in CI before the Jekyll build (see
// .github/workflows/docs.yml) so the site ships the figure baked in — no
// client-side calls at page load, no per-visitor rate-limit risk, and the
// same number for every visitor. Sibling of build-activity-snapshot.mjs and
// deliberately the same shape.
//
// Output: site/_data/installs.json
// Liquid consumer: site/_includes/hero.html via {{ site.data.installs.* }}
//
// Why bake rather than fetch in the browser: two of the three sources could
// be read client-side (NuGet and api.github.com both send
// Access-Control-Allow-Origin: *), but Docker Hub sends no CORS header at
// all, so a browser on bowire.io cannot read it. Baking all three keeps one
// number from one place instead of two live values plus one stale one.
//
// Auth: GITHUB_TOKEN env var when present (CI default — 1000 req/h);
// unauthenticated otherwise (60 req/h, fine for local re-runs).
//
// Failure mode: a source that cannot be read is recorded as null with the
// reason, and `available` reports whether *every* source answered. The page
// renders what it has rather than a wrong total presented as complete.

import { writeFile, mkdir } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO = 'Kuestenlogik/Bowire';
const NUGET_QUERY = 'https://azuresearch-usnc.nuget.org/query?q=Kuestenlogik.Bowire&prerelease=false&take=1000';
const DOCKER_REPO = 'https://hub.docker.com/v2/repositories/kuestenlogik/bowire/';

const __dirname = dirname(fileURLToPath(import.meta.url));
const OUT_PATH = join(__dirname, '..', '..', 'site', '_data', 'installs.json');

const ghHeaders = {
    'Accept': 'application/vnd.github+json',
    'User-Agent': 'bowire-install-snapshot',
};
if (process.env.GITHUB_TOKEN) ghHeaders['Authorization'] = `Bearer ${process.env.GITHUB_TOKEN}`;

async function getJson(url, headers = {}) {
    const res = await fetch(url, { headers, signal: AbortSignal.timeout(20000) });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return res.json();
}

/**
 * NuGet: the *max* across the Kuestenlogik.Bowire.* family, not the sum.
 *
 * Summing double-counts. One `dotnet tool install` pulls the CLI plus its
 * transitively referenced core and bundled protocol plugins, bumping ~10
 * package counters for one logical install. The max — today the core
 * Kuestenlogik.Bowire, which everything depends on — is an honest lower
 * bound on distinct pulls, and it auto-tracks whichever package leads.
 */
async function nugetMax() {
    const data = await getJson(NUGET_QUERY);
    const family = (data.data || []).filter((p) =>
        p.id === 'Kuestenlogik.Bowire' || p.id.startsWith('Kuestenlogik.Bowire.'));
    if (family.length === 0) throw new Error('no packages matched the family filter');
    const top = family.reduce((best, p) =>
        (p.totalDownloads || 0) > (best.totalDownloads || 0) ? p : best, family[0]);
    // The Version badge reads the canonical host package, not the leader —
    // they are the same today and need not stay so. Baked from the same
    // response so the page makes no NuGet call of its own either.
    const core = family.find((p) => p.id === 'Kuestenlogik.Bowire');
    return {
        count: top.totalDownloads || 0,
        leader: top.id,
        family: family.length,
        version: core ? core.version : null,
    };
}

/**
 * GitHub release assets: the sum across every asset of every release.
 *
 * This is the one number that already covers the package managers. Both the
 * winget manifest and the Chocolatey install script fetch the MSI from
 * `releases/download/v<version>/…`, so a winget or choco install increments
 * this counter — they are not a separate source, and could not be separated
 * from a direct download even in principle, because it is the same file.
 */
async function githubAssets() {
    const releases = await getJson(
        `https://api.github.com/repos/${REPO}/releases?per_page=100`, ghHeaders);
    if (!Array.isArray(releases)) throw new Error('unexpected response shape');
    let count = 0;
    let assets = 0;
    for (const r of releases) {
        for (const a of r.assets || []) { count += a.download_count || 0; assets++; }
    }
    return { count, releases: releases.length, assets };
}

/** Docker Hub's all-time pull count. ghcr.io publishes no equivalent. */
async function dockerPulls() {
    const data = await getJson(DOCKER_REPO);
    if (typeof data.pull_count !== 'number') throw new Error('no pull_count in response');
    return { count: data.pull_count };
}

async function collect(name, fn) {
    try {
        const value = await fn();
        console.log(`[install-snapshot] ${name}: ${value.count}`);
        return { ...value, error: null };
    } catch (e) {
        console.warn(`[install-snapshot] ${name}: unavailable — ${e.message}`);
        return { count: null, error: e.message };
    }
}

const [nuget, github, docker] = await Promise.all([
    collect('nuget', nugetMax),
    collect('github', githubAssets),
    collect('docker', dockerPulls),
]);

const parts = [nuget, github, docker];
const total = parts.reduce((sum, p) => sum + (p.count || 0), 0);
const available = parts.every((p) => p.count !== null);

const snapshot = {
    generated_at: new Date().toISOString(),
    // False when any source failed: the total is then a floor of a floor,
    // and the page should say so rather than present it as complete.
    available,
    total,
    // The three do not count the same event. A NuGet download is a package
    // pull, a GitHub asset download is an installer fetch, a Docker pull is
    // an image layer fetch that a nightly CI job repeats. The total is a
    // direction, not a headcount — say so wherever it is rendered.
    nuget,
    github,
    docker,
};

await mkdir(dirname(OUT_PATH), { recursive: true });
await writeFile(OUT_PATH, JSON.stringify(snapshot, null, 2) + '\n', 'utf8');
console.log(`[install-snapshot] wrote ${OUT_PATH} (available=${available}, total=${total})`);
