#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// backfill-release-bodies.mjs — one-shot backfill of historical
// GitHub Release bodies from the hand-maintained RELEASE_NOTES.md.
//
// Why: pre-v2.0 the release.yml workflow extracted version blocks from
// RELEASE_NOTES.md at tag time, BUT a regex / format mismatch meant
// the rich editorial content stayed local-only — every historical
// release.body on GitHub is just the placeholder fallback. Going
// forward GH Releases ARE the source of truth (mirrored back to
// RELEASE_NOTES.md by generate-release-notes.mjs); to make that flow
// honest, the historical rich content needs to live on GitHub too.
//
// What it does: parses RELEASE_NOTES.md by ## heading, matches each
// version block to an existing release (tag = first whitespace-token
// of the heading after ##), and PATCHes the release body with the
// block contents. Releases not in RELEASE_NOTES.md are untouched.
// Releases already carrying rich content (>200 chars, no placeholder
// marker) are skipped unless --force is set so the script is safe to
// re-run.
//
// Usage:
//   node scripts/backfill-release-bodies.mjs                 # dry-run (default)
//   node scripts/backfill-release-bodies.mjs --apply         # actually PATCH
//   node scripts/backfill-release-bodies.mjs --apply --force # overwrite even rich bodies

import { execSync } from "node:child_process";
import { readFileSync } from "node:fs";
import process from "node:process";

function resolveToken() {
    if (process.env.GH_TOKEN) return process.env.GH_TOKEN;
    if (process.env.GITHUB_TOKEN) return process.env.GITHUB_TOKEN;
    try { return execSync("gh auth token", { stdio: ["ignore", "pipe", "ignore"] }).toString().trim(); }
    catch { return null; }
}

const TOKEN = resolveToken();
if (!TOKEN) {
    console.error("No GitHub token. Set GH_TOKEN or run `gh auth login` first.");
    process.exit(1);
}

const OWNER = "Kuestenlogik";
const REPO = "Bowire";
const APPLY = process.argv.includes("--apply");
const FORCE = process.argv.includes("--force");
const PLACEHOLDER_MARKER = "See the auto-generated change list below";

async function gh(method, path, body) {
    const res = await fetch(`https://api.github.com${path}`, {
        method,
        headers: {
            "Authorization": `Bearer ${TOKEN}`,
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "Content-Type": "application/json",
            "User-Agent": "bowire-release-backfill",
        },
        body: body ? JSON.stringify(body) : undefined,
    });
    if (!res.ok) {
        console.error(`GitHub ${method} ${path} → HTTP ${res.status}: ${await res.text()}`);
        process.exit(1);
    }
    return res.json();
}

async function fetchAllReleases() {
    const all = [];
    let page = 1;
    while (true) {
        const batch = await gh("GET", `/repos/${OWNER}/${REPO}/releases?per_page=100&page=${page}`);
        if (!Array.isArray(batch) || batch.length === 0) break;
        all.push(...batch);
        if (batch.length < 100) break;
        page++;
    }
    return all;
}

// Parse RELEASE_NOTES.md into [{ tag, body }] entries. Heading shape:
// `## vX.Y.Z(-rc.N) — <date or theme>`. Body runs until the next `## `
// heading or end of file. The standalone `---` separators that frame
// each section are dropped from the captured body.
function parseReleaseNotes(md) {
    const lines = md.split(/\r?\n/);
    const entries = [];
    let current = null;
    for (const line of lines) {
        // Match both `## v1.9.0 — …` (modern) and `## 1.3.0 — …`
        // (older entries that pre-dated the v-prefix convention). Tag
        // gets the `v` prefix added back when missing so it lines up
        // with GitHub's release tag_name.
        const m = line.match(/^##\s+(v?\d[\w.\-]*)/);
        if (m) {
            if (current) entries.push(current);
            const raw = m[1];
            const tag = raw.startsWith("v") ? raw : `v${raw}`;
            current = { tag, headingLine: line, body: [] };
            continue;
        }
        if (!current) continue;
        if (/^---\s*$/.test(line)) continue;
        current.body.push(line);
    }
    if (current) entries.push(current);
    // Trim trailing blank lines per entry; collapse leading blanks.
    for (const e of entries) {
        while (e.body.length && e.body[0].trim() === "") e.body.shift();
        while (e.body.length && e.body[e.body.length - 1].trim() === "") e.body.pop();
        e.bodyText = e.body.join("\n");
    }
    return entries;
}

function hasPlaceholderOnly(body) {
    // softprops/action-gh-release appends the auto-generated 'What's
    // Changed' / 'Full Changelog' sections to whatever editorial body
    // it was given, so the total release.body length includes both.
    // Editorial-empty releases are detected by the placeholder marker
    // appearing in the body — the workflow only writes that string
    // when the awk extract didn't match a version block in
    // RELEASE_NOTES.md. If the marker is present, the rest is just
    // the auto-gen list, and the body counts as "no editorial".
    if (!body) return true;
    return body.includes(PLACEHOLDER_MARKER);
}

async function main() {
    const md = readFileSync("RELEASE_NOTES.md", "utf8");
    const entries = parseReleaseNotes(md);
    const releases = await fetchAllReleases();
    const releaseByTag = new Map(releases.map(r => [r.tag_name, r]));

    let updated = 0;
    let skipped = 0;
    let missing = 0;
    let untouched = 0;

    for (const entry of entries) {
        const rel = releaseByTag.get(entry.tag);
        if (!rel) {
            console.log(`MISS  ${entry.tag.padEnd(12)} — no matching release on GitHub`);
            missing++;
            continue;
        }
        if (!FORCE && !hasPlaceholderOnly(rel.body)) {
            console.log(`SKIP  ${entry.tag.padEnd(12)} — release body already populated (${rel.body.length} chars)`);
            untouched++;
            continue;
        }
        if (!entry.bodyText.trim()) {
            console.log(`SKIP  ${entry.tag.padEnd(12)} — RELEASE_NOTES.md block is empty`);
            skipped++;
            continue;
        }
        console.log(`PATCH ${entry.tag.padEnd(12)} (${entry.bodyText.length} chars)${APPLY ? "" : " — dry-run"}`);
        if (APPLY) {
            await gh("PATCH", `/repos/${OWNER}/${REPO}/releases/${rel.id}`, { body: entry.bodyText });
            updated++;
        } else {
            updated++;
        }
    }

    console.log("");
    console.log(`Summary: ${updated} ${APPLY ? "updated" : "would update"} · ${untouched} skipped (already populated) · ${skipped} skipped (empty block) · ${missing} no matching release`);
    if (!APPLY) console.log("Re-run with --apply to write changes.");
}

main().catch(err => { console.error(err); process.exit(1); });
